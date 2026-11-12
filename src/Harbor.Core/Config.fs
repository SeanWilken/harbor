module Harbor.Config

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

/// How to tell whether a service is up. JSON: { "kind": "systemd", "unit": "..." } etc.
type Check =
    | Systemd of unit: string
    | SystemdUser of unit: string
    | Process of name: string
    | Port of port: int
    | Http of url: string
    | Podman of container: string

type ServiceConfig = { Name: string; Check: Check }

/// Where an agent pushes, and how often (it also pushes after every rescan).
type AgentConfig =
    { Hub: string
      PushSeconds: int
      /// Job kinds this machine will run when the hub asks. A hub can't request more than this.
      Allow: string[] }

/// Weekly recap thresholds (hub.recap).
type RecapConfig =
    { NeglectedDays: int
      DirtyDays: int
      BehindDays: int
      Weekday: DayOfWeek }

let defaultRecap = { NeglectedDays = 30; DirtyDays = 7; BehindDays = 7; Weekday = DayOfWeek.Monday }

type HubConfig =
    { Database: string
      /// Tailscale logins allowed to approve/reject machines from the tailnet (loopback always can).
      Admins: string[]
      /// Tailscale logins that may read the UI and API with redacted detail; "*" = any identified
      /// tailnet user. Everyone else (except admins and loopback) gets 403.
      Viewers: string[]
      /// Also listen on this node's Tailscale addresses (same port as `listen`).
      Tailnet: bool
      /// Loopback listener for a Tailscale Serve HTTPS front end, e.g. "http://127.0.0.1:7425".
      /// Requests there are identified by Serve's Tailscale-User-Login header. Off when unset.
      ServeListen: string option
      Recap: RecapConfig
      /// tailscaled's LocalAPI socket; found automatically when unset.
      TailscaleSocket: string option }

type Config =
    { Listen: string
      /// This agent's name on the hub; one Tailscale node may run several agents.
      Machine: string
      Roots: string[]
      Ignore: string[]
      /// Don't descend into dot-folders below a root.
      SkipHidden: bool
      MaxDepth: int
      RescanSeconds: int
      /// `git fetch` every repo with a remote this often, so ahead/behind is current. 0 = never.
      FetchMinutes: int
      /// Rebuild the installed-software manifest this often (it is sent only when it changed); 0 = never.
      ManifestHours: int
      Services: ServiceConfig[]
      Agent: AgentConfig option
      Hub: HubConfig option }

/// On-disk shape: every field optional, missing ones fall back to `defaults`.
type private AgentFile = { Hub: string; PushSeconds: int option; Allow: string[] option }

type private RecapFile =
    { NeglectedDays: int option
      DirtyDays: int option
      BehindDays: int option
      Weekday: string option }

let private recapOf (file: RecapFile option) : RecapConfig =
    let pick o d = Option.defaultValue d o
    match file with
    | None -> defaultRecap
    | Some r ->
        { NeglectedDays = pick r.NeglectedDays defaultRecap.NeglectedDays
          DirtyDays = pick r.DirtyDays defaultRecap.DirtyDays
          BehindDays = pick r.BehindDays defaultRecap.BehindDays
          Weekday =
            match r.Weekday with
            | None -> defaultRecap.Weekday
            | Some d ->
                match Enum.TryParse<DayOfWeek>(d, true) with
                | true, day -> day
                | _ -> failwith $"hub.recap.weekday: not a day of the week: {d}" }

type private HubFile =
    { Database: string
      Admins: string[] option
      Viewers: string[] option
      Tailnet: bool option
      ServeListen: string option
      Recap: RecapFile option
      TailscaleSocket: string option }

type private ConfigFile =
    { Listen: string option
      Machine: string option
      Roots: string[] option
      Ignore: string[] option
      SkipHidden: bool option
      MaxDepth: int option
      RescanSeconds: int option
      FetchMinutes: int option
      ManifestHours: int option
      Services: ServiceConfig[] option
      Agent: AgentFile option
      Hub: HubFile option }

/// Fetch only touches remote-tracking refs, never the working tree, so it's on by default.
let defaultAllow = [| "fetch" |]

let private home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

let defaults: Config =
    { Listen = "http://127.0.0.1:7420"
      Machine = Environment.MachineName.ToLowerInvariant()
      Roots = [| home |]
      Ignore = [| "node_modules"; ".cache"; ".local"; ".cargo"; ".nuget"; ".npm"; "snap"; ".venv"; "venv"; "target"; "bin"; "obj"; "dist" |]
      SkipHidden = true
      MaxDepth = 5
      RescanSeconds = 600
      FetchMinutes = 1440
      ManifestHours = 24
      Services = [||]
      Agent = None
      Hub = None }

let jsonOptions =
    JsonFSharpOptions
        .Default()
        .WithUnionInternalTag()
        .WithUnionNamedFields()
        .WithUnionTagName("kind")
        .WithUnionTagNamingPolicy(JsonNamingPolicy.CamelCase)
        .WithUnionUnwrapFieldlessTags()
        .WithSkippableOptionFields()
        .ToJsonSerializerOptions()
    |> fun o ->
        o.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        o.ReadCommentHandling <- JsonCommentHandling.Skip
        o.AllowTrailingCommas <- true
        o

let private expand (p: string) =
    if p = "~" then home
    elif p.StartsWith "~/" then Path.Combine(home, p[2..])
    else p

/// First existing of: explicit path, ./harbor.json, ~/.config/harbor/config.json.
let candidates (explicitPath: string option) =
    [ yield! Option.toList explicitPath
      "harbor.json"
      Path.Combine(home, ".config", "harbor", "config.json") ]

let load (explicitPath: string option) : Result<Config * string option, string> =
    match candidates explicitPath |> List.tryFind File.Exists with
    | None when explicitPath.IsSome -> Error $"config not found: {explicitPath.Value}"
    | None -> Ok(defaults, None)
    | Some path ->
        try
            let f =
                match JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText path, jsonOptions) with
                | null -> failwith "empty config"
                | f -> f
            let pick o d = Option.defaultValue d o
            let c: Config =
                { Listen = pick f.Listen defaults.Listen
                  Machine = pick f.Machine defaults.Machine
                  Roots = pick f.Roots defaults.Roots |> Array.map expand
                  Ignore = pick f.Ignore defaults.Ignore
                  SkipHidden = pick f.SkipHidden defaults.SkipHidden
                  MaxDepth = pick f.MaxDepth defaults.MaxDepth
                  RescanSeconds = pick f.RescanSeconds defaults.RescanSeconds
                  FetchMinutes = pick f.FetchMinutes defaults.FetchMinutes
                  ManifestHours = pick f.ManifestHours defaults.ManifestHours
                  Services = pick f.Services defaults.Services
                  Agent =
                    f.Agent
                    |> Option.map (fun a ->
                        { Hub = a.Hub.TrimEnd '/'
                          PushSeconds = pick a.PushSeconds 300
                          Allow = pick a.Allow defaultAllow })
                  Hub =
                    f.Hub
                    |> Option.map (fun h ->
                        { Database = h.Database
                          Admins = pick h.Admins [||]
                          Viewers = pick h.Viewers [||]
                          Tailnet = pick h.Tailnet true
                          ServeListen = h.ServeListen
                          Recap = recapOf h.Recap
                          TailscaleSocket = h.TailscaleSocket }) }
            Ok(c, Some path)
        with ex ->
            Error $"{path}: {ex.Message}"
