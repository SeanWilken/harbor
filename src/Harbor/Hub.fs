/// Hub HTTP: the tailnet guard, agent pushes, machine approval and the read API for the UI.
module Harbor.Hub

open System
open System.Net
open System.Threading.Tasks
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.WebSockets
open Harbor.Domain
open Harbor.Enrol

let private callerKey = "harbor.caller"
let private roleKey = "harbor.role"

let private whois (ts: Tailscale.Client option) (ip: IPAddress) (port: int) =
    task {
        match ts with
        | Some ts when isTailnetAddress ip ->
            try
                return! ts.WhoIs(ip, port)
            with ex ->
                eprintfn $"whois {ip}: {ex.Message}"
                return None
        | _ -> return None
    }

let private denyReason =
    "not a harbor viewer: an admin can add your Tailscale login to hub.viewers (or hub.admins)"

/// Every request: loopback passes, tailnet peers need a WhoIs answer, anything else is 403.
/// Then a role: admins and viewers may use the UI and API; other identified peers may only push
/// (machine enrollment has its own approval).
/// `servePort`: the loopback listener Tailscale Serve proxies to. Requests arriving there are
/// identified by Serve's Tailscale-User-Login header only, and may not push or open agent links.
let useGuard (app: WebApplication) (ts: Tailscale.Client option) (servePort: int option) (admins: string[]) (viewers: string[]) =
    app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
        task {
            match ctx.Connection.RemoteIpAddress with
            | null ->
                ctx.Response.StatusCode <- 403
            | ip ->
                let viaServe = servePort = Some ctx.Connection.LocalPort && IPAddress.IsLoopback ip
                let! decision =
                    if viaServe then
                        Task.FromResult(served (ctx.Request.Headers["Tailscale-User-Login"].ToString()))
                    else
                        task {
                            let! peer = whois ts ip ctx.Connection.RemotePort
                            return access ip peer
                        }
                let agentPath = ctx.Request.Path.Equals(PathString "/api/push") || ctx.Request.Path.Equals(PathString AgentLink.path)
                match decision with
                | Allowed _ when viaServe && agentPath ->
                    ctx.Response.StatusCode <- 403
                    do! ctx.Response.WriteAsync "agents use the hub's tailnet address, not Tailscale Serve"
                | Allowed caller ->
                    ctx.Items[callerKey] <- box caller
                    match role admins viewers caller with
                    | Some r ->
                        ctx.Items[roleKey] <- box r
                        do! next.Invoke ctx
                    | None when agentPath ->
                        do! next.Invoke ctx
                    | None ->
                        ctx.Response.StatusCode <- 403
                        do! ctx.Response.WriteAsync denyReason
                | Denied reason ->
                    ctx.Response.StatusCode <- 403
                    do! ctx.Response.WriteAsync reason
        }
        :> Task)
    |> ignore

/// Identity here is the caller's tailnet address, not a cookie, so any page open in an admin's
/// browser could otherwise make it POST to the hub on their behalf. Browsers label cross-site
/// requests (Origin, Sec-Fetch-Site); agents and scripts send neither, so they're unaffected.
let crossSite (methodName: string) (origin: string) (fetchSite: string) (host: string) (isWebSocket: bool) =
    let writes = not (HttpMethods.IsGet methodName || HttpMethods.IsHead methodName || HttpMethods.IsOptions methodName)
    let foreignOrigin =
        origin <> ""
        && (match Uri.TryCreate(origin, UriKind.Absolute) with
            | true, NonNull u -> u.Authority <> host
            | _ -> true)
    let foreignFetch = fetchSite <> "" && fetchSite <> "same-origin" && fetchSite <> "none"
    (writes || isWebSocket) && (foreignOrigin || foreignFetch)

let useBrowserSafety (app: WebApplication) =
    app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
        task {
            let h = ctx.Response.Headers
            h["X-Frame-Options"] <- "DENY"
            h["Content-Security-Policy"] <- "frame-ancestors 'none'"
            h["X-Content-Type-Options"] <- "nosniff"
            h["Referrer-Policy"] <- "no-referrer"
            let req = ctx.Request
            if crossSite req.Method (req.Headers.Origin.ToString()) (req.Headers["Sec-Fetch-Site"].ToString()) (req.Host.Value |> Option.ofObj |> Option.defaultValue "") ctx.WebSockets.IsWebSocketRequest then
                ctx.Response.StatusCode <- 403
                do! ctx.Response.WriteAsync "cross-site request refused"
            else
                do! next.Invoke ctx
        }
        :> Task)
    |> ignore

let private caller (ctx: HttpContext) =
    match ctx.Items.TryGetValue callerKey with
    | true, (:? Caller as c) -> c
    | _ -> failwith "request did not pass the tailnet guard"

let private roleOf (ctx: HttpContext) =
    match ctx.Items.TryGetValue roleKey with
    | true, (:? Role as r) -> Some r
    | _ -> None

let private isAdmin ctx = roleOf ctx = Some Admin

let private actor ctx = match caller ctx with Local -> "local" | Peer p -> p.Login | Served l -> l

/// For the agent's loopback debugging endpoints: an admin through the hub guard, or (agent mode,
/// no guard) a loopback caller.
let adminOrLoopback (ctx: HttpContext) =
    match ctx.Items.TryGetValue roleKey with
    | true, (:? Role as r) -> r = Admin
    | _ -> match ctx.Connection.RemoteIpAddress with null -> false | ip -> IPAddress.IsLoopback ip

type JobBody = { Kind: string; Path: string option; Arg: string option }
type HiddenBody = { Path: string; Hidden: bool }

/// Must match the job.kind check in db/up.
let knownJobs = [| "fetch"; "pull"; "diff"; "role" |]

/// A diff names one repo and a commit or "worktree"; a role switch names one of the machine's
/// declared roles (checked against its snapshot as well); other jobs take no arg.
let checkJob (declared: RoleInfo[]) (body: JobBody) : Result<unit, string> =
    match body.Kind, body.Path, body.Arg with
    | k, _, _ when not (Array.contains k knownJobs) -> Error $"unknown job kind '{k}'"
    | "diff", Some _, Some a when a = "worktree" || GitParse.isSha a -> Ok()
    | "diff", _, _ -> Error "a diff needs a repo path and a commit id or 'worktree'"
    | "role", None, Some r when declared |> Array.exists (fun d -> d.Name = r) -> Ok()
    | "role", None, Some r when declared.Length = 0 -> Error $"this machine declares no roles, so it can't switch to '{r}'"
    | "role", None, Some r -> Error $"'{r}' isn't one of this machine's roles"
    | "role", _, _ -> Error "a role switch names a role and no path"
    | _, _, Some _ -> Error $"'{body.Kind}' takes no arg"
    | _ -> Ok()

let private problem code (msg: string) = Results.Json({| message = msg |}, statusCode = Nullable code)

/// Manifest as Markdown: OS, then each manager's packages and pending upgrades.
let manifestMarkdown (machine: string) (m: Manifest) =
    let sb = Text.StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore
    line $"# {machine}: manifest {m.TakenAt:``yyyy-MM-dd HH:mm``}"
    line ""
    line $"{m.Os.PrettyName}, kernel {m.Os.Kernel}, {m.Os.Arch}. Hash `{m.Hash.Substring(0, 12)}`."
    for set in m.Sets do
        line ""
        line $"## {set.Manager} ({set.Packages.Length} packages, {set.Upgrades.Length} upgradable)"
        set.Error |> Option.iter (fun e -> line $"Listing failed: {e}")
        if set.Upgrades.Length > 0 then
            line ""
            for u in set.Upgrades do line $"- upgrade {u.Name}: {u.From} → {u.To}"
        line ""
        line "| package | version |"
        line "| --- | --- |"
        for p in set.Packages do line $"| {p.Name} | {p.Version} |"
    line ""
    line $"## repos ({m.Repos.Length})"
    for r in m.Repos do line $"""- `{r.Path}` {defaultArg r.RemoteKey "(no remote)"} {defaultArg r.Branch ""}"""
    sb.ToString()

let private recapNow (db: Storage.Db) (settings: Recap.Settings) (start: DateTimeOffset) (end': DateTimeOffset) =
    task {
        let! repos, machines = db.RecapInputs start
        return Recap.build settings start end' repos machines
    }

/// Viewers get the repo sections only: package lists and versions are admin detail.
let private redactRecap (r: Recap.Report) = { r with Machines = [||]; Drift = [||]; DriftMore = 0 }

/// Once an hour: if the latest scheduled week has no recap yet, build and store it.
let recapLoop (db: Storage.Db) (settings: Recap.Settings) (stop: Threading.CancellationToken) : Task =
    task {
        try
            while not stop.IsCancellationRequested do
                try
                    let start, end' = Recap.period settings.Weekday DateTimeOffset.Now
                    let! exists = db.HasRecap end'
                    if not exists then
                        let! r = recapNow db settings start end'
                        let! saved = db.SaveRecap(r, Recap.markdown r)
                        if saved then eprintfn $"recap: stored week ending {end':``yyyy-MM-dd``}"
                with ex ->
                    eprintfn $"recap: {ex.Message}"
                do! Task.Delay(TimeSpan.FromHours 1., stop)
        with :? OperationCanceledException ->
            ()
    }

let private diffResult (ia: Storage.ManifestInfo, ma: Manifest) (ib: Storage.ManifestInfo, mb: Manifest) : IResult =
    Results.Json(
        {| a = ia; b = ib
           os = (if ma.Os = mb.Os then None else Some {| a = ma.Os; b = mb.Os |})
           managers = {| a = ma.Sets |> Array.map _.Manager; b = mb.Sets |> Array.map _.Manager |}
           changes = Manifest.diff ma mb |},
        Config.jsonOptions)

/// Daily: prune what retention says to drop (see Storage.Prune).
let pruneLoop (db: Storage.Db) (stop: Threading.CancellationToken) : Task =
    task {
        try
            while not stop.IsCancellationRequested do
                try
                    let! removed = db.Prune()
                    if not removed.IsEmpty then
                        eprintfn $"""retention: {String.Join(", ", removed |> List.map (fun (t, n) -> $"{n} {t}"))}"""
                with ex ->
                    eprintfn $"retention: {ex.Message}"
                do! Task.Delay(TimeSpan.FromHours 24., stop)
        with :? OperationCanceledException ->
            ()
    }

let private wantsMarkdown (ctx: HttpContext) = ctx.Request.Query["format"].ToString() = "md"
/// `agentUrl`: where agents should push (the hub's first tailnet IPv4 URL), shown when pairing.
let mapApi (app: WebApplication) (db: Storage.Db) (links: AgentLink.Registry) (recapSettings: Recap.Settings) (agentUrl: string option) (browserUrl: string option) (sites: Sites.Checker) =
    // ---- sites: public websites checked from outside, on any host ----
    app.MapGet("/api/sites", Func<Task<IResult>>(fun () ->
        task {
            let now = DateTimeOffset.Now
            let! checks = db.SiteChecks(now.AddDays -1.)
            let bySite = checks |> Array.groupBy _.Site |> Map.ofArray
            let rows = sites.Sites |> Array.map (fun s -> Sites.summarize now s (bySite.TryFind s.Name |> Option.defaultValue [||]))
            return Results.Json(rows, Config.jsonOptions)
        })) |> ignore

    app.MapPost("/api/sites/check", Func<HttpContext, Task<IResult>>(fun ctx ->
        task {
            if not (isAdmin ctx) then return problem 403 "only hub admins can run site checks"
            else
                do! sites.Now()
                return Results.NoContent()
        })) |> ignore

    app.Map(AgentLink.path, RequestDelegate(fun ctx ->
        task {
            match caller ctx with
            | Peer p when ctx.WebSockets.IsWebSocketRequest ->
                let opts = WebSocketAcceptContext(KeepAliveInterval = TimeSpan.FromSeconds 20., KeepAliveTimeout = TimeSpan.FromSeconds 30.)
                use! ws = ctx.WebSockets.AcceptWebSocketAsync opts
                do! AgentLink.serve ws links p.NodeId (fun name -> db.StatusOf(p.NodeId, name)) app.Lifetime.ApplicationStopping
            | Peer _ -> ctx.Response.StatusCode <- 400
            | _ ->
                ctx.Response.StatusCode <- 403
                do! ctx.Response.WriteAsync "agents connect over the tailnet so the hub can identify them"
        }
        :> Task)) |> ignore

    app.MapPost("/api/push", Func<HttpContext, Push, Task<IResult>>(fun ctx push ->
        task {
            match caller ctx with
            | Local -> return problem 403 "push over the tailnet, not loopback: the sender must be identifiable"
            | Served _ -> return problem 403 "agents push to the hub's tailnet address, not through Tailscale Serve"
            | Peer p ->
                match! db.Ingest(Storage.Remote p, push) with
                | Ok reply -> return Results.Ok reply
                | Error reason -> return problem 403 reason
        })) |> ignore

    app.MapGet("/api/machines", Func<HttpContext, Task<Storage.MachineSummary[]>>(fun ctx ->
        task {
            let! ms = db.Machines(fun (n, m) -> links.IsConnected(n, m))
            return if isAdmin ctx then ms else ms |> Array.map (fun m -> { m with Login = None })
        })) |> ignore

    app.MapGet("/api/machines/{id}/snapshot", Func<HttpContext, int64, Task<IResult>>(fun ctx id ->
        task {
            match! db.Snapshot id with
            | Some s -> return Results.Ok(if isAdmin ctx then s else Redact.snapshot s)
            | None -> return Results.NotFound()
        })) |> ignore

    let setStatus status =
        Func<HttpContext, int64, Task<IResult>>(fun ctx id ->
            task {
                if not (isAdmin ctx) then
                    return problem 403 "only hub admins can approve or reject machines"
                else
                    let! found = db.SetStatus(id, status, actor ctx)
                    return if found then Results.NoContent() else Results.NotFound()
            })
    app.MapPost("/api/machines/{id}/approve", setStatus Approved) |> ignore
    app.MapPost("/api/machines/{id}/reject", setStatus Rejected) |> ignore

    app.MapGet("/api/me", Func<HttpContext, IResult>(fun ctx ->
        let who = caller ctx
        let login, node = match who with Local -> "local", None | Peer p -> p.Login, Some p.NodeName | Served l -> l, None
        let r = match roleOf ctx with Some Admin -> "admin" | Some Viewer -> "viewer" | None -> "none"
        // a browser on the hub machine itself, whether over loopback or its own tailnet address
        let self = who = Local || (ctx.Connection.RemoteIpAddress <> null && ctx.Connection.RemoteIpAddress = ctx.Connection.LocalIpAddress)
        Results.Json {| login = login; node = node; self = self; role = r; admin = (r = "admin"); agentUrl = agentUrl; browserUrl = browserUrl |})) |> ignore
    app.MapGet("/api/shared",Func<Task<Storage.SharedRepo[]>>(fun () -> db.Shared())) |> ignore

    app.MapPost("/api/machines/{id}/jobs", Func<HttpContext, int64, JobBody, Task<IResult>>(fun ctx id body ->
        task {
            if not (isAdmin ctx) then
                return problem 403 "only hub admins can queue jobs"
            else
                let! snap = if body.Kind = "role" then db.Snapshot id else Task.FromResult None
                let declared = snap |> Option.bind _.Role |> Option.map _.Declared |> Option.defaultValue [||]
                match checkJob declared body with
                | Error e -> return problem 400 e
                | Ok() ->
                    match! db.RequestJob(id, body.Kind, body.Path, body.Arg, actor ctx) with
                    | Ok jobId -> return Results.Json({| id = jobId |}, statusCode = Nullable 202)
                    | Error e -> return problem 409 e
        })) |> ignore

    app.MapGet("/api/machines/{id}/jobs", Func<HttpContext, int64, Task<Storage.JobInfo[]>>(fun ctx id ->
        task {
            let! js = db.Jobs id
            return if isAdmin ctx then js else js |> Array.map (fun j -> { j with Output = None; RequestedBy = "" })
        })) |> ignore

    app.MapPost("/api/machines/{id}/hidden", Func<HttpContext, int64, HiddenBody, Task<IResult>>(fun ctx id body ->
        task {
            if not (isAdmin ctx) then
                return problem 403 "only hub admins can hide repos"
            else
                do! db.SetHidden(id, body.Path, body.Hidden, actor ctx)
                return Results.NoContent()
        })) |> ignore

    // ---- refresh: opening a machine asks its agent for fresh state (cadence is otherwise slow) ----
    let lastRefresh = Collections.Concurrent.ConcurrentDictionary<int64, DateTimeOffset>()
    app.MapPost("/api/machines/{id}/refresh", Func<int64, Task<IResult>>(fun id ->
        task {
            let now = DateTimeOffset.UtcNow
            match lastRefresh.TryGetValue id with
            | true, t when now - t < TimeSpan.FromSeconds 30. -> return Results.Json({| sent = false; reason = "refreshed under 30 s ago" |}, statusCode = Nullable 202)
            | _ ->
                match! db.MachineKey id with
                | None -> return Results.NotFound()
                | Some(nodeId, name) ->
                    lastRefresh[id] <- now
                    links.Wake(nodeId, name, "refresh")
                    return Results.Json({| sent = nodeId = "local" || links.IsConnected(nodeId, name) |}, statusCode = Nullable 202)
        })) |> ignore

    // ---- sync: every linked machine rescans and pushes now (the UI's sync button, the tray's Sync now) ----
    app.MapPost("/api/sync", Func<Task<IResult>>(fun () ->
        task {
            let now = DateTimeOffset.UtcNow
            let! ms = db.Machines(fun (n, m) -> links.IsConnected(n, m))
            let mutable woken = []
            for m in ms |> Array.filter (fun m -> m.Status = "approved" && m.Connected) do
                match! db.MachineKey m.Id with
                | Some(nodeId, name) ->
                    match lastRefresh.TryGetValue m.Id with
                    | true, t when now - t < TimeSpan.FromSeconds 10. -> ()
                    | _ ->
                        lastRefresh[m.Id] <- now
                        links.Wake(nodeId, name, "refresh")
                    woken <- m.Name :: woken
                | None -> ()
            let offline = ms |> Array.filter (fun m -> m.Status = "approved" && not m.Connected) |> Array.map _.Name
            return Results.Json({| syncing = List.rev woken; offline = offline |}, statusCode = Nullable 202)
        })) |> ignore

    // ---- availability: which machines take outside work right now, by their current role ----
    app.MapGet("/api/availability", Func<Task<IResult>>(fun () ->
        task {
            let! ms = db.Machines(fun (n, m) -> links.IsConnected(n, m))
            let rows = Storage.availability DateTimeOffset.Now ms
            return Results.Json({| at = DateTimeOffset.Now; machines = rows |})
        })) |> ignore

    // ---- pairing: admins create one-time codes; agents present them with a push ----
    app.MapPost("/api/pairing", Func<HttpContext, Task<IResult>>(fun ctx ->
        task {
            if not (isAdmin ctx) then return problem 403 "only hub admins can pair devices"
            else
                let! code, expires = db.CreatePairingCode(actor ctx)
                ctx.Response.Headers.CacheControl <- "no-store"
                return Results.Json {| code = Pairing.display code; expiresAt = expires; minutes = int Pairing.lifetime.TotalMinutes |}
        })) |> ignore

    /// Whether a code from this admin's window has been used yet, and by which machine.
    app.MapGet("/api/pairing/{code}", Func<HttpContext, string, Task<IResult>>(fun ctx code ->
        task {
            if not (isAdmin ctx) then return problem 403 "only hub admins can pair devices"
            else
                match! db.PairingState code with
                | None -> return Results.NotFound()
                | Some(expires, usedBy) -> return Results.Json {| expiresAt = expires; usedBy = usedBy |}
        })) |> ignore

    // ---- manifests: the list is metadata for anyone with a role; contents are admin-only ----
    app.MapGet("/api/machines/{id}/manifests", Func<int64, Task<Storage.ManifestInfo[]>>(fun id -> db.Manifests id)) |> ignore

    app.MapGet("/api/manifests/{id}", Func<HttpContext, int64, Task<IResult>>(fun ctx id ->
        task {
            if not (isAdmin ctx) then return problem 403 "package lists are admin-only"
            else
                match! db.Manifest id with
                | None -> return Results.NotFound()
                | Some(info, m) ->
                    if wantsMarkdown ctx then
                        let! ms = db.Machines(fun _ -> false)
                        let name = ms |> Array.tryFind (fun x -> x.Id = info.MachineId) |> Option.map _.Name |> Option.defaultValue $"machine {info.MachineId}"
                        return Results.Text(manifestMarkdown name m, "text/markdown; charset=utf-8")
                    else
                        return Results.Json(m, Config.jsonOptions)
        })) |> ignore

    /// What changed from manifest a to b: the same machine over time, or two machines.
    app.MapGet("/api/manifests/{a}/diff/{b}", Func<HttpContext, int64, int64, Task<IResult>>(fun ctx a b ->
        task {
            if not (isAdmin ctx) then return problem 403 "package lists are admin-only"
            else
                let! ma = db.Manifest a
                let! mb = db.Manifest b
                return
                    match ma, mb with
                    | Some a, Some b -> diffResult a b
                    | _ -> Results.NotFound()
        })) |> ignore

    // ---- recaps ----
    app.MapGet("/api/recap", Func<HttpContext, Task<IResult>>(fun ctx ->
        task {
            let days = match Int32.TryParse(ctx.Request.Query["days"].ToString()) with | true, d when d > 0 && d <= 90 -> d | _ -> 7
            let end' = DateTimeOffset.Now
            let! r = recapNow db recapSettings (end'.AddDays(float -days)) end'
            let r = if isAdmin ctx then r else redactRecap r
            return if wantsMarkdown ctx then Results.Text(Recap.markdown r, "text/markdown; charset=utf-8") else Results.Json(r, Config.jsonOptions)
        })) |> ignore

    app.MapGet("/api/recaps", Func<Task<Storage.RecapInfo[]>>(fun () -> db.Recaps())) |> ignore

    app.MapGet("/api/recaps/{id}", Func<HttpContext, int64, Task<IResult>>(fun ctx id ->
        task {
            match! db.Recap id with
            | None -> return Results.NotFound()
            | Some(json, md) ->
                if isAdmin ctx then
                    return if wantsMarkdown ctx then Results.Text(md, "text/markdown; charset=utf-8") else Results.Text(json, "application/json")
                else
                    // stored recaps hold admin detail; viewers get the repo sections re-rendered
                    match JsonSerializer.Deserialize<Recap.Report>(json, Config.jsonOptions) with
                    | null -> return Results.NotFound()
                    | r ->
                        let r = redactRecap r
                        return if wantsMarkdown ctx then Results.Text(Recap.markdown r, "text/markdown; charset=utf-8") else Results.Json(r, Config.jsonOptions)
        })) |> ignore

/// Our Tailscale addresses, retrying while tailscaled comes up (e.g. at boot).
let tailnetAddresses (ts: Tailscale.Client) =
    task {
        let deadline = DateTime.UtcNow.AddMinutes 2.
        let mutable found: IPAddress[] = [||]
        let mutable lastError = ""
        while found.Length = 0 && DateTime.UtcNow < deadline do
            try
                let! ips = ts.SelfAddresses()
                found <- ips
                if ips.Length = 0 then lastError <- "tailscale reports no addresses (logged out?)"
            with ex ->
                lastError <- ex.Message
            if found.Length = 0 then
                do! Task.Delay(TimeSpan.FromSeconds 5.)
        return if found.Length > 0 then Ok found else Error lastError
    }

let listenUrl (ip: IPAddress) (port: int) =
    if ip.AddressFamily = Sockets.AddressFamily.InterNetworkV6 then $"http://[{ip}]:{port}" else $"http://{ip}:{port}"
