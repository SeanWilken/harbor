/// Roles: what this machine is doing right now (general, worker, gaming, media...), one at a time.
/// The machine's own config declares them and what switching into each one does; the hub can only
/// pick a declared name. The current role is saved next to the agent's status file, so it survives
/// restarts, and is reported in every snapshot.
module Harbor.Roles

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Harbor.Domain

type Saved = { Name: string; Since: DateTimeOffset; By: string; Applied: string[] }

let statePath (machine: string) = Path.Combine(AgentStatus.directory (), $"role-{AgentStatus.safe machine}.json")

let private load (machine: string) : Saved option =
    try
        let p = statePath machine
        if File.Exists p then JsonSerializer.Deserialize<Saved>(File.ReadAllText p, Config.jsonOptions) |> Option.ofObj else None
    with _ ->
        None

let private save (machine: string) (s: Saved) =
    try AgentStatus.writeAtomic (statePath machine) (JsonSerializer.Serialize(s, Config.jsonOptions))
    with ex -> eprintfn $"role state: {ex.Message}"

let declared (roles: Config.RoleConfig[]) : RoleInfo[] = roles |> Array.map (fun r -> { Name = r.Name; Work = r.Work })

/// The saved role while the config still declares it, else the first declared one (the default).
let current (roles: Config.RoleConfig[]) (saved: Saved option) (now: DateTimeOffset) : RoleState option =
    let state (r: Config.RoleConfig) since by applied =
        { Name = r.Name; Work = r.Work; Since = since; By = by; Declared = declared roles; Applied = applied }
    match roles |> Array.tryHead with
    | None -> None
    | Some first ->
        match saved |> Option.bind (fun s -> roles |> Array.tryFind (fun r -> r.Name = s.Name) |> Option.map (fun r -> r, s)) with
        | Some(r, s) -> Some(state r s.Since s.By s.Applied)
        | None -> Some(state first now "default" [||])

/// powercfg's built-in schemes are named by alias, so this works on any Windows language.
let powerCommand (windows: bool) (profile: string) : string list =
    if windows then
        let scheme =
            match profile with
            | "performance" -> "SCHEME_MIN"
            | "power-saver" -> "SCHEME_MAX"
            | _ -> "SCHEME_BALANCED"
        [ "powercfg"; "/setactive"; scheme ]
    else
        [ "powerprofilesctl"; "set"; profile ]

let private firstLine (s: string) = s.Trim().Split('\n') |> Array.head |> _.Trim()

let private command (label: string) (argv: string list) =
    task {
        match! Proc.run (TimeSpan.FromSeconds 20.) None [] argv with
        | Ok o when o.Code = 0 -> return $"{label}: ok", true
        | Ok o ->
            let said = firstLine (if o.Err.Trim() = "" then o.Out else o.Err)
            return $"{label}: failed ({said})", false
        | Error e -> return $"{label}: failed ({e})", false
    }

let private unit (verb: string) (name: string) =
    if OperatingSystem.IsWindows() then Task.FromResult($"{verb} {name}: systemd units are Linux only", false)
    else command $"{verb} {name}" [ "systemctl"; "--user"; verb; name ]

/// Other processes with this name (never harbor itself, which may be one of several dotnet hosts).
let private running (name: string) =
    Process.GetProcessesByName name |> Array.filter (fun p -> p.Id <> Environment.ProcessId)

/// Opened like a double-click (apps, files, URLs such as steam://); args mean a plain executable.
let private startCommand (cmd: string) (args: string[]) : Task<string * bool> =
    let isUrl = cmd.Contains "://"
    let name = if isUrl then cmd else (match Path.GetFileNameWithoutExtension cmd with null | "" -> cmd | n -> n)
    let others = if isUrl then [||] else running name
    try
        if others.Length > 0 then
            Task.FromResult($"start {name}: already running", true)
        else
            let psi = ProcessStartInfo(cmd, UseShellExecute = (args.Length = 0))
            args |> Array.iter psi.ArgumentList.Add
            match Process.Start psi with
            | null -> ()
            | p -> p.Dispose()
            Task.FromResult($"start {name}: started", true)
    finally
        for p in others do p.Dispose()

/// Asked to close, never killed: SIGTERM on Linux, the main window's close on Windows.
let private stopProcess (name: string) =
    task {
        let ps = running name
        try
            if ps.Length = 0 then return $"stop {name}: not running", true
            elif OperatingSystem.IsWindows() then
                let left = ps |> Array.filter (fun p -> not (p.CloseMainWindow())) |> Array.length
                if left = 0 then return $"stop {name}: asked to close", true
                else return $"stop {name}: {left} of {ps.Length} have no window to close; left running", false
            else
                return! command $"stop {name}" ("kill" :: "-TERM" :: [ for p in ps -> string p.Id ])
        finally
            for p in ps do p.Dispose()
    }

let private run (a: Config.RoleAction) (stopping: bool) : Task<string * bool> =
    let attempt (f: unit -> Task<string * bool>) label =
        task {
            try return! f ()
            with ex -> return $"{label}: failed ({ex.Message})", false
        }
    match a.Command, a.Unit, a.Process with
    | Some c, _, _ -> attempt (fun () -> startCommand c (defaultArg a.Args [||])) $"start {c}"
    | _, Some u, _ -> attempt (fun () -> unit (if stopping then "stop" else "start") u) u
    | _, _, Some p -> attempt (fun () -> stopProcess p) $"stop {p}"
    | _ -> Task.FromResult("empty action", false)

/// Stops first, then the power profile, then starts: room is made before anything new runs.
let apply (role: Config.RoleConfig) : Task<(string * bool) list> =
    task {
        let mutable out = []
        for a in role.Stop do
            let! r = run a true
            out <- r :: out
        match role.Power with
        | Some p ->
            let! r = command $"power {p}" (powerCommand (OperatingSystem.IsWindows()) p)
            out <- r :: out
        | None -> ()
        for a in role.Start do
            let! r = run a false
            out <- r :: out
        return List.rev out
    }

type Controller(machine: string, roles: Config.RoleConfig[], ?act: Config.RoleConfig -> Task<(string * bool) list>) =
    let act = defaultArg act apply
    let gate = new SemaphoreSlim(1, 1)
    let mutable state = current roles (load machine) DateTimeOffset.Now
    let changed = Event<RoleState option>()

    member _.State = state
    /// Work "none": harbor pauses its own fetches and manifest rebuilds too.
    member _.Quiet = state |> Option.exists (fun s -> s.Work = "none")
    member _.Declares(name: string) = roles |> Array.exists (fun r -> r.Name = name)
    member _.Changed = changed.Publish

    /// Switch (or re-apply the current role, e.g. to restart what it starts). The role changes even
    /// when an action fails: it states intent, and the failed lines say what didn't happen.
    member _.Switch(name: string, by: string) : Task<bool * string> =
        task {
            match roles |> Array.tryFind (fun r -> r.Name = name) with
            | None ->
                let names = String.Join(", ", roles |> Array.map _.Name)
                return false, (if roles.Length = 0 then "this machine declares no roles" else $"'{name}' isn't a role here ({names})")
            | Some role ->
                do! gate.WaitAsync()
                try
                    let! results = act role
                    let lines = results |> List.map fst |> Array.ofList
                    let since = match state with Some s when s.Name = name -> s.Since | _ -> DateTimeOffset.Now
                    let saved = { Name = name; Since = since; By = by; Applied = lines }
                    save machine saved
                    state <- current roles (Some saved) since
                    changed.Trigger state
                    let ok = results |> List.forall snd
                    let summary = if lines.Length = 0 then $"now {name}" else $"now {name}\n" + String.Join("\n", lines)
                    return ok, summary
                finally
                    gate.Release() |> ignore
        }
