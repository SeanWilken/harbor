/// harbor-agent: scan this machine and report to a hub. No listener of any kind.
module Harbor.AgentMain

open System
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Harbor

let private usage =
    """usage: harbor-agent [run|status|pair <code>|snapshot|manifest] [--config <path>]
  run          scan and push to agent.hub until stopped (default)
  status       is this machine tethered to its hub? exit 0 yes, 1 waiting, 2 no
  pair <code>  pair with the hub using a code from its "Pair a device"; the running
               agent sends it with its next push (add --hub <url> to set the hub too)
  snapshot     print one snapshot as JSON and exit (what a push would carry)
  manifest     print the installed-software manifest as JSON and exit"""

let private arg (name: string) (argv: string[]) =
    argv |> Array.tryFindIndex ((=) name) |> Option.bind (fun i -> Array.tryItem (i + 1) argv)

let private print (value: 'T) =
    let opts = JsonSerializerOptions(Config.jsonOptions, WriteIndented = true)
    printfn "%s" (JsonSerializer.Serialize(value, opts))

/// Exit code for "config changed, start me again": the tray restarts the agent after any exit,
/// systemd (Restart=on-failure) after a non-zero one.
let private restartCode = 75

/// Completes when the config file changes to text that still loads. Polls rather than using a file
/// watcher: editors save by replacing the file, which watchers report inconsistently. An edit that
/// doesn't load is reported once and ignored, so a half-saved file never stops a running agent.
let private configChanged (path: string) (stop: CancellationToken) : Task =
    let read () = try IO.File.ReadAllText path with _ -> ""
    task {
        let mutable seen = read ()
        let mutable changed = false
        while not changed && not stop.IsCancellationRequested do
            do! Task.Delay(TimeSpan.FromSeconds 5., stop).ContinueWith(fun (_: Task) -> ())
            let now = read ()
            if now <> seen && now <> "" then
                seen <- now
                match Config.load (Some path) with
                | Ok _ -> changed <- true
                | Error e -> eprintfn $"harbor-agent: {path} changed but doesn't load, keeping the running config: {e}"
    }

/// Until SIGTERM (systemd stop) or Ctrl+C, or until the config file changes (then exits to be restarted).
let private runUntilStopped (cfg: Config.Config) (source: string option) (a: Config.AgentConfig) =
    use stop = new CancellationTokenSource()
    let onSignal = Action<PosixSignalContext>(fun c -> c.Cancel <- true; stop.Cancel())
    use _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onSignal)
    use _int = PosixSignalRegistration.Create(PosixSignal.SIGINT, onSignal)
    let waker = Agent.Waker()
    let store, reporter = Agent.start cfg waker (Agent.httpSink a.Hub) stop.Token
    AgentLink.run a.Hub cfg.Machine Agent.backoff (Agent.onWake store waker) reporter.Linked stop.Token |> ignore
    eprintfn $"harbor-agent '{cfg.Machine}': pushing to {a.Hub} every {a.PushSeconds}s"
    let changed = match source with Some p -> configChanged p stop.Token | None -> Task.Delay(Timeout.Infinite, stop.Token)
    try
        Task.WhenAny(Task.Delay(Timeout.Infinite, stop.Token), changed).Wait()
    with _ ->
        ()
    if changed.IsCompletedSuccessfully && not stop.IsCancellationRequested then
        eprintfn "harbor-agent: config changed; exiting to restart with it"
        stop.Cancel()
        restartCode
    else
        eprintfn "harbor-agent: stopped"
        0

/// Human-readable tether state from the status file(s); the exit code suits scripts and login banners.
let private status (cfg: Config.Config) =
    let now = DateTimeOffset.Now
    match AgentStatus.read (AgentStatus.statusPath cfg.Machine) with
    | None ->
        printfn $"harbor: '{cfg.Machine}' has no status yet (agent never ran here?)"
        2
    | Some s ->
        let since = s.LastPushAt |> Option.map (fun t -> $", last push {int (now - t).TotalSeconds}s ago") |> Option.defaultValue ""
        match AgentStatus.tether now s with
        | AgentStatus.Tethered ->
            printfn $"harbor: '{s.Machine}' tethered to {s.Hub} (linked{since})"
            0
        | AgentStatus.Waiting why ->
            printfn $"harbor: '{s.Machine}' waiting: {why}{since}"
            1
        | AgentStatus.Untethered why ->
            printfn $"harbor: '{s.Machine}' NOT tethered: {why}"
            2

/// Leave the code for the running agent. Setting the hub only creates a config when there is none:
/// rewriting an existing one would drop its comments.
let private pair (argv: string[]) (cfg: Config.Config) (source: string option) =
    match argv |> Array.tryItem 1 |> Option.map AgentStatus.normalizeCode with
    | Some code when code.Length = 8 ->
        AgentStatus.savePairCode cfg.Machine code
        match arg "--hub" argv, source with
        | Some hub, None ->
            let path = defaultArg (arg "--config" argv) "harbor.json"
            let json = JsonSerializer.Serialize({| machine = cfg.Machine; roots = cfg.Roots; agent = {| hub = hub.TrimEnd '/' |} |}, JsonSerializerOptions(WriteIndented = true))
            IO.File.WriteAllText(path, json)
            printfn $"wrote {path} (hub {hub}); start the agent: harbor-agent run --config {path}"
        | Some hub, Some p when cfg.Agent |> Option.forall (fun a -> a.Hub <> hub.TrimEnd '/') ->
            let current = cfg.Agent |> Option.map _.Hub |> Option.defaultValue "no hub"
            printfn $"note: {p} points at {current}; set agent.hub to {hub} there"
        | _ -> ()
        printfn $"pairing code saved for '{cfg.Machine}'. The agent sends it with its next push; then `harbor-agent status`."
        0
    | _ ->
        eprintfn "usage: harbor-agent pair <code> [--hub http://<hub>:7420]   (the 8-character code from the hub, e.g. 7KQ4-M2XP)"
        2

[<EntryPoint>]
let main argv =
    let command =
        match Array.tryHead argv with
        | Some c when not (c.StartsWith "--") -> c
        | _ -> "run"
    match command, Config.load (arg "--config" argv) with
    | ("-h" | "help"), _ ->
        eprintfn "%s" usage
        0
    | "pair", Error _ when (arg "--hub" argv).IsSome -> pair argv Config.defaults None // first install: no config yet
    | _, Error e ->
        eprintfn $"{e}"
        2
    | "run", Ok(cfg, source) ->
        match cfg.Agent with
        | Some a -> runUntilStopped cfg (source |> Option.map IO.Path.GetFullPath) a
        | None ->
            eprintfn "harbor-agent needs `agent.hub` in its config, e.g. \"agent\": { \"hub\": \"http://devbox:7420\" }"
            2
    | "status", Ok(cfg, _) -> status cfg
    | "pair", Ok(cfg, source) -> pair argv cfg source
    | "snapshot", Ok(cfg, _) ->
        let store = Inventory.Store cfg
        store.Rescan().Wait()
        print (store.Snapshot().Result)
        0
    | "manifest", Ok(cfg, _) ->
        let store = Inventory.Store cfg
        store.Rescan().Wait()
        print (store.RefreshManifest().Result)
        0
    | other, _ ->
        eprintfn $"unknown command '{other}'\n{usage}"
        2
