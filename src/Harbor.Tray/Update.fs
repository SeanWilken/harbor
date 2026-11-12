/// "Update harbor…" on Windows: pull and reinstall from the checkout the installer ran from
/// (%LOCALAPPDATA%\harbor\install.json, written by deploy\install-windows.ps1). Runs visibly in a
/// PowerShell window; the installer stops and restarts the tray itself.
module Harbor.Tray.Update

open System
open System.Diagnostics
open System.IO
open System.Text.Json

/// The update script, when this install came from install-windows.ps1 and the checkout still exists.
let script () : string option =
    if not (OperatingSystem.IsWindows()) then None
    else
        try
            let info = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "harbor", "install.json")
            use doc = JsonDocument.Parse(File.ReadAllText info)
            match doc.RootElement.TryGetProperty "repo" with
            | true, r ->
                let s = Path.Combine(r.GetString() |> nonNull, "deploy", "update-windows.ps1")
                if File.Exists s then Some s else None
            | _ -> None
        with _ ->
            None

let run (script: string) =
    let psi = ProcessStartInfo("powershell.exe", UseShellExecute = true)
    for a in [ "-NoExit"; "-ExecutionPolicy"; "Bypass"; "-File"; script ] do psi.ArgumentList.Add a
    Process.Start psi |> ignore
