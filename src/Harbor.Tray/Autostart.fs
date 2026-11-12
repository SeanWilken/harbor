/// "Start at login": a desktop entry in ~/.config/autostart on Linux, a Startup-folder shortcut on
/// Windows (the same one deploy/install-windows.ps1 creates). Re-created with this process's own
/// path and arguments, so `--agent` and `--config` carry over.
module Harbor.Tray.Autostart

open System
open System.IO
open System.Reflection

let private exe () = Environment.ProcessPath |> Option.ofObj |> Option.defaultValue "harbor-tray"

/// Arguments to start with again (debug flags dropped).
let private args () =
    Environment.GetCommandLineArgs() |> Array.skip 1 |> Array.filter (fun a -> a <> "--snapshot")

let private quote (s: string) = if s.Contains ' ' then $"\"{s}\"" else s

let private linuxPath () =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "autostart", "harbor-tray.desktop")

let private windowsPath () =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.Startup, "harbor.lnk")

let path () = if OperatingSystem.IsWindows() then windowsPath () else linuxPath ()

let isEnabled () = File.Exists(path ())

/// WScript.Shell through late-bound COM, so no extra package is needed.
let private writeShortcut (lnk: string) (target: string) (arguments: string) =
    match Type.GetTypeFromProgID "WScript.Shell" with
    | null -> failwith "WScript.Shell is not available"
    | t ->
        let shell = Activator.CreateInstance t |> nonNull
        let sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [| box lnk |]) |> nonNull
        let st = sc.GetType()
        let set (name: string) (value: string) = st.InvokeMember(name, BindingFlags.SetProperty, null, sc, [| box value |]) |> ignore
        set "TargetPath" target
        set "Arguments" arguments
        set "WorkingDirectory" (Path.GetDirectoryName target |> Option.ofObj |> Option.defaultValue "")
        set "Description" "harbor tray (runs the harbor agent)"
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, [||]) |> ignore

let enable () =
    let line = String.Join(" ", args () |> Array.map quote)
    if OperatingSystem.IsWindows() then writeShortcut (windowsPath ()) (exe ()) line
    else
        let p = linuxPath ()
        Directory.CreateDirectory(Path.GetDirectoryName p |> nonNull) |> ignore
        File.WriteAllText(
            p,
            String.concat "\n"
                [ "[Desktop Entry]"; "Type=Application"; "Name=harbor tray"
                  "Comment=Shows whether this machine is tethered to its harbor hub"
                  $"Exec={quote (exe ())} {line}".TrimEnd(); "Icon=network-server"
                  "X-GNOME-Autostart-enabled=true"; "Terminal=false"; "" ])

let disable () = if File.Exists(path ()) then File.Delete(path ())
