module Harbor.Program

open System
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Hosting
open Harbor.Config

type Mode =
    | AgentMode
    | HubMode
    | BothMode
    | MigrateMode

let private usage = "usage: harbor [agent|hub|both|migrate] [--config <path>] [--ui <dir>]   (default: both)"

let private arg (name: string) (argv: string[]) =
    argv |> Array.tryFindIndex ((=) name) |> Option.bind (fun i -> Array.tryItem (i + 1) argv)

let private mode (argv: string[]) =
    match Array.tryHead argv with
    | None -> Ok BothMode
    | Some a when a.StartsWith "--" -> Ok BothMode
    | Some "agent" -> Ok AgentMode
    | Some "hub" -> Ok HubMode
    | Some "both" -> Ok BothMode
    | Some "migrate" -> Ok MigrateMode
    | Some other -> Error $"unknown mode '{other}'\n{usage}"

let private serveUi (app: WebApplication) (dir: string) =
    if Directory.Exists dir then
        let files = new PhysicalFileProvider(Path.GetFullPath dir)
        app.UseDefaultFiles(DefaultFilesOptions(FileProvider = files)) |> ignore
        app.UseStaticFiles(StaticFileOptions(FileProvider = files)) |> ignore
    else
        eprintfn $"no UI build at {dir}; run `bun run build` in ui/ (API still served)"

/// This machine's raw snapshot and an on-demand rescan: admins (or loopback on an agent) only.
let private mapLocal (app: WebApplication) (store: Inventory.Store) =
    app.MapGet("/api/snapshot", Func<HttpContext, Task<IResult>>(fun ctx -> task {
        if not (Hub.adminOrLoopback ctx) then return Results.StatusCode 403
        else
            let! s = store.Snapshot()
            return Results.Ok s })) |> ignore
    app.MapPost("/api/scan", Func<HttpContext, Task<IResult>>(fun ctx -> task {
        if not (Hub.adminOrLoopback ctx) then return Results.StatusCode 403
        else
            do! store.Rescan()
            return Results.NoContent() })) |> ignore

let private builder (argv: string[]) (urls: string list) =
    let b = WebApplication.CreateBuilder argv
    b.Services.ConfigureHttpJsonOptions(fun o ->
        for c in Config.jsonOptions.Converters do
            o.SerializerOptions.Converters.Add c
        o.SerializerOptions.PropertyNamingPolicy <- Text.Json.JsonNamingPolicy.CamelCase)
    |> ignore
    b.WebHost.UseUrls(Array.ofList urls) |> ignore
    // a full push with a manifest is ~200 KB; nothing legitimate comes near this
    b.WebHost.ConfigureKestrel(fun o -> o.Limits.MaxRequestBodySize <- Nullable(4L * 1024L * 1024L)) |> ignore
    b

let private startAgent (app: WebApplication) (cfg: Config) (waker: Agent.Waker) (sink: Agent.Sink) =
    Agent.start cfg waker sink app.Lifetime.ApplicationStopping

let private runAgent argv (cfg: Config) =
    match cfg.Agent with
    | None ->
        eprintfn "agent mode needs `agent.hub` in the config, e.g. \"agent\": { \"hub\": \"http://devbox:7420\" }"
        2
    | Some a ->
        let app = (builder argv [ cfg.Listen ]).Build()
        let waker = Agent.Waker()
        let store, reporter = startAgent app cfg waker (Agent.httpSink a.Hub)
        AgentLink.run a.Hub cfg.Machine Agent.backoff (Agent.onWake store waker) reporter.Linked app.Lifetime.ApplicationStopping |> ignore
        mapLocal app store // loopback-only, for debugging an agent box
        eprintfn $"harbor agent '{cfg.Machine}': pushing to {a.Hub} every {a.PushSeconds}s"
        app.Run()
        0

let private runHub argv (cfg: Config) (hub: HubConfig) (withAgent: bool) =
    (Migrations.run hub.Database Migrations.defaultDir).GetAwaiter().GetResult()
    let tailscale =
        if not hub.Tailnet then
            Ok None
        else
            Tailscale.findSocket hub.TailscaleSocket
            |> Result.map (fun s -> Some(new Tailscale.Client(s)))
    match tailscale with
    | Error e ->
        eprintfn $"{e} (set hub.tailscaleSocket, or hub.tailnet: false for loopback only)"
        2
    | Ok ts ->
        let port = Uri(cfg.Listen).Port
        let tailnetUrls =
            match ts with
            | None -> Ok []
            | Some c ->
                (Hub.tailnetAddresses c).GetAwaiter().GetResult()
                |> Result.map (Array.map (fun ip -> Hub.listenUrl ip port) >> List.ofArray)
        match tailnetUrls with
        | Error e ->
            eprintfn $"no tailscale addresses to listen on: {e}"
            2
        | Ok urls ->
            // Serve's listener must be loopback: only tailscaled may reach it.
            let serve = hub.ServeListen |> Option.map Uri |> Option.filter (fun u -> u.IsLoopback)
            hub.ServeListen |> Option.iter (fun s -> if serve.IsNone then eprintfn $"hub.serveListen {s} ignored: it must be a loopback address")
            let app = (builder argv (cfg.Listen :: urls @ (serve |> Option.map string |> Option.toList))).Build()
            use db = new Storage.Db(hub.Database)
            Hub.useBrowserSafety app
            Hub.useGuard app ts (serve |> Option.map _.Port) hub.Admins hub.Viewers
            serveUi app (arg "--ui" argv |> Option.defaultValue (Path.Combine("ui", "dist")))
            let links = AgentLink.Registry()
            app.UseWebSockets() |> ignore
            let agentUrl = urls |> List.tryFind (fun u -> not (u.Contains "[")) |> Option.map (fun u -> u.TrimEnd '/')
            Hub.mapApi app db links hub.Recap agentUrl
            Hub.recapLoop db hub.Recap app.Lifetime.ApplicationStopping |> ignore
            Hub.pruneLoop db app.Lifetime.ApplicationStopping |> ignore
            db.JobQueued.Add(fun (nodeId, name) -> if nodeId <> "local" then links.Wake(nodeId, name, "job"))
            if withAgent then
                let waker = Agent.Waker()
                db.JobQueued.Add(fun (nodeId, _) -> if nodeId = "local" then waker.Wake())
                let store, _ = startAgent app cfg waker (fun push -> db.Ingest(Storage.LocalAgent, push))
                links.SetLocal(Agent.onWake store waker)
                mapLocal app store
            let mode = if withAgent then "hub + agent" else "hub"
            eprintfn $"""harbor {mode}: listening on {String.Join(", ", cfg.Listen :: urls)}"""
            serve |> Option.iter (fun u -> eprintfn $"harbor: Tailscale Serve front end expected on {u} (identity from Tailscale-User-Login)")
            app.Run()
            0

[<EntryPoint>]
let main argv =
    match mode argv, Config.load (arg "--config" argv) with
    | Error e, _
    | _, Error e ->
        eprintfn $"{e}"
        2
    | Ok m, Ok(cfg, source) ->
        eprintfn $"""harbor: config {defaultArg source "(defaults)"}"""
        match m, cfg.Hub with
        | AgentMode, _ -> runAgent argv cfg
        | MigrateMode, Some hub ->
            (Migrations.run hub.Database Migrations.defaultDir).GetAwaiter().GetResult()
            0
        | HubMode, Some hub -> runHub argv cfg hub false
        | BothMode, Some hub -> runHub argv cfg hub true
        | _, None ->
            eprintfn $"{m} needs `hub.database` in the config (or run `harbor agent`)"
            2
