/// What changed between a machine's previous snapshot and this one. Pure: Storage writes the
/// drafts, and handles commit_seen itself because deduplication needs the database.
module Harbor.Events

open System
open System.IO
open System.Text.Json
open Harbor.Domain

type Draft =
    { Kind: string
      RepoPath: string option
      Subject: string option
      Data: string } // JSON object

let private draft kind repoPath subject (data: obj) =
    { Kind = kind; RepoPath = repoPath; Subject = subject; Data = JsonSerializer.Serialize data }

let private dirty (r: Repo) =
    r.Changes |> Option.exists (fun c -> c.Staged + c.Unstaged + c.Untracked + c.Conflicted > 0)

let private aheadBehind (r: Repo) =
    r.Branch |> Option.map (fun b -> b.Ahead, b.Behind) |> Option.defaultValue (0, 0)

let private head (r: Repo) = r.Branch |> Option.bind _.Head

let private isUnder (root: string) (path: string) =
    let root = Path.TrimEndingDirectorySeparator root
    path = root || path.StartsWith(root + string Path.DirectorySeparatorChar) || path.StartsWith(root + "/")

let private repoChanges (prev: Repo) (next: Repo) =
    let at = Some next.Path
    [ match dirty prev, dirty next with
      | false, true -> draft "dirty" at None {| paths = next.Changes |> Option.map _.Paths |> Option.defaultValue [||] |}
      | true, false -> draft "clean" at None {||}
      | _ -> ()
      let (a0, b0), (a1, b1) = aheadBehind prev, aheadBehind next
      if (a0, b0) <> (a1, b1) then
          draft "ahead_behind" at None {| ahead = a1; behind = b1; prevAhead = a0; prevBehind = b0 |}
      if head prev <> head next then
          draft "branch_changed" at (head next) {| from = head prev; ``to`` = head next |} ]

let private repoEvents (prev: Snapshot) (next: Snapshot) =
    let before = prev.Repos |> Array.map (fun r -> r.Path, r) |> Map.ofArray
    let after = next.Repos |> Array.map (fun r -> r.Path, r) |> Map.ofArray
    [ for r in next.Repos do
          match before.TryFind r.Path with
          | None -> draft "repo_added" (Some r.Path) None {| name = r.Name |}
          | Some p -> yield! repoChanges p r
      for r in prev.Repos do
          let rootPresent = next.Machine.Roots |> Array.exists (fun root -> isUnder root r.Path)
          if not (after.ContainsKey r.Path) && rootPresent then
              draft "repo_removed" (Some r.Path) None {| name = r.Name |} ]

let private serviceEvents (prev: Snapshot) (next: Snapshot) =
    let before = prev.Services |> Array.map (fun s -> s.Name, s.State) |> Map.ofArray
    [ for s in next.Services do
          match before.TryFind s.Name, s.State with
          | Some Running, Stopped -> draft "service_down" None (Some s.Name) {| detail = s.Detail |}
          | Some Stopped, Running -> draft "service_up" None (Some s.Name) {| detail = s.Detail |}
          | _ -> () ]

let private roleEvents (prev: Snapshot) (next: Snapshot) =
    match prev.Role, next.Role with
    | Some a, Some b when a.Name <> b.Name -> [ draft "role_changed" None (Some b.Name) {| from = a.Name; ``to`` = b.Name; work = b.Work; by = b.By |} ]
    | None, Some b when b.By <> "default" -> [ draft "role_changed" None (Some b.Name) {| from = None; ``to`` = b.Name; work = b.Work; by = b.By |} ]
    | _ -> []

/// The first snapshot is a baseline: no events (commits are still recorded).
let diff (prev: Snapshot option) (next: Snapshot) : Draft list =
    match prev with
    | None -> []
    | Some p -> repoEvents p next @ serviceEvents p next @ roleEvents p next
