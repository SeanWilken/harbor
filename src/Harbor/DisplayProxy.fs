/// Desktop viewers under the hub's own address: /display/<machine>/… is passed, path unchanged, to
/// that machine's display viewer (its `access` entry of kind "display"), so one Home Screen app
/// reaches every machine. Each viewer still pairs devices itself: its cookie is scoped to
/// /display/<machine> and the hub only carries it, never reads, logs or stores it. Media (WebRTC)
/// goes device ↔ machine directly; only the page, API and signaling pass through here.
module Harbor.DisplayProxy

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Yarp.ReverseProxy.Forwarder
open Harbor.Domain

/// The /display/<machine> segment: harbor's machine name, as the viewer's own mount expects it.
let isMachineSegment (s: string) = Regex.IsMatch(s, "^[a-z0-9][a-z0-9-]{0,62}$")

/// Where to forward, if anywhere: the display entry's https origin, and only when its host is the
/// machine's own tailnet name. An agent can claim any URL; the hub never forwards to loopback (its
/// own admin listener), the LAN or another machine on that claim.
let upstream (machineHost: string option) (access: AccessEntry[] option) : Result<Uri, string> =
    match machineHost, access |> Option.bind (Array.tryFind (fun a -> a.Kind = "display")) |> Option.bind _.Url with
    | _, None -> Error "no desktop viewer configured for this machine"
    | None, Some _ -> Error "the hub doesn't know this machine's tailnet name"
    | Some host, Some url ->
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, NonNull u when u.Scheme = "https" && u.UserInfo = "" && u.Query = "" && u.AbsolutePath = "/" ->
            if String.Equals(u.Host, host.TrimEnd '.', StringComparison.OrdinalIgnoreCase) then Ok(Uri(u.GetLeftPart UriPartial.Authority))
            else Error $"the desktop viewer must be on the machine's own tailnet name ({host}), not {u.Host}"
        | _ -> Error "the desktop viewer URL must be a plain https origin"

/// Machine name → upstream, from the latest snapshots; cached briefly since one page load is dozens
/// of requests.
type Resolver(db: Storage.Db, hubHost: string option) =
    let ttl = TimeSpan.FromSeconds 30.
    let cache = ConcurrentDictionary<string, DateTimeOffset * Result<Uri, string>>()

    let lookup (name: string) =
        task {
            let! machines = db.Machines(fun _ -> false)
            match machines |> Array.tryFind (fun m -> m.Name = name && m.Status = "approved") with
            | None -> return Error $"no approved machine named {name}"
            | Some m ->
                let! snap = db.Snapshot m.Id
                let host = if m.Local then hubHost else m.NodeName
                return upstream host (snap |> Option.bind _.Access)
        }

    member _.Resolve(name: string) : Task<Result<Uri, string>> =
        task {
            match cache.TryGetValue name with
            | true, (at, r) when DateTimeOffset.UtcNow - at < ttl -> return r
            | _ ->
                let! r = lookup name
                cache[name] <- (DateTimeOffset.UtcNow, r)
                return r
        }

/// The default copy, minus the caller's Tailscale identity headers (from the hub's Serve front end):
/// the viewer authenticates its own way and has no use for who is behind the hub.
type private Transformer() =
    inherit HttpTransformer()
    override _.TransformRequestAsync(ctx, request, prefix, ct) =
        let copy = HttpTransformer.Default.TransformRequestAsync(ctx, request, prefix, ct)
        let strip () =
            for h in request.Headers |> Seq.map _.Key |> Seq.toArray do
                if h.StartsWith("Tailscale-", StringComparison.OrdinalIgnoreCase) then request.Headers.Remove h |> ignore
        if copy.IsCompletedSuccessfully then
            strip ()
            ValueTask.CompletedTask
        else
            ValueTask(task {
                do! copy
                strip () })

let private client () =
    new HttpMessageInvoker(
        new SocketsHttpHandler(
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds 10.,
            ActivityHeadersPropagator = null))

/// Browsers send `Service-Worker: script` when fetching a worker script to register.
let isServiceWorkerScript (headers: IHeaderDictionary) = headers["Service-Worker"].ToString() = "script"

let addServices (services: IServiceCollection) = services.AddHttpForwarder() |> ignore

let map (app: WebApplication) (resolver: Resolver) =
    let forwarder = app.Services.GetRequiredService<IHttpForwarder>()
    let http = client ()
    // signaling sockets stay open for the viewer's session (15 minutes by default)
    let config = ForwarderRequestConfig(ActivityTimeout = TimeSpan.FromMinutes 30.)
    let transformer = Transformer()
    let handler =
        RequestDelegate(fun ctx ->
            task {
                ctx.Response.Headers.CacheControl <- "no-store"
                match ctx.Request.RouteValues["machine"] with
                | :? string as _ when isServiceWorkerScript ctx.Request.Headers ->
                    // a viewer's worker would sit on harbor's origin and answer for the mount
                    // (caching, offline pages): pairing and signaling must always reach the viewer
                    ctx.Response.StatusCode <- 404
                    do! ctx.Response.WriteAsync "harbor doesn't serve service workers under /display/"
                | :? string as name when isMachineSegment name ->
                    match! resolver.Resolve name with
                    | Error why ->
                        ctx.Response.StatusCode <- 404
                        do! ctx.Response.WriteAsync why
                    | Ok origin ->
                        let! error = forwarder.SendAsync(ctx, origin.ToString().TrimEnd '/', http, config, transformer)
                        if error <> ForwarderError.None then eprintfn $"display {name}: {error}"
                | _ -> ctx.Response.StatusCode <- 404
            }
            :> Task)
    app.Map("/display/{machine}", handler) |> ignore
    app.Map("/display/{machine}/{**rest}", handler) |> ignore
