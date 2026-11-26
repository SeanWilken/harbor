/// The releases page: every machine's release units, with the recipe their repo uses and their
/// last runs. Recipes are keyed by repo (remote) and unit folder, so clones share one.
module Harbor.ReleaseView

open System
open Harbor.Domain
open Harbor.Storage

type UnitView =
    { MachineId: int64
      Machine: string
      RepoName: string
      RepoPath: string
      RepoKey: string
      Unit: ReleaseUnit
      Recipe: Recipe
      /// False when the recipe is harbor's default, not one someone saved.
      Saved: bool
      /// The last release (not a status check), and the last migration status check.
      LastRun: JobInfo option
      LastStatus: JobInfo option }

/// Profile to use when none was saved: none if some service runs without one, else "all" or the first.
let defaultProfile (u: ReleaseUnit) =
    if u.Services |> Array.exists (fun s -> s.Profiles.Length = 0) || u.Profiles.Length = 0 then None
    elif Array.contains "all" u.Profiles then Some "all"
    else Some u.Profiles[0]

let defaultRecipe (key: string) (u: ReleaseUnit) : Recipe =
    { RepoKey = key; Unit = u.Path; Profile = defaultProfile u; Steps = u.Steps; UpdatedAt = DateTimeOffset.MinValue; UpdatedBy = "" }

/// Which unit a release job was for (its repo path, and the unit inside its request).
let jobUnit (j: JobInfo) =
    match j.Path, j.Arg |> Option.map Releases.decode with
    | Some p, Some(Ok r) -> Some(p, r)
    | _ -> None

let build (machines: MachineSummary[]) (snaps: Map<int64, Snapshot>) (recipes: Recipe[]) (jobs: (int64 * JobInfo)[]) : UnitView[] =
    let saved = recipes |> Array.map (fun r -> (r.RepoKey, r.Unit), r) |> Map.ofArray
    [| for m in machines |> Array.filter (fun m -> m.Status = "approved") |> Array.sortBy _.Name do
           match snaps.TryFind m.Id with
           | Some s ->
               for u in defaultArg s.Releases [||] |> Array.filter (fun u -> not (Array.contains u.Repo m.Hidden)) do
                   let repo = s.Repos |> Array.tryFind (fun r -> r.Path = u.Repo)
                   let key = repo |> Option.map (repoKey m.Id) |> Option.defaultValue $"local:{m.Id}:{u.Repo}"
                   let runs =
                       jobs
                       |> Array.filter (fun (id, j) -> id = m.Id && (jobUnit j |> Option.exists (fun (p, r) -> p = u.Repo && r.Unit = u.Path)))
                       |> Array.map snd
                   let isStatus (j: JobInfo) = jobUnit j |> Option.exists (fun (_, r) -> r.Steps = [| "status" |])
                   yield
                       { MachineId = m.Id
                         Machine = m.Name
                         RepoName = repo |> Option.map _.Name |> Option.defaultValue (IO.Path.GetFileName u.Repo |> string)
                         RepoPath = u.Repo
                         RepoKey = key
                         Unit = u
                         Recipe = saved.TryFind(key, u.Path) |> Option.defaultValue (defaultRecipe key u)
                         Saved = saved.ContainsKey(key, u.Path)
                         LastRun = runs |> Array.tryFind (isStatus >> not)
                         LastStatus = runs |> Array.tryFind isStatus }
           | None -> () |]

/// What a viewer sees: the units and whether runs worked, not their logs or who ran them.
let forViewer (v: UnitView) =
    let strip (j: JobInfo) = { j with Output = None; RequestedBy = "" }
    { v with LastRun = v.LastRun |> Option.map strip; LastStatus = v.LastStatus |> Option.map strip }

/// A recipe's steps must be ones harbor knows, in run order, at least one.
let checkRecipe (steps: string[]) : Result<string[], string> =
    let unknown = steps |> Array.filter (fun s -> not (Array.contains s Releases.stepOrder))
    if steps.Length = 0 then Error "a recipe needs at least one step"
    elif unknown.Length > 0 then Error $"""unknown steps: {String.Join(", ", unknown)}"""
    else Ok(Releases.stepOrder |> Array.filter (fun s -> Array.contains s steps))
