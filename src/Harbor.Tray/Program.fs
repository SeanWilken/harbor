/// harbor-tray: the tether light in the tray, plus Open harbor / Pair a device / Pair with hub.
module Harbor.Tray.Program

open System
open System.Diagnostics
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Themes.Fluent
open Avalonia.Threading
open Harbor

/// What the menu shows: this machine's agent(s) and, on the hub machine, the fleet.
type View =
    { Agents: AgentStatus.Status[]
      Fleet: Hub.Fleet option }

let private shade (now: DateTimeOffset) (v: View) =
    let tethers = v.Agents |> Array.map (AgentStatus.tether now)
    if tethers |> Array.exists (function AgentStatus.Untethered _ -> true | _ -> false) then Icons.Red
    elif tethers |> Array.exists (function AgentStatus.Waiting _ -> true | _ -> false) then Icons.Amber
    elif tethers.Length > 0 then Icons.Green
    elif v.Fleet.IsSome then Icons.Green // hub without its own agent
    else Icons.Grey

let private agentLine (now: DateTimeOffset) (s: AgentStatus.Status) =
    let target = if s.Hub = "local" then "this hub" else s.Hub
    match AgentStatus.tether now s with
    | AgentStatus.Tethered -> $"● {s.Machine}: tethered to {target}"
    | AgentStatus.Waiting why -> $"◐ {s.Machine}: {why}"
    | AgentStatus.Untethered why -> $"○ {s.Machine}: {why}"

let private openUrl (url: string) =
    try
        if OperatingSystem.IsWindows() then Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore
        else Process.Start("xdg-open", url) |> ignore
    with ex ->
        eprintfn $"open {url}: {ex.Message}"

/// The machine name pairing should use: the agent's own status, else its config, else the hostname.
let private argAfter (name: string) =
    let args = Environment.GetCommandLineArgs()
    args |> Array.tryFindIndex ((=) name) |> Option.bind (fun i -> Array.tryItem (i + 1) args)

/// --config: the agent config the tray pairs with (and passes to a supervised agent).
let private configPath = argAfter "--config"

let private machineName (v: View) =
    match v.Agents |> Array.tryFind (fun a -> a.Hub <> "local") with
    | Some a -> a.Machine
    | None ->
        match Config.load configPath with
        | Ok(cfg, _) -> cfg.Machine
        | Error _ -> Config.defaults.Machine

type App() =
    inherit Application()

    let mutable view = { Agents = [||]; Fleet = None }
    /// --agent <path>: run harbor-agent hidden under the tray (Windows login start-up).
    let agent = argAfter "--agent" |> Option.map (fun exe -> Supervisor.Agent(exe, configPath))
    let mutable lastMenu = ""

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Dark

    member private this.Menu(tray: TrayIcon) =
        let now = DateTimeOffset.Now
        let lines =
            [ yield! view.Agents |> Array.map (agentLine now)
              match view.Fleet with
              | Some f ->
                  let stale = if f.Stale > 0 then sprintf ", %d stale" f.Stale else ""
                  let pending = if f.Pending > 0 then sprintf ", %d pending" f.Pending else ""
                  yield $"hub: {f.Online}/{f.Total} online{stale}{pending}"
              | None -> ()
              if view.Agents.Length = 0 && view.Fleet.IsNone then yield "no harbor agent or hub on this machine" ]
        let key = String.Join("|", lines)
        tray.Icon <- Icons.icon (shade now view)
        tray.ToolTipText <- "harbor\n" + String.Join("\n", lines)
        if key <> lastMenu then // rebuilding an unchanged menu makes some trays flicker
            lastMenu <- key
            let menu = NativeMenu()
            for l in lines do menu.Items.Add(NativeMenuItem(l, IsEnabled = false))
            // this machine's harbor build, and whether the hub knows of newer commits
            let newer = view.Agents |> Array.tryPick (fun a -> a.Update)
            let build =
                match newer with
                | Some u ->
                    let plural = if u.Behind = 1 then "" else "s"
                    $"harbor {BuildInfo.short}: {u.Behind} newer commit{plural} (latest {u.Latest.Substring(0, 7)})"
                | None -> $"harbor {BuildInfo.short}"
            menu.Items.Add(NativeMenuItem(build, IsEnabled = false))
            menu.Items.Add(NativeMenuItemSeparator())
            let item (header: string) (onClick: unit -> unit) =
                let i = NativeMenuItem(header)
                i.Click.Add(fun _ -> onClick ())
                menu.Items.Add i
            let hubUrl =
                match view.Fleet, view.Agents |> Array.tryFind (fun a -> a.Hub <> "local") with
                | Some _, _ -> Some Hub.loopback
                | None, Some a -> Some a.Hub
                | None, None -> None
            hubUrl |> Option.iter (fun u -> item "Open harbor" (fun () -> openUrl u))
            // this machine's agent(s) at once, then every linked machine through the hub
            if view.Agents.Length > 0 || hubUrl.IsSome then
                item "Sync now" (fun () ->
                    for a in view.Agents do if a.Hub <> "local" then AgentStatus.requestSync a.Machine
                    hubUrl |> Option.iter (fun u -> Hub.sync u |> ignore))
            if view.Fleet.IsSome then item "Pair a device…" (fun () -> (Windows.pairDevice ()).Show())
            if view.Agents |> Array.exists (fun a -> a.Hub <> "local" && a.HubStatus <> "approved") || view.Agents.Length = 0 then
                let hub = view.Agents |> Array.tryPick (fun a -> if a.Hub <> "local" then Some a.Hub else None)
                item "Pair with hub…" (fun () -> (Windows.pairWithHub (machineName view) hub).Show())
            menu.Items.Add(NativeMenuItemSeparator())
            // always offered: the script pulls from the checkout's remote, which may be ahead of
            // what the hub has seen
            Update.script () |> Option.iter (fun script ->
                let label = match newer with Some u -> $"Update harbor… ({u.Behind} newer)" | None -> "Update harbor…"
                item label (fun () -> Update.run script))
            agent |> Option.iter (fun a ->
                item "Restart agent" a.Restart
                item "Agent log" (fun () -> openUrl a.Log))
            let login = NativeMenuItem("Start at login", ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Autostart.isEnabled ())
            login.Click.Add(fun _ ->
                try
                    if Autostart.isEnabled () then Autostart.disable () else Autostart.enable ()
                with ex ->
                    eprintfn $"start at login: {ex.Message}"
                login.IsChecked <- Autostart.isEnabled ())
            menu.Items.Add login
            item (if agent.IsSome then "Quit (stops the agent)" else "Quit tray") (fun () ->
                agent |> Option.iter _.Stop()
                match this.ApplicationLifetime with
                | :? IClassicDesktopStyleApplicationLifetime as l -> l.Shutdown()
                | _ -> ())
            tray.Menu <- menu

    member private this.Refresh(tray: TrayIcon) =
        task {
            let! fleet = Hub.fleet ()
            let agents = AgentStatus.readAll ()
            Dispatcher.UIThread.Post(fun () ->
                view <- { Agents = agents; Fleet = fleet }
                this.Menu tray)
        }
        |> ignore

    override this.OnFrameworkInitializationCompleted() =
        match Environment.GetCommandLineArgs() |> Array.tryFindIndex ((=) "--snapshot") with
        | Some i ->
            // render icons and windows to PNGs, then exit: lets us check the look without a screen grab
            let dir = Environment.GetCommandLineArgs() |> Array.tryItem (i + 1) |> Option.defaultValue "."
            Directory.CreateDirectory dir |> ignore
            for s in [ Icons.Green; Icons.Amber; Icons.Red; Icons.Grey ] do
                (Icons.bitmap s).Save(Path.Combine(dir, $"icon-{s}.png"))
            Windows.snapshot (Windows.pairWithHub "pi" (Some "http://100.101.1.10:7420")) (Path.Combine(dir, "pair-with-hub.png"))
            let w = Windows.pairDevice ()
            DispatcherTimer.RunOnce((fun () ->
                Windows.snapshot w (Path.Combine(dir, "pair-device.png"))
                match this.ApplicationLifetime with
                | :? IClassicDesktopStyleApplicationLifetime as l -> l.Shutdown()
                | _ -> ()), TimeSpan.FromSeconds 3.) |> ignore
        | None ->
            let tray = new TrayIcon(Icon = Icons.icon Icons.Grey, ToolTipText = "harbor", IsVisible = true)
            let icons = TrayIcons()
            icons.Add tray
            TrayIcon.SetIcons(this, icons)
            this.Menu tray
            this.Refresh tray
            agent |> Option.iter (fun a ->
                AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> a.Stop())
                a.Run() |> ignore)
            let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 5.)
            timer.Tick.Add(fun _ -> this.Refresh tray)
            timer.Start()
        base.OnFrameworkInitializationCompleted()

[<EntryPoint; STAThread>]
let main argv =
    AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime(argv, ShutdownMode.OnExplicitShutdown)
