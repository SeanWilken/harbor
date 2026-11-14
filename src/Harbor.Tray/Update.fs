/// "Update harbor…": pull and reinstall from the checkout the installer ran from. Windows reads
/// %LOCALAPPDATA%\harbor\install.json (deploy\install-windows.ps1) and runs update-windows.ps1 in a
/// PowerShell window; Linux reads ~/.local/share/harbor/install.json (deploy/install-hub.sh) and runs
/// update-linux.sh in a terminal. Either installer restarts the tray itself.
module Harbor.Tray.Update

open System
open System.Diagnostics
open System.IO
open System.Text.Json

let private installInfo () =
    if OperatingSystem.IsWindows() then
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "harbor", "install.json")
    else
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share", "harbor", "install.json")

let private scriptName = if OperatingSystem.IsWindows() then "update-windows.ps1" else "update-linux.sh"

/// The update script, when this install recorded its checkout and the checkout still exists.
let script () : string option =
    try
        use doc = JsonDocument.Parse(File.ReadAllText(installInfo ()))
        match doc.RootElement.TryGetProperty "repo" with
        | true, r ->
            let s = Path.Combine(r.GetString() |> nonNull, "deploy", scriptName)
            if File.Exists s then Some s else None
        | _ -> None
    with _ ->
        None

/// Linux: run a command in a visible terminal window, kept open afterwards so its output can be read.
let inTerminal (argv: string list) =
    let quoted = argv |> List.map (fun a -> "'" + a.Replace("'", "'\\''") + "'") |> String.concat " "
    let shell = [ "sh"; "-c"; quoted + "; echo; printf 'Press Enter to close. '; read _" ]
    let candidates =
        [ "gnome-terminal", [ "--" ] @ shell
          "x-terminal-emulator", [ "-e" ] @ shell
          "konsole", [ "-e" ] @ shell
          "xterm", [ "-e" ] @ shell ]
    let onPath (exe: string) =
        (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "").Split ':'
        |> Array.exists (fun d -> d <> "" && File.Exists(Path.Combine(d, exe)))
    match candidates |> List.tryFind (fst >> onPath) with
    | Some(exe, args) ->
        let psi = ProcessStartInfo(exe, UseShellExecute = false)
        for a in args do psi.ArgumentList.Add a
        Process.Start psi |> ignore
    | None -> eprintfn "no terminal found (tried gnome-terminal, x-terminal-emulator, konsole, xterm)"

let run (script: string) =
    if OperatingSystem.IsWindows() then
        let psi = ProcessStartInfo("powershell.exe", UseShellExecute = true)
        for a in [ "-NoExit"; "-ExecutionPolicy"; "Bypass"; "-File"; script ] do psi.ArgumentList.Add a
        Process.Start psi |> ignore
    else
        inTerminal [ "sh"; script ]
