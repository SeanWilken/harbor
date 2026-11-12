/// Who may talk to the hub, and what happens to a push. Pure; Hub.fs supplies WhoIs results.
module Harbor.Enrol

open System
open System.Net
open Harbor.Tailscale

type Caller =
    | Local // loopback on the hub itself
    | Peer of Peer
    /// A browser user identified by Tailscale Serve (Tailscale-User-Login), on the hub's dedicated
    /// Serve listener only. Serve replaces any such header a client sends.
    | Served of login: string


type Access =
    | Allowed of Caller
    | Denied of reason: string

/// The Serve listener's caller. Tagged devices have no user login, so Serve sends none: refused.
let served (login: string) : Access =
    if String.IsNullOrWhiteSpace login then Denied "Tailscale Serve did not identify a user (tagged devices have none)"
    else Allowed(Served(login.Trim()))

type Status =
    | Pending
    | Approved
    | Rejected

module Status =
    let toDb = function Pending -> "pending" | Approved -> "approved" | Rejected -> "rejected"
    let ofDb = function "approved" -> Approved | "rejected" -> Rejected | _ -> Pending

type PushOutcome =
    /// Approved machine: store snapshot, rows and events.
    | Store
    /// Known but not approved yet: keep the snapshot for the approval preview, nothing else.
    | Hold
    /// First contact: create the machine as pending and keep the snapshot.
    | Enrol
    | Refuse of reason: string

let private cgnat = IPNetwork.Parse "100.64.0.0/10"
let private tailnet6 = IPNetwork.Parse "fd7a:115c:a1e0::/48"

let isTailnetAddress (ip: IPAddress) =
    let ip = if ip.IsIPv4MappedToIPv6 then ip.MapToIPv4() else ip
    cgnat.Contains ip || tailnet6.Contains ip

/// The address range is a cheap prefilter; WhoIs is the actual identity check.
let access (remote: IPAddress) (whois: Peer option) : Access =
    if IPAddress.IsLoopback remote then Allowed Local
    elif not (isTailnetAddress remote) then Denied "not a tailnet address"
    else
        match whois with
        | Some p -> Allowed(Peer p)
        | None -> Denied "tailscale does not know this peer"

/// Pushes must come over the tailnet so the sender is identified by WhoIs; the hub's own
/// agent pushes in-process instead of over loopback.
let push (caller: Caller) (existing: Status option) : PushOutcome =
    match caller, existing with
    | Local, _ -> Refuse "push over the tailnet, not loopback: the sender must be identifiable"
    | Served _, _ -> Refuse "agents push to the hub's tailnet address, not through Tailscale Serve"
    | Peer _, None -> Enrol
    | Peer _, Some Pending -> Hold
    | Peer _, Some Approved -> Store
    | Peer _, Some Rejected -> Refuse "machine was rejected on the hub"

/// Approve/reject: loopback, or a tailnet user listed in hub.admins.
let canAdmin (admins: string[]) (caller: Caller) =
    match caller with
    | Local -> true
    | Peer { Login = l }
    | Served l -> admins |> Array.exists (fun a -> String.Equals(a, l, StringComparison.OrdinalIgnoreCase))

type Role =
    | Admin
    | Viewer

let private listed (logins: string[]) (login: string) =
    logins |> Array.exists (fun a -> a = "*" || String.Equals(a, login, StringComparison.OrdinalIgnoreCase))

/// What a caller may do with the UI and read API. None = nothing (403); pushes are decided by `push`.
/// `viewers` may contain "*" for any identified tailnet user. Admins are never matched by "*".
let role (admins: string[]) (viewers: string[]) (caller: Caller) : Role option =
    let byLogin (l: string) =
        if admins |> Array.exists (fun a -> String.Equals(a, l, StringComparison.OrdinalIgnoreCase)) then Some Admin
        elif listed viewers l then Some Viewer
        else None
    match caller with
    | Local -> Some Admin
    | Peer p -> byLogin p.Login
    | Served l -> byLogin l
