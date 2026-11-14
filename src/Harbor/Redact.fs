/// What a viewer (not an admin) may see. Viewers get the fleet picture: machines, repos,
/// branches, commits, figures. Not: who is logged in where, hardware addresses, LAN layout,
/// process lists, changed file names, who switched a role and what it ran, raw error/command output or git identities.
module Harbor.Redact

open Harbor.Domain

let private repo (r: Repo) =
    { r with
        Identity = { Name = None; Email = None; Scope = None }
        Changes = r.Changes |> Option.map (fun c -> { c with Paths = [||] })
        FetchError = r.FetchError |> Option.map (fun _ -> "fetch failed")
        Error = r.Error |> Option.map (fun _ -> "scan failed") }

/// Interfaces keep their kind and only tailnet addresses (viewers are on the tailnet already).
let private network (n: NetInterface) =
    { n with
        Mac = None
        Gateway = None
        GatewayMac = None
        Addresses = if n.Kind = "tailnet" then n.Addresses else [||] }

let snapshot (s: Snapshot) : Snapshot =
    { s with
        Repos = s.Repos |> Array.map repo
        Services = s.Services |> Array.map (fun x -> { x with Detail = "" })
        Resources = s.Resources |> Option.map (fun r -> { r with Top = [||] })
        Networks = s.Networks |> Option.map (Array.map network)
        // the role and its work level are the point; who switched it and what ran are admin detail
        Role = s.Role |> Option.map (fun r -> { r with By = (if r.By = "default" || r.By = "tray" then r.By else "hub"); Applied = [||] }) }
