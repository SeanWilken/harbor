/// The tray's "Restart agent" and "Agent log": the agent it supervises (Windows, --agent), else the
/// harbor systemd user unit that runs it (Linux: harbor-hub runs the hub and its agent together).
module Harbor.Tray.AgentControl

open System
open System.Diagnostics
open System.IO

type Control =
    { RestartLabel: string
      Restart: unit -> unit
      ShowLog: unit -> unit }

let supervised (a: Supervisor.Agent) (openUrl: string -> unit) =
    { RestartLabel = "Restart agent"
      Restart = a.Restart
      ShowLog = fun () -> openUrl a.Log }

let private unitDir () =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "systemd", "user")

/// The installed unit that runs this machine's agent; the hub's comes first, since it runs `both`.
let installedUnit () : string option =
    if OperatingSystem.IsWindows() then None
    else [ "harbor-hub"; "harbor-agent" ] |> List.tryFind (fun u -> File.Exists(Path.Combine(unitDir (), u + ".service")))

let private systemctl (args: string list) =
    try
        let psi = ProcessStartInfo("systemctl", UseShellExecute = false)
        for a in "--user" :: args do psi.ArgumentList.Add a
        Process.Start psi |> ignore
    with ex ->
        eprintfn $"systemctl {args}: {ex.Message}"

let ofUnit (unitName: string) =
    { RestartLabel = if unitName = "harbor-hub" then "Restart hub + agent" else "Restart agent"
      Restart = fun () -> systemctl [ "restart"; unitName ]
      ShowLog = fun () -> Update.inTerminal [ "journalctl"; "--user"; "-u"; unitName; "-n"; "200"; "-f" ] }
