/// Holds the latest repo scan. Repos are rescanned on a timer or on demand;
/// services, resources and networks are cheap, so they are read fresh on every snapshot.
module Harbor.Inventory

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Harbor.Config
open Harbor.Domain

type Scanned = { Repo: Repo; Commits: LoggedCommit[] }

let private scanOne (path: string) : Task<Scanned> =
    task {
        let repo = Git.scan path
        let commits = Git.recentCommits path
        let! repo = repo
        let! commits = commits
        return { Repo = repo; Commits = commits }
    }

let scanRepos (cfg: Config) : Task<Scanned[]> =
    task {
        let paths = Discovery.find cfg.Ignore cfg.SkipHidden cfg.MaxDepth cfg.Roots
        let results = Array.zeroCreate paths.Length
        let opts = ParallelOptions(MaxDegreeOfParallelism = 8)
        do! Parallel.ForEachAsync(Array.indexed paths, opts, fun (i, p) _ ->
                ValueTask(task { let! r = scanOne p in results[i] <- r }))
        return results
    }

/// Repos worth fetching: has a remote, and isn't a worktree (it shares its main repo's refs).
let fetchable (repos: Repo[]) =
    repos |> Array.filter (fun r -> r.Remotes.Length > 0 && r.WorktreeOf.IsNone) |> Array.map _.Path

type Store(cfg: Config) =
    let gate = new SemaphoreSlim(1, 1)
    let fetchGate = new SemaphoreSlim(1, 1)
    let sampler = Resources.Sampler()
    let mutable manifest: Manifest option = None
    let mutable scanned: Scanned[] = [||]
    let mutable fetchErrors: Map<string, string> = Map.empty
    let mutable nextScan = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable machine =
        { Hostname = Environment.MachineName
          Os = Manifest.description ()
          ScannedAt = DateTimeOffset.MinValue
          ScanMs = 0L
          Roots = [||] }

    member _.Config = cfg

    /// Completes when the scan after this call finishes (the agent pushes then).
    member _.NextScan: Task = nextScan.Task

    /// Concurrent callers wait for the scan in flight rather than starting another.
    member _.Rescan() : Task<unit> =
        task {
            do! gate.WaitAsync()
            try
                let sw = Stopwatch.StartNew()
                let! r = scanRepos cfg
                scanned <- r
                machine <-
                    { machine with
                        ScannedAt = DateTimeOffset.Now
                        ScanMs = sw.ElapsedMilliseconds
                        Roots = cfg.Roots |> Array.filter Directory.Exists }
                let done' = nextScan
                nextScan <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                done'.TrySetResult() |> ignore
            finally
                gate.Release() |> ignore
        }

    member _.Snapshot() : Task<Snapshot> =
        task {
            let services = Services.checkAll cfg.Services
            let resources = sampler.Sample()
            let networks = Network.interfaces ()
            let! services = services
            let! resources = resources
            let! networks = networks
            let repos = scanned |> Array.map (fun s -> { s.Repo with FetchError = fetchErrors.TryFind s.Repo.Path })
            return
                { Machine = machine
                  Repos = repos
                  Services = services
                  Resources = Some resources
                  Networks = Some networks
                  Access = (if cfg.Access.Length = 0 then None else Some cfg.Access) }
        }

    /// Fetch one repo (by path) or every fetchable repo, then rescan so ahead/behind reflects it.
    /// Returns the failures; Error if the path isn't a fetchable repo on this machine.
    member this.Fetch(path: string option) : Task<Result<(string * string)[], string>> =
        task {
            let candidates = fetchable (scanned |> Array.map _.Repo)
            let targets =
                match path with
                | None -> Ok candidates
                | Some p when Array.contains p candidates -> Ok [| p |]
                | Some p -> Error $"not a fetchable repo here: {p}"
            match targets with
            | Error e -> return Error e
            | Ok targets ->
                do! fetchGate.WaitAsync()
                try
                    let results = Array.zeroCreate targets.Length
                    let opts = ParallelOptions(MaxDegreeOfParallelism = 4)
                    do! Parallel.ForEachAsync(Array.indexed targets, opts, fun (i, p) _ ->
                            ValueTask(task { let! r = Git.fetch p in results[i] <- (p, r) }))
                    for p, r in results do
                        fetchErrors <-
                            match r with
                            | Ok() -> fetchErrors.Remove p
                            | Error e -> fetchErrors.Add(p, e)
                    do! this.Rescan()
                    return Ok(results |> Array.choose (fun (p, r) -> match r with Error e -> Some(p, e) | Ok() -> None))
                finally
                    fetchGate.Release() |> ignore
        }

    /// The latest installed-software manifest, once built.
    member _.Manifest = manifest

    member _.RefreshManifest() : Task<Manifest> =
        task {
            let! m = Harbor.Manifest.collect (scanned |> Array.map _.Repo)
            manifest <- Some m
            return m
        }

    member _.Commits: RepoCommits[] =
        scanned |> Array.map (fun s -> { Path = s.Repo.Path; Commits = s.Commits })
