/// Pushes this machine's snapshot to the hub every PushSeconds and after each rescan,
/// backing off while the hub is unreachable. Only the newest snapshot is ever sent.
module Harbor.Agent

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Harbor.Domain

type Sink = Push -> Task<Result<PushReply, string>>

let httpSink (hubUrl: string) : Sink =
    let http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.)
    fun push ->
        task {
            try
                use! r = http.PostAsJsonAsync($"{hubUrl}/api/push", push, Config.jsonOptions)
                let! body = r.Content.ReadAsStringAsync()
                if r.IsSuccessStatusCode then
                    match JsonSerializer.Deserialize<PushReply>(body, Config.jsonOptions) with
                    | null -> return Error "hub sent an empty reply"
                    | reply -> return Ok reply
                else
                    return Error $"hub answered {int r.StatusCode}: {body.Trim()}"
            with ex ->
                return Error(ex.GetBaseException().Message)
        }

let private heads (snap: Snapshot) =
    snap.Repos |> Array.map (fun r -> r.Path, r.Branch |> Option.bind _.Oid) |> Map.ofArray

/// Commits for repos whose HEAD differs from what the hub last stored. After an agent restart
/// everything is resent once; the hub deduplicates by (remote, sha).
let pendingCommits (acked: Map<string, string option>) (snap: Snapshot) (commits: RepoCommits[]) =
    let now = heads snap
    commits
    |> Array.filter (fun rc ->
        rc.Commits.Length > 0
        && (match now.TryFind rc.Path, acked.TryFind rc.Path with
            | Some head, Some sent -> head <> sent
            | _ -> true))

/// 5s, 10s, 20s ... capped at 5 min, with ±20% jitter so agents don't retry in lockstep.
let backoff (failures: int) =
    let secs = min 300. (5. * Math.Pow(2., float (max 0 (failures - 1))))
    TimeSpan.FromSeconds(secs * (0.8 + Random.Shared.NextDouble() * 0.4))

/// Run one job if this machine allows its kind. Never throws: failures become a failed result.
let runJob (store: Inventory.Store) (allow: string[]) (job: JobRequest) : Task<JobResult> =
    task {
        let finish ok output = { Id = job.Id; Ok = ok; Output = output; FinishedAt = DateTimeOffset.Now }
        if not (Array.contains job.Kind allow) then
            return finish false $"'{job.Kind}' is not allowed on this machine (agent.allow)"
        else
            try
                match job.Kind with
                | "fetch" ->
                    match! store.Fetch job.Path with
                    | Error e -> return finish false e
                    | Ok [||] -> return finish true (match job.Path with Some p -> $"fetched {p}" | None -> "fetched all repos")
                    | Ok failures ->
                        let lines = failures |> Array.map (fun (p, e) -> $"{p}: {e}")
                        return finish false (String.Join("\n", lines))
                | other -> return finish false $"unknown job kind '{other}'"
            with ex ->
                return finish false ex.Message
    }

/// Wakes the push loop early (a job finished, or the hub queued one for the in-process agent).
type Waker() =
    let mutable tcs = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let gate = obj ()
    member _.Next = lock gate (fun () -> tcs.Task)
    member _.Wake() =
        lock gate (fun () ->
            let t = tcs
            tcs <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            t.TrySetResult() |> ignore)

let run (store: Inventory.Store) (name: string) (pushSeconds: int) (allow: string[]) (waker: Waker) (sink: Sink) (reporter: AgentStatus.Reporter) (stop: CancellationToken) : Task =
    task {
        let period = TimeSpan.FromSeconds(float (max 10 pushSeconds))
        let gate = obj ()
        let mutable acked = Map.empty
        let mutable failures = 0
        let mutable lastStatus = ""
        let mutable started = Set.empty<int64> // never run a job twice, even if the hub resends it
        let mutable results: JobResult list = []
        let mutable hubManifest: string option = None // hash the hub says it holds
        let start (job: JobRequest) =
            task {
                let! r = runJob store allow job
                lock gate (fun () -> results <- r :: results)
                waker.Wake()
            }
            |> ignore
        do! store.NextScan.WaitAsync stop // nothing to push before the first scan
        while not stop.IsCancellationRequested do
            let nextScan = store.NextScan
            let woken = waker.Next
            let! snap = store.Snapshot()
            let sending = lock gate (fun () -> results)
            let push =
                { Version = 1
                  Machine = name
                  PushSeconds = pushSeconds
                  Snapshot = snap
                  Commits = pendingCommits acked snap store.Commits
                  JobResults = if sending.IsEmpty then None else Some(Array.ofList sending)
                  // Only approved machines' manifests are kept; pending ones would resend 100 KB each push.
                  Manifest = store.Manifest |> Option.filter (fun m -> lastStatus = "approved" && hubManifest <> Some m.Hash)
                  PairCode = if lastStatus = "approved" then None else AgentStatus.pendingPairCode name
                  AgentCommit = BuildInfo.commit }
            match! sink push with
            | Ok reply ->
                if failures > 0 || reply.Status <> lastStatus then
                    eprintfn $"""push: hub says {reply.Status}{reply.Message |> Option.map ((+) ", ") |> Option.defaultValue ""}"""
                failures <- 0
                lastStatus <- reply.Status
                if reply.Status = "approved" then
                    acked <- heads snap
                    AgentStatus.clearPairCode name
                hubManifest <- reply.ManifestHash
                // A rejected code is dropped, so a typo doesn't keep failing (and count against the limit).
                if reply.PairResult = Some "rejected" then AgentStatus.clearPairCode name
                reporter.Pushed(reply.Status, (AgentStatus.pendingPairCode name).IsSome, reply.Update)
                if reply.PairResult = Some "rejected" then
                    reporter.Failed "pairing code not accepted (wrong, used or expired): get a new one from the hub"
                let sentIds = sending |> List.map _.Id |> Set.ofList
                lock gate (fun () -> results <- results |> List.filter (fun r -> not (sentIds.Contains r.Id)))
                for job in reply.Jobs |> Option.defaultValue [||] do
                    if not (started.Contains job.Id) then
                        started <- started.Add job.Id
                        eprintfn $"""job {job.Id}: {job.Kind} {defaultArg job.Path "(all)"}"""
                        start job
            | Error e ->
                failures <- failures + 1
                eprintfn $"push failed ({failures}): {e}"
                reporter.Failed e
            let wait = if failures = 0 then period else backoff failures
            try
                let! _ = Task.WhenAny(Task.Delay(wait, stop), nextScan, woken)
                ()
            with :? OperationCanceledException ->
                ()
    }

/// `git fetch` every fetchable repo after the first scan, then every `minutes`.
let fetchLoop (store: Inventory.Store) (minutes: int) (stop: CancellationToken) : Task =
    task {
        if minutes > 0 then
            try
                do! store.NextScan.WaitAsync stop
                while not stop.IsCancellationRequested do
                    match! store.Fetch None with
                    | Ok [||] -> ()
                    | Ok failed -> eprintfn $"fetch: {failed.Length} repo(s) failed, e.g. {fst failed[0]}: {snd failed[0]}"
                    | Error e -> eprintfn $"fetch: {e}"
                    do! Task.Delay(TimeSpan.FromMinutes(float minutes), stop)
            with :? OperationCanceledException ->
                ()
    }

/// Rebuild the manifest after the first scan, then every `hours`; wake the push loop when it changed.
let manifestLoop (store: Inventory.Store) (hours: int) (waker: Waker) (stop: CancellationToken) : Task =
    task {
        if hours > 0 then
            try
                do! store.NextScan.WaitAsync stop
                while not stop.IsCancellationRequested do
                    let before = store.Manifest |> Option.map _.Hash
                    try
                        let! m = store.RefreshManifest()
                        if before <> Some m.Hash then waker.Wake()
                    with ex ->
                        eprintfn $"manifest: {ex.Message}"
                    do! Task.Delay(TimeSpan.FromHours(float hours), stop)
            with :? OperationCanceledException ->
                ()
    }

/// Rescan repos now, then every RescanSeconds until shutdown.
let rescanLoop (store: Inventory.Store) (stop: CancellationToken) : Task =
    task {
        let period = TimeSpan.FromSeconds(float (max 30 store.Config.RescanSeconds))
        while not stop.IsCancellationRequested do
            try
                do! store.Rescan()
            with ex ->
                eprintfn $"scan failed: {ex.Message}"
            try
                do! Task.Delay(period, stop)
            with :? OperationCanceledException ->
                ()
    }

/// Everything an agent runs: rescans, fetches, manifests and the push loop. Returns the store and
/// the status reporter (give its Linked to the hub link).
let start (cfg: Config.Config) (waker: Waker) (sink: Sink) (stop: CancellationToken) : Inventory.Store * AgentStatus.Reporter =
    let store = Inventory.Store cfg
    let pushSeconds = cfg.Agent |> Option.map _.PushSeconds |> Option.defaultValue 300
    let reporter = AgentStatus.Reporter(cfg.Machine, (cfg.Agent |> Option.map _.Hub |> Option.defaultValue "local"), pushSeconds)
    let allow = cfg.Agent |> Option.map _.Allow |> Option.defaultValue Config.defaultAllow
    rescanLoop store stop |> ignore
    fetchLoop store cfg.FetchMinutes stop |> ignore
    manifestLoop store cfg.ManifestHours waker stop |> ignore
    run store cfg.Machine pushSeconds allow waker sink reporter stop |> ignore
    store, reporter

/// What a hub wake asks for. A refresh rescans first; the push follows the scan by itself.
let onWake (store: Inventory.Store) (waker: Waker) (reason: string) =
    if reason = "refresh" then store.Rescan() |> ignore else waker.Wake()
