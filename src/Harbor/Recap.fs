/// The weekly recap: what fell behind, what's neglected, what changed and where machines differ.
/// Pure: the hub gathers snapshots, event-derived "since" times and manifests, this decides.
module Harbor.Recap

open System
open System.Text
open Harbor.Domain

/// Thresholds and the recap weekday (hub.recap in the config).
type Settings = Config.RecapConfig

let defaults = Config.defaultRecap

/// A repo as the hub knows it, with transition times from its event history (None = it was
/// already that way when harbor first saw it).
type RepoState =
    { Machine: string
      Repo: Repo
      BehindSince: DateTimeOffset option
      DirtySince: DateTimeOffset option }

type MachineManifests =
    { Machine: string
      /// Newest manifest.
      Latest: Manifest option
      /// Newest manifest taken at or before the period start: the baseline for "changed this week".
      AtStart: Manifest option }

type RepoNote =
    { Machine: string
      Name: string
      Path: string
      Detail: string
      /// When it started (or the best available time).
      Since: DateTimeOffset option }

type MachineChanges =
    { Machine: string
      Os: string
      Kernel: string
      Changes: Manifest.PackageChange[]
      Upgrades: Upgrade[] }

/// One package installed on two or more machines at different versions.
type Drift = { Manager: string; Name: string; Versions: (string * string)[] }

type Report =
    { PeriodStart: DateTimeOffset
      PeriodEnd: DateTimeOffset
      Settings: Settings
      Behind: RepoNote[]
      Dirty: RepoNote[]
      Unpushed: RepoNote[]
      Neglected: RepoNote[]
      Machines: MachineChanges[]
      Drift: Drift[]
      /// Drift rows beyond the ones listed.
      DriftMore: int }

/// The week a scheduled recap covers: ending at the latest `weekday` 00:00 at or before `now`.
let period (weekday: DayOfWeek) (now: DateTimeOffset) =
    let back = (int now.DayOfWeek - int weekday + 7) % 7
    let end' = DateTimeOffset(now.Date.AddDays(float -back), now.Offset)
    end'.AddDays -7., end'

let private older (now: DateTimeOffset) (days: int) (t: DateTimeOffset option) : bool =
    match t with
    | Some t -> (now - t).TotalDays >= float days
    | None -> true // already so when harbor first saw it

let private note (s: RepoState) detail since =
    { Machine = s.Machine; Name = s.Repo.Name; Path = s.Repo.Path; Detail = detail; Since = since }

let private isDirty (r: Repo) =
    r.Changes |> Option.exists (fun c -> c.Staged + c.Unstaged + c.Untracked + c.Conflicted > 0)

let repoNotes (settings: Settings) (now: DateTimeOffset) (repos: RepoState[]) =
    let behind =
        repos
        |> Array.choose (fun s ->
            match s.Repo.Branch with
            | Some b when b.Behind > 0 ->
                // git's answer (oldest missing upstream commit) beats harbor's own event history
                let since = s.Repo.BehindSince |> Option.orElse s.BehindSince
                if older now settings.BehindDays since then
                    let upstream = defaultArg b.Upstream "upstream"
                    Some(note s $"↓{b.Behind} behind {upstream}" since)
                else None
            | _ -> None)
    let dirty =
        repos
        |> Array.choose (fun s ->
            if isDirty s.Repo && older now settings.DirtyDays (s.DirtySince |> Option.orElse s.Repo.LastModified) then
                let c = s.Repo.Changes.Value
                Some(note s $"{c.Staged + c.Unstaged + c.Untracked + c.Conflicted} uncommitted" (s.DirtySince |> Option.orElse s.Repo.LastModified))
            else None)
    let unpushed =
        repos
        |> Array.choose (fun s ->
            match s.Repo.Branch, s.Repo.LastCommit with
            | Some b, Some c when b.Ahead > 0 && older now settings.DirtyDays (Some c.Date) ->
                Some(note s $"↑{b.Ahead} unpushed" (Some c.Date))
            | _ -> None)
    let neglected =
        repos
        |> Array.choose (fun s ->
            let last = [ s.Repo.LastCommit |> Option.map _.Date; s.Repo.LastModified ] |> List.choose id |> List.sortDescending |> List.tryHead
            if s.Repo.WorktreeOf.IsNone && older now settings.NeglectedDays last then Some(note s "no commits or changes" last) else None)
    let oldest = Array.sortBy (fun (n: RepoNote) -> n.Since |> Option.defaultValue DateTimeOffset.MinValue)
    oldest behind, oldest dirty, oldest unpushed, oldest neglected

/// Packages on two or more machines whose versions differ, per manager (machines without that
/// manager don't count).
let drift (machines: (string * Manifest) list) : Drift[] =
    machines
    |> List.collect (fun (name, m) ->
        m.Sets |> Array.filter (fun s -> s.Error.IsNone) |> Array.toList
        |> List.collect (fun s -> s.Packages |> Array.toList |> List.map (fun p -> (s.Manager, p.Name), (name, p.Version))))
    |> List.groupBy fst
    |> List.choose (fun ((mgr, pkg), rows) ->
        let versions = rows |> List.map snd |> List.sortBy fst |> Array.ofList
        if versions.Length >= 2 && (versions |> Array.map snd |> Array.distinct).Length > 1 then
            Some { Manager = mgr; Name = pkg; Versions = versions }
        else None)
    |> List.sortBy (fun d -> d.Manager, d.Name)
    |> Array.ofList

let build (settings: Settings) (start: DateTimeOffset) (end': DateTimeOffset) (repos: RepoState[]) (machines: MachineManifests[]) : Report =
    let behind, dirty, unpushed, neglected = repoNotes settings end' repos
    let changes =
        machines
        |> Array.choose (fun m ->
            m.Latest
            |> Option.map (fun latest ->
                { Machine = m.Machine
                  Os = latest.Os.PrettyName
                  Kernel = latest.Os.Kernel
                  Changes = (match m.AtStart with Some a -> Manifest.diff a latest | None -> [||])
                  Upgrades = latest.Sets |> Array.collect _.Upgrades }))
    let allDrift = drift [ for m in machines do match m.Latest with Some l -> yield m.Machine, l | None -> () ]
    { PeriodStart = start
      PeriodEnd = end'
      Settings = settings
      Behind = behind
      Dirty = dirty
      Unpushed = unpushed
      Neglected = neglected
      Machines = changes
      Drift = Array.truncate 200 allDrift
      DriftMore = max 0 (allDrift.Length - 200) }

// ---- markdown ----

let private day (t: DateTimeOffset) = t.ToString "yyyy-MM-dd"
let private age (now: DateTimeOffset) (t: DateTimeOffset option) =
    match t with
    | Some t -> $"{int (now - t).TotalDays}d"
    | None -> "before harbor"

let markdown (r: Report) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore
    line $"# Harbor recap: {day r.PeriodStart} to {day r.PeriodEnd}"
    line ""
    let section title (notes: RepoNote[]) (rule: string) =
        line $"## {title} ({notes.Length})"
        line ""
        line $"_{rule}_"
        line ""
        if notes.Length = 0 then line "Nothing." else
            line "| machine | repo | state | for |"
            line "| --- | --- | --- | --- |"
            for n in notes do line $"| {n.Machine} | {n.Name} `{n.Path}` | {n.Detail} | {age r.PeriodEnd n.Since} |"
        line ""
    section "Behind" r.Behind $"Behind upstream for {r.Settings.BehindDays}+ days."
    section "Uncommitted" r.Dirty $"Uncommitted changes older than {r.Settings.DirtyDays} days."
    section "Unpushed" r.Unpushed $"Local commits older than {r.Settings.DirtyDays} days not on the upstream."
    section "Neglected" r.Neglected $"No commits or changes for {r.Settings.NeglectedDays}+ days."
    line "## Machines"
    line ""
    for m in r.Machines do
        let added = m.Changes |> Array.filter (fun c -> c.Before.IsNone)
        let removed = m.Changes |> Array.filter (fun c -> c.After.IsNone)
        let changed = m.Changes |> Array.filter (fun c -> c.Before.IsSome && c.After.IsSome)
        line $"### {m.Machine}: {m.Os}, kernel {m.Kernel}"
        line ""
        line $"This week: {added.Length} installed, {removed.Length} removed, {changed.Length} changed. Pending upgrades: {m.Upgrades.Length}."
        for c in Array.truncate 50 m.Changes do
            line $"""- {c.Manager} {c.Name}: {defaultArg c.Before "—"} → {defaultArg c.After "—"}"""
        if m.Changes.Length > 50 then line $"- … {m.Changes.Length - 50} more"
        line ""
    line $"## Version drift between machines ({r.Drift.Length + r.DriftMore})"
    line ""
    if r.Drift.Length = 0 then line "None (or only one machine reports each manager)." else
        for d in r.Drift do
            let vs = d.Versions |> Array.map (fun (m, v) -> $"{m} {v}") |> String.concat ", "
            line $"- {d.Manager} {d.Name}: {vs}"
        if r.DriftMore > 0 then line $"- … {r.DriftMore} more"
    sb.ToString()
