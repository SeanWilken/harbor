/// GET /api/report: the whole fleet's work state in one document, for people and for agents
/// deciding what to look at next. Built only from what the machines last reported; every clone
/// says how fresh that is. Facts and attention items, never plans: harbor doesn't know which agent
/// owns a branch or what a task is for.
module Harbor.Report

open System
open Harbor.Domain
open Harbor.Storage

/// Bumped when a field changes meaning or goes away; new fields don't bump it.
[<Literal>]
let Version = 1

type Attention =
    { /// conflicted | dirty | unpushed | diverged | behind | fetch_failed | gone_upstream | stashes | no_remote | stale_report
      Kind: string
      /// critical | warn | info
      Severity: string
      Repo: string
      Machine: string
      Path: string
      Text: string
      /// When this started, when known (oldest unpushed commit, behind since, last change).
      Since: DateTimeOffset option }

type BranchState = { Name: string; Upstream: string option; Ahead: int; Behind: int; Gone: bool; LastCommitAt: DateTimeOffset option }

type Clone =
    { Machine: string
      MachineId: int64
      Path: string
      /// None when detached.
      Branch: string option
      Head: string option
      Upstream: string option
      Ahead: int
      Behind: int
      BehindSince: DateTimeOffset option
      Staged: int
      Unstaged: int
      Untracked: int
      Conflicted: int
      Unpushed: int
      OldestUnpushedAt: DateTimeOffset option
      Stashes: int
      LastCommitAt: DateTimeOffset option
      LastCommit: string option
      LastModifiedAt: DateTimeOffset option
      LastFetchAt: DateTimeOffset option
      FetchError: string option
      /// The main working tree, when this clone is a linked worktree.
      WorktreeOf: string option
      /// The path or branch follows an agent's naming (.claude/worktrees, claude/…, codex/…). A
      /// naming hint only: it says nothing about which agent or task, or whether it's still active.
      AgentHint: bool
      Branches: BranchState[]
      /// When this machine last reported; the clone is as old as this.
      ReportedAt: DateTimeOffset option
      /// The machine's report is older than three push intervals.
      Stale: bool }

type RepoReport =
    { /// The remote key (host/owner/name), or machine:path for a repo with no remote.
      Key: string
      Name: string
      /// host/owner/name of the primary remote; never the raw URL, which may carry a token.
      Remote: string option
      Clones: Clone[]
      /// Whether every clone has the same commit checked out; None with a single clone.
      SameHead: bool option
      Attention: Attention[] }

type MachineReport =
    { Id: int64
      Name: string
      Os: string option
      Online: bool
      Linked: bool
      ReportedAt: DateTimeOffset option
      Role: string option
      Work: string option
      Available: string
      Repos: int
      AgentCommit: string option }

type AlertBrief = { Id: int64; Rule: string; Severity: string; Title: string; OpenedAt: DateTimeOffset; Seen: bool; Open: bool }

type Report =
    { Version: int
      GeneratedAt: DateTimeOffset
      Machines: MachineReport[]
      Repos: RepoReport[]
      /// Every repo's attention items in one list: critical first, then the longest-standing.
      Attention: Attention[]
      /// Unseen or still-open alerts.
      Alerts: AlertBrief[] }

let private severityRank = function "critical" -> 0 | "warn" -> 1 | _ -> 2

let private agentPath (path: string) (branch: string option) =
    let p = path.Replace('\\', '/')
    p.Contains "/.claude/worktrees/" || p.Contains "/.codex/" || p.Contains "/worktrees/"
    || branch |> Option.exists (fun b -> b.StartsWith "claude/" || b.StartsWith "codex/" || b.StartsWith "agent/")

let clone (m: MachineSummary) (stale: bool) (r: Repo) : Clone =
    let changes = r.Changes
    let inFlight = r.InFlight
    let branch = r.Branch |> Option.bind _.Head
    { Machine = m.Name
      MachineId = m.Id
      Path = r.Path
      Branch = branch
      Head = r.Branch |> Option.bind _.Oid
      Upstream = r.Branch |> Option.bind _.Upstream
      Ahead = r.Branch |> Option.map _.Ahead |> Option.defaultValue 0
      Behind = r.Branch |> Option.map _.Behind |> Option.defaultValue 0
      BehindSince = r.BehindSince
      Staged = changes |> Option.map _.Staged |> Option.defaultValue 0
      Unstaged = changes |> Option.map _.Unstaged |> Option.defaultValue 0
      Untracked = changes |> Option.map _.Untracked |> Option.defaultValue 0
      Conflicted = changes |> Option.map _.Conflicted |> Option.defaultValue 0
      Unpushed = inFlight |> Option.map _.UnpushedCount |> Option.defaultValue 0
      OldestUnpushedAt = inFlight |> Option.bind (fun f -> Array.tryLast f.Unpushed) |> Option.map _.Date
      Stashes = inFlight |> Option.map _.Stashes |> Option.defaultValue 0
      LastCommitAt = r.LastCommit |> Option.map _.Date
      LastCommit = r.LastCommit |> Option.map _.Subject
      LastModifiedAt = r.LastModified
      LastFetchAt = r.LastFetch
      FetchError = r.FetchError
      WorktreeOf = r.WorktreeOf
      AgentHint = agentPath r.Path branch
      Branches =
        r.Branches
        |> Option.defaultValue [||]
        |> Array.map (fun b -> { Name = b.Name; Upstream = b.Upstream; Ahead = b.Ahead; Behind = b.Behind; Gone = b.Gone; LastCommitAt = b.Date })
      ReportedAt = m.LastPushAt
      Stale = stale }

/// What in this clone needs a person (or an agent) to act; empty when it's clean and current.
let attention (now: DateTimeOffset) (repo: string) (hasRemote: bool) (c: Clone) : Attention[] =
    let item kind severity text since =
        { Kind = kind; Severity = severity; Repo = repo; Machine = c.Machine; Path = c.Path; Text = text; Since = since }
    let plural n word = if n = 1 then $"1 {word}" else $"{n} {word}s"
    let olderThan days (t: DateTimeOffset option) = t |> Option.exists (fun t -> (now - t).TotalDays > days)
    let dirtyFiles = c.Staged + c.Unstaged + c.Untracked
    let gone = c.Branches |> Array.filter _.Gone
    let upstream = defaultArg c.Upstream "upstream"
    [| if c.Stale then item "stale_report" "warn" "the machine hasn't reported recently; this is its last known state" c.ReportedAt
       if c.Conflicted > 0 then item "conflicted" "critical" $"""{plural c.Conflicted "conflicted file"}: a merge or rebase is stuck""" c.LastModifiedAt
       if dirtyFiles > 0 then
           let sev = if olderThan 7. c.LastModifiedAt then "warn" else "info"
           item "dirty" sev $"""{plural dirtyFiles "uncommitted file"} ({c.Staged} staged, {c.Unstaged} changed, {c.Untracked} new)""" c.LastModifiedAt
       if c.Unpushed > 0 then
           let sev = if olderThan 3. c.OldestUnpushedAt then "warn" else "info"
           item "unpushed" sev $"""{plural c.Unpushed "commit"} on no remote""" c.OldestUnpushedAt
       if c.Ahead > 0 && c.Behind > 0 then item "diverged" "warn" $"{c.Ahead} ahead and {c.Behind} behind {upstream}: needs a rebase or merge" c.BehindSince
       elif c.Behind > 0 then
           let sev = if olderThan 7. c.BehindSince then "warn" else "info"
           item "behind" sev $"""{plural c.Behind "commit"} behind {upstream}""" c.BehindSince
       if c.FetchError.IsSome then item "fetch_failed" "warn" $"fetch failed: {c.FetchError.Value}" c.LastFetchAt
       if gone.Length > 0 then
           let names = String.Join(", ", gone |> Array.map _.Name)
           item "gone_upstream" "info" $"""{plural gone.Length "branch"} whose remote branch was deleted: {names}""" None
       if c.Stashes > 0 then item "stashes" "info" (plural c.Stashes "stash") None
       if not hasRemote then item "no_remote" "info" "no remote: this clone is the only copy" None |]

let private byImportance (xs: Attention seq) =
    xs
    |> Seq.sortBy (fun a -> severityRank a.Severity, a.Since |> Option.defaultValue DateTimeOffset.MaxValue)
    |> Seq.toArray

/// `snapshots`: each approved machine's latest report (already redacted for viewers).
let build (now: DateTimeOffset) (machines: MachineSummary[]) (snapshots: Map<int64, Snapshot>) (alerts: Alert[]) : Report =
    let approved = machines |> Array.filter (fun m -> m.Status = "approved")
    let available = availability now approved |> Array.map (fun a -> a.Machine, a.Available) |> Map.ofArray
    let clones =
        [ for m in approved do
              match snapshots.TryFind m.Id with
              | Some s ->
                  for r in s.Repos do
                      if not (Array.contains r.Path m.Hidden) then
                          let remote = GitParse.primaryRemote r.Remotes |> Option.bind (fun x -> GitParse.remoteKey x.Url)
                          yield remote, r.Name, clone m (not (fresh now m)) r
              | None -> () ]
    let repos =
        clones
        |> List.groupBy (fun (remote, _, c) -> match remote with Some k -> k | None -> $"{c.Machine}:{c.Path}")
        |> List.map (fun (key, xs) ->
            let remote = xs |> List.tryPick (fun (r, _, _) -> r)
            let cs = xs |> List.map (fun (_, _, c) -> c) |> List.sortBy (fun c -> c.Machine, c.Path) |> Array.ofList
            let name = xs |> List.head |> fun (_, n, _) -> n
            { Key = key
              Name = name
              Remote = remote
              Clones = cs
              SameHead = (if cs.Length < 2 then None else Some(cs |> Array.map _.Head |> Array.distinct |> Array.length = 1))
              Attention = cs |> Seq.collect (attention now name remote.IsSome) |> byImportance })
        |> List.sortBy (fun r -> (if r.Attention.Length = 0 then 1 else 0), r.Name.ToLowerInvariant())
        |> Array.ofList
    { Version = Version
      GeneratedAt = now
      Machines =
        approved
        |> Array.map (fun m ->
            { Id = m.Id; Name = m.Name; Os = m.Os; Online = fresh now m; Linked = m.Connected; ReportedAt = m.LastPushAt
              Role = m.Role; Work = m.Work; Available = available.TryFind m.Name |> Option.defaultValue "unknown"
              Repos = m.Repos; AgentCommit = m.AgentCommit })
      Repos = repos
      Attention = repos |> Seq.collect _.Attention |> byImportance
      Alerts =
        alerts
        |> Array.filter (fun a -> a.AckedAt.IsNone || a.ResolvedAt.IsNone)
        |> Array.map (fun a ->
            { Id = a.Id; Rule = a.Rule; Severity = a.Severity; Title = a.Title; OpenedAt = a.OpenedAt
              Seen = a.AckedAt.IsSome; Open = a.ResolvedAt.IsNone }) }

/// Only repos that need something, or only one machine's clones (and their attention).
let filter (machine: string option) (attentionOnly: bool) (r: Report) : Report =
    let repos =
        r.Repos
        |> Array.choose (fun repo ->
            match machine with
            | None -> Some repo
            | Some name ->
                match repo.Clones |> Array.filter (fun c -> c.Machine = name) with
                | [||] -> None
                | cs -> Some { repo with Clones = cs; Attention = repo.Attention |> Array.filter (fun a -> a.Machine = name) })
        |> Array.filter (fun repo -> not attentionOnly || repo.Attention.Length > 0)
    { r with
        Machines = (match machine with Some name -> r.Machines |> Array.filter (fun m -> m.Name = name) | None -> r.Machines)
        Repos = repos
        Attention = repos |> Seq.collect _.Attention |> byImportance }

let private day (t: DateTimeOffset) = t.ToString "yyyy-MM-dd"

let private ago (now: DateTimeOffset) (t: DateTimeOffset) =
    let d = now - t
    if d.TotalDays >= 2. then $"{int d.TotalDays}d"
    elif d.TotalHours >= 1. then $"{int d.TotalHours}h"
    else $"{max 0 (int d.TotalMinutes)}m"

/// A compact text form for an agent's context or a chat: machines, then what needs attention.
let markdown (r: Report) : string =
    let sb = Text.StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore
    line $"# Work state ({r.GeneratedAt:``yyyy-MM-dd HH:mm zzz``}, report v{r.Version})"
    line ""
    line "| machine | online | role | available | last report |"
    line "| --- | --- | --- | --- | --- |"
    for m in r.Machines do
        let role = match m.Role, m.Work with Some n, Some w -> $"{n} ({w})" | _ -> "–"
        let last = m.ReportedAt |> Option.map (fun t -> ago r.GeneratedAt t + " ago") |> Option.defaultValue "never"
        line $"""| {m.Name} | {(if m.Online then "yes" else "no")} | {role} | {m.Available} | {last} |"""
    let open' = r.Alerts |> Array.filter _.Open
    if open'.Length > 0 then
        line ""
        line "## Open alerts"
        for a in open' do line $"""- [{a.Severity}] {a.Title}{(if a.Seen then " (seen)" else "")}"""
    let needing = r.Repos |> Array.filter (fun x -> x.Attention.Length > 0)
    line ""
    line $"## Needs attention ({needing.Length} of {r.Repos.Length} repos)"
    for repo in needing do
        let remote = repo.Remote |> Option.map (fun x -> $" ({x})") |> Option.defaultValue ""
        line ""
        line $"### {repo.Name}{remote}"
        for a in repo.Attention do
            let since = a.Since |> Option.map (fun t -> $", since {day t}") |> Option.defaultValue ""
            let branch =
                repo.Clones
                |> Array.tryFind (fun c -> c.Machine = a.Machine && c.Path = a.Path)
                |> Option.bind _.Branch
                |> Option.map (fun b -> $" on {b}")
                |> Option.defaultValue ""
            line $"- [{a.Severity}] {a.Machine}{branch}: {a.Text}{since} (`{a.Path}`)"
    let clean = r.Repos.Length - needing.Length
    if clean > 0 then
        line ""
        line $"{clean} other repos are clean and current."
    sb.ToString()
