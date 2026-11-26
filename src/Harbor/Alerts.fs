/// Alerts: rules that look at what the hub already knows (machine reports, site checks) and keep a
/// queue of conditions. Each alert stays until someone marks it seen; an open one also stays listed
/// until its condition clears. Actions only point at existing endpoints, with their own checks.
module Harbor.Alerts

open System
open System.Threading
open System.Threading.Tasks
open Harbor.Domain
open Harbor.Storage

let private cond rule subject machineId severity title detail : AlertCondition =
    { Rule = rule; Subject = subject; MachineId = machineId; Severity = severity; Title = title; Detail = detail }

/// An approved machine whose report has gone stale (no push for three intervals).
let offline (now: DateTimeOffset) (ms: MachineSummary[]) =
    ms
    |> Array.filter (fun m -> m.Status = "approved" && m.LastPushAt.IsSome && not (fresh now m))
    |> Array.map (fun m ->
        let every = max 1 ((defaultArg m.PushSeconds 300) / 60)
        cond "offline" m.Name (Some m.Id) "critical" $"{m.Name} stopped reporting"
            (Some $"It reports every {every} min and has missed three in a row: offline, asleep, or its agent stopped."))

/// The last role switch had an action that failed (it stays until the next switch).
let roleFailed (m: MachineSummary) (snap: Snapshot) =
    snap.Role
    |> Option.bind (fun r ->
        match r.Applied |> Array.filter (fun l -> l.Contains "failed") with
        | [||] -> None
        | failed ->
            Some(cond "role_failed" m.Name (Some m.Id) "warn" $"{m.Name}: switching to {r.Name} had failed actions" (Some(String.Join("\n", failed)))))
    |> Option.toArray

/// Commits that exist only in one clone for longer than `days`: the work you'd lose with the disk.
let unpushed (now: DateTimeOffset) (days: float) (m: MachineSummary) (snap: Snapshot) =
    snap.Repos
    |> Array.filter (fun r -> r.Remotes.Length > 0 && not (Array.contains r.Path m.Hidden))
    |> Array.choose (fun r ->
        r.InFlight
        |> Option.bind (fun f -> Array.tryLast f.Unpushed |> Option.map (fun oldest -> f, oldest))
        |> Option.filter (fun (_, oldest) -> (now - oldest.Date).TotalDays > days)
        |> Option.map (fun (f, oldest) ->
            let plural = if f.UnpushedCount = 1 then "" else "s"
            cond "unpushed" $"{m.Name}:{r.Path}" (Some m.Id) "warn" $"{r.Name} on {m.Name}: {f.UnpushedCount} commit{plural} not pushed"
                (Some $"Oldest from {oldest.Date:``yyyy-MM-dd``} on {oldest.Branch}: \"{oldest.Subject}\"")))

/// Down on its last two checks (one failure is a blip), or its certificate is close to expiry.
let sites (now: DateTimeOffset) (site: Config.SiteConfig) (checks: Sites.Check[]) =
    let isDns = site.Url.StartsWith "dns:"
    let down =
        match checks |> Array.rev |> Array.truncate 2 with
        | [| last; prev |] when not last.Ok && not prev.Ok ->
            let title = if isDns then $"{site.Name} isn't answering DNS" else $"{site.Name} is down"
            [| cond "site_down" site.Name None "critical" title last.Error |]
        | _ -> [||]
    let cert =
        match Array.tryLast checks |> Option.bind _.CertExpires with
        | Some e when (e - now).TotalDays < 14. ->
            let days = int (floor (e - now).TotalDays)
            let when' = if days < 0 then "has expired" elif days = 0 then "expires today" else $"expires in {days} days"
            [| cond "cert" site.Name None (if days <= 3 then "critical" else "warn") $"{site.Name}: certificate {when'}"
                   (Some $"Expires {e:``yyyy-MM-dd``}; renew it, or check the host's automatic renewal.") |]
        | _ -> [||]
    Array.append down cert

/// A unit's latest release failed (migration status checks don't count); it clears when the next succeeds.
let releaseFailed (machines: MachineSummary[]) (jobs: (int64 * JobInfo)[]) =
    let names = machines |> Array.map (fun m -> m.Id, m.Name) |> Map.ofArray
    jobs // newest first
    |> Array.choose (fun (id, j) ->
        match ReleaseView.jobUnit j, j.Ok with
        | Some(path, r), Some ok when r.Steps <> [| "status" |] -> Some((id, path, r.Unit), (j, ok))
        | _ -> None)
    |> Array.distinctBy fst
    |> Array.choose (fun ((id, path, unit), (j, ok)) ->
        match names.TryFind id with
        | Some name when not ok ->
            let folder = if unit = "" then IO.Path.GetFileName path |> string else unit
            let failedLine =
                (defaultArg j.Output "").Split '\n' |> Array.tryFindBack (fun l -> l.StartsWith "── " && l.Contains "failed")
            Some(cond "release_failed" $"{name}:{path}:{unit}" (Some id) "warn" $"{folder} on {name}: release failed"
                     (failedLine |> Option.map (fun l -> l.TrimStart('─', ' '))))
        | _ -> None)

let rules = [| "offline"; "role_failed"; "unpushed"; "site_down"; "cert"; "release_failed" |]

/// The job body a role action posts (the jobs endpoint's own shape).
type JobBody = { Kind: string; Arg: string }

/// A button on an alert: a link (Href) or a POST to an existing hub endpoint, admins only.
type Action =
    { Label: string
      Href: string option
      Post: string option
      Body: JobBody option
      Confirm: string option }

let private link label href = { Label = label; Href = Some href; Post = None; Body = None; Confirm = None }
let private post label path body confirm = { Label = label; Href = None; Post = Some path; Body = body; Confirm = confirm }

/// What one can do about an alert; `snap` is its machine's latest report (for role names).
let actions (admin: bool) (sites: Config.SiteConfig[]) (snap: Snapshot option) (a: Alert) : Action[] =
    let machine = a.MachineId |> Option.map (fun id -> link "Open machine" $"#m/{id}") |> Option.toList
    let adminOnly xs = if admin then xs else []
    [ match a.Rule with
      | "offline" ->
          yield! machine
          yield! adminOnly [ post "Sync all" "/api/sync" None None ]
      | "role_failed" ->
          yield! machine
          match a.MachineId, snap |> Option.bind _.Role with
          | Some id, Some role ->
              let jobs = $"/api/machines/{id}/jobs"
              yield! adminOnly [ post $"Retry {role.Name}" jobs (Some { Kind = "role"; Arg = role.Name }) (Some $"Run the {role.Name} role's actions on {a.Subject} again?") ]
              match role.Declared |> Array.tryHead with
              | Some d when d.Name <> role.Name ->
                  yield! adminOnly [ post $"Switch to {d.Name}" jobs (Some { Kind = "role"; Arg = d.Name }) (Some $"Switch {a.Subject} to {d.Name}?") ]
              | _ -> ()
          | _ -> ()
      | "unpushed" -> yield! machine
      | "release_failed" ->
          yield link "Open releases" "#network/releases"
          yield! machine
      | "site_down" | "cert" ->
          yield! adminOnly [ post "Check now" "/api/sites/check" None None ]
          match sites |> Array.tryFind (fun s -> s.Name = a.Subject) with
          | Some s when not (s.Url.StartsWith "dns:") -> yield link "Open site" s.Url
          | _ -> ()
      | _ -> () ]
    |> Array.ofList

/// Evaluates every rule once a minute (and on demand), keeping the alert table in step.
type Evaluator(db: Db, machines: unit -> Task<MachineSummary[]>, siteList: Config.SiteConfig[]) =
    let gate = new SemaphoreSlim(1, 1)
    let unpushedDays = 3.

    let evaluate () =
        task {
            let now = DateTimeOffset.Now
            let found = ResizeArray<AlertCondition>()
            let ran = ResizeArray<string>()
            let attempt (names: string list) (work: unit -> Task<AlertCondition[]>) =
                task {
                    try
                        let! cs = work ()
                        found.AddRange cs
                        ran.AddRange names
                    with ex ->
                        eprintfn $"""alerts: {String.Join(", ", names)}: {ex.Message}"""
                }
            let! ms = machines ()
            do! attempt [ "offline" ] (fun () -> Task.FromResult(offline now ms))
            do! attempt [ "role_failed"; "unpushed" ] (fun () ->
                    task {
                        let out = ResizeArray()
                        for m in ms |> Array.filter (fun m -> m.Status = "approved") do
                            match! db.Snapshot m.Id with
                            | Some s ->
                                out.AddRange(roleFailed m s)
                                out.AddRange(unpushed now unpushedDays m s)
                            | None -> ()
                        return out.ToArray()
                    })
            do! attempt [ "site_down"; "cert" ] (fun () ->
                    task {
                        let! checks = db.SiteChecks(now.AddDays -1.)
                        let bySite = checks |> Array.groupBy _.Site |> Map.ofArray
                        return siteList |> Array.collect (fun s -> sites now s (bySite.TryFind s.Name |> Option.defaultValue [||]))
                    })
            do! attempt [ "release_failed" ] (fun () ->
                    task {
                        let! jobs = db.ReleaseJobs()
                        return releaseFailed ms jobs
                    })
            let! opened = db.SyncAlerts(now, ran.ToArray(), found.ToArray())
            for a in opened do eprintfn $"alert: {a.Title}"
        }

    member _.Now() : Task =
        task {
            do! gate.WaitAsync()
            try do! evaluate () finally gate.Release() |> ignore
        }

    member this.Loop(stop: CancellationToken) : Task =
        task {
            try
                do! Task.Delay(TimeSpan.FromSeconds 30., stop) // let agents reconnect after a hub restart
                while not stop.IsCancellationRequested do
                    try do! this.Now() with ex -> eprintfn $"alerts: {ex.Message}"
                    do! Task.Delay(TimeSpan.FromMinutes 1., stop)
            with :? OperationCanceledException ->
                ()
        }
