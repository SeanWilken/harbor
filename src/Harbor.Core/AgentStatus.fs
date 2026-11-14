/// What an agent last knew about its hub, written to a small local file so indicators (the tray,
/// `harbor-agent status`, a Pi LED) can show "tethered" without the agent listening on anything.
/// Also the pending pairing code `harbor-agent pair` leaves for the running agent.
module Harbor.AgentStatus

open System
open System.IO
open System.Text.Json

type Status =
    { Machine: string
      /// Hub URL, or "local" for the hub's own in-process agent.
      Hub: string
      /// approved / pending / rejected as the hub last said; "unknown" before any answer.
      HubStatus: string
      Linked: bool
      LastPushAt: DateTimeOffset option
      LastError: string option
      /// A pairing code is waiting to be sent.
      Pairing: bool
      PushSeconds: int
      /// This agent's harbor build, and newer commits the hub knows of (None = up to date or unknown).
      Commit: string option
      Update: Domain.HarborUpdate option
      /// The current role and its work level, and the roles the tray may switch to (None = none declared).
      Role: string option
      Work: string option
      Roles: string[] option
      UpdatedAt: DateTimeOffset }

/// ~/.local/state/harbor (XDG_STATE_HOME) on Linux, %LOCALAPPDATA%\harbor on Windows.
let directory () =
    let root =
        if OperatingSystem.IsWindows() then Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData
        else
            match Environment.GetEnvironmentVariable "XDG_STATE_HOME" with
            | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "state")
            | d -> d
    Path.Combine(root, "harbor")

let safe (machine: string) = String(machine |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '-' then c else '_') |> Array.ofSeq)
let statusPath (machine: string) = Path.Combine(directory (), $"status-{safe machine}.json")
let pairPath (machine: string) = Path.Combine(directory (), $"pair-{safe machine}.txt")

/// Write via a temp file and rename, so a reader never sees half a file.
let writeAtomic (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    let tmp = path + ".tmp"
    File.WriteAllText(tmp, text)
    File.Move(tmp, path, true)

let write (s: Status) =
    try writeAtomic (statusPath s.Machine) (JsonSerializer.Serialize(s, Config.jsonOptions))
    with ex -> eprintfn $"status file: {ex.Message}"

let read (path: string) : Status option =
    try
        match JsonSerializer.Deserialize<Status>(File.ReadAllText path, Config.jsonOptions) with
        | null -> None
        | s -> Some s
    with _ ->
        None

/// Every agent status on this machine (usually one).
let readAll () : Status[] =
    let dir = directory ()
    if Directory.Exists dir then Directory.GetFiles(dir, "status-*.json") |> Array.choose read else [||]

type Tether =
    /// Approved, linked, recently pushed.
    | Tethered
    /// Pending approval, a pairing code waiting, or approved but the hub is out of reach.
    | Waiting of reason: string
    /// Rejected, never answered, or the agent isn't running.
    | Untethered of reason: string

/// The agent rewrites its file at least every push, so an old file means it isn't running.
let tether (now: DateTimeOffset) (s: Status) : Tether =
    let silent = now - s.UpdatedAt > TimeSpan.FromSeconds(float (max 90 (3 * s.PushSeconds)))
    if silent then Untethered $"agent not running (last update {s.UpdatedAt.LocalDateTime:g})"
    else
        match s.HubStatus with
        | "approved" when s.Linked -> Tethered
        | "approved" -> Waiting(defaultArg s.LastError "hub link down; pushes continue on their timer")
        | "pending" when s.Pairing -> Waiting "pairing code sent, waiting for the hub"
        | "pending" -> Waiting(defaultArg s.LastError "waiting for approval on the hub (or pair it with a code)")
        | "rejected" -> Untethered "rejected by the hub"
        | _ when s.Pairing -> Waiting "pairing code waiting to be sent"
        | _ -> Untethered(defaultArg s.LastError "hub has not answered yet")

/// One mutable view per agent; each change rewrites the file.
type Reporter(machine: string, hub: string, pushSeconds: int) =
    let gate = obj ()
    let mutable current =
        { Machine = machine; Hub = hub; HubStatus = "unknown"; Linked = (hub = "local"); LastPushAt = None
          LastError = None; Pairing = false; PushSeconds = pushSeconds; Commit = BuildInfo.commit; Update = None
          Role = None; Work = None; Roles = None; UpdatedAt = DateTimeOffset.Now }
    let update f =
        lock gate (fun () ->
            current <- { f current with UpdatedAt = DateTimeOffset.Now }
            write current)
    member _.Current = current
    member _.Pushed(hubStatus: string, pairing: bool, newer: Domain.HarborUpdate option) =
        // a rejected pairing code stays visible while the machine is still pending
        update (fun s ->
            { s with
                HubStatus = hubStatus
                LastPushAt = Some DateTimeOffset.Now
                LastError = (if hubStatus = "pending" && not pairing then s.LastError |> Option.filter (fun e -> e.StartsWith "pairing") else None)
                Pairing = pairing
                Update = newer })
    member _.Failed(error: string) = update (fun s -> { s with LastError = Some error })
    member _.Linked(up: bool) = update (fun s -> { s with Linked = up || hub = "local" })
    member _.Role(role: Domain.RoleState option) =
        update (fun s ->
            { s with
                Role = role |> Option.map _.Name
                Work = role |> Option.map _.Work
                Roles = role |> Option.map (fun r -> r.Declared |> Array.map _.Name) })

// ---- "sync now" from the tray: a request file the running agent picks up ----

let syncPath (machine: string) = Path.Combine(directory (), $"sync-{safe machine}")

let requestSync (machine: string) = writeAtomic (syncPath machine) (DateTimeOffset.Now.ToString "o")

/// True once per request: the file is removed when taken.
let takeSyncRequest (machine: string) =
    let p = syncPath machine
    try
        if File.Exists p then
            File.Delete p
            true
        else false
    with _ ->
        false

// ---- role switches from the tray: the request names the role; the running agent switches ----

let roleRequestPath (machine: string) = Path.Combine(directory (), $"role-request-{safe machine}")

let requestRole (machine: string) (role: string) = writeAtomic (roleRequestPath machine) role

/// The requested role, once: the file is removed when taken.
let takeRoleRequest (machine: string) : string option =
    let p = roleRequestPath machine
    try
        if File.Exists p then
            let role = File.ReadAllText(p).Trim()
            File.Delete p
            if role = "" then None else Some role
        else None
    with _ ->
        None

// ---- pairing codes waiting to be sent ----

/// Accepts "7kq4-m2xp", "7KQ4 M2XP" or "7KQ4M2XP".
let normalizeCode (code: string) =
    String(code.ToUpperInvariant() |> Seq.filter Char.IsLetterOrDigit |> Array.ofSeq)

let savePairCode (machine: string) (code: string) = writeAtomic (pairPath machine) (normalizeCode code)

let pendingPairCode (machine: string) =
    try
        let p = pairPath machine
        if File.Exists p then (match File.ReadAllText(p).Trim() with "" -> None | c -> Some c) else None
    with _ ->
        None

let clearPairCode (machine: string) =
    try File.Delete(pairPath machine) with _ -> ()
