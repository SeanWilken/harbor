module Harbor.Tests.RolesTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Harbor
open Harbor.Domain

let private loadText (json: string) =
    let path = Path.Combine(Path.GetTempPath(), $"harbor-roles-{Guid.NewGuid():N}.json")
    File.WriteAllText(path, json)
    try Config.load (Some path) |> Result.map fst finally File.Delete path

let private role name work : Config.RoleConfig =
    { Name = name; Title = None; Description = None; Work = work; Power = None; Start = [||]; Stop = [||] }

let private info name work : RoleInfo =
    { Name = name; Work = work; Title = None; Description = None; Power = None; Start = None; Stop = None }

[<Fact>]
let ``roles load from config, first one is the default`` () =
    match loadText """{ "roles": [ { "name": "general", "work": "light" },
                                     { "name": "gaming", "title": "Game streaming", "work": "none", "power": "performance",
                                       "start": [ { "command": "steam://open/bigpicture" }, { "unit": "sunshine" } ],
                                       "stop": [ { "process": "ollama" } ] } ] }""" with
    | Ok c ->
        Assert.Equal<string[]>([| "general"; "gaming" |], c.Roles |> Array.map _.Name)
        Assert.Equal(Some "performance", c.Roles[1].Power)
        Assert.Equal(2, c.Roles[1].Start.Length)
        Assert.Equal(Some "Game streaming", c.Roles[1].Title)
    | Error e -> failwith e

[<Theory>]
[<InlineData("""{ "roles": [ { "name": "Gaming", "work": "none" } ] }""")>]
[<InlineData("""{ "roles": [ { "name": "gaming", "work": "most" } ] }""")>]
[<InlineData("""{ "roles": [ { "name": "gaming", "work": "none", "power": "turbo" } ] }""")>]
[<InlineData("""{ "roles": [ { "name": "a", "work": "none" }, { "name": "a", "work": "full" } ] }""")>]
[<InlineData("""{ "roles": [ { "name": "a", "work": "none", "stop": [ { "command": "rm" } ] } ] }""")>]
[<InlineData("""{ "roles": [ { "name": "a", "work": "none", "start": [ { "process": "x" } ] } ] }""")>]
[<InlineData("""{ "roles": [ { "name": "a", "work": "none", "start": [ { "command": "x", "unit": "y" } ] } ] }""")>]
let ``malformed roles fail the config load`` (json: string) =
    Assert.True(Result.isError (loadText json))

[<Fact>]
let ``the saved role survives while declared, else the default`` () =
    let now = DateTimeOffset.Now
    let roles = [| role "general" "light"; role "gaming" "none" |]
    let saved name : Roles.Saved = { Name = name; Since = now.AddHours -1.; By = "tray"; Applied = [||] }
    Assert.Equal(Some "gaming", Roles.current roles (Some(saved "gaming")) now |> Option.map _.Name)
    let fallback = (Roles.current roles (Some(saved "media")) now).Value
    Assert.Equal(("general", "default"), (fallback.Name, fallback.By))
    Assert.Equal(None, Roles.current [||] (Some(saved "gaming")) now)

[<Fact>]
let ``power profiles map to powercfg aliases and powerprofilesctl`` () =
    Assert.Equal<string list>([ "powercfg"; "/setactive"; "SCHEME_MIN" ], Roles.powerCommand true "performance")
    Assert.Equal<string list>([ "powercfg"; "/setactive"; "SCHEME_MAX" ], Roles.powerCommand true "power-saver")
    Assert.Equal<string list>([ "powerprofilesctl"; "set"; "balanced" ], Roles.powerCommand false "balanced")

[<Fact>]
let ``switching saves the role, reports failed actions and goes quiet for work none`` () =
    let machine = $"test-roles-{Guid.NewGuid():N}"
    let roles = [| role "general" "light"; role "gaming" "none" |]
    let act (r: Config.RoleConfig) = Task.FromResult(if r.Name = "gaming" then [ "start sunshine: started", true; "power performance: failed (x)", false ] else [])
    try
        let c = Roles.Controller(machine, roles, act)
        Assert.False c.Quiet
        let ok, out = c.Switch("media", "hub").Result
        Assert.False ok
        Assert.Contains("isn't a role here", out)
        let ok, out = c.Switch("gaming", "tray").Result
        Assert.False ok // the switch happened; one action didn't
        Assert.Contains("power performance: failed", out)
        Assert.True c.Quiet
        Assert.Equal(Some("gaming", "tray"), c.State |> Option.map (fun s -> s.Name, s.By))
        // a restarted agent comes back in the saved role without re-running anything
        let again = Roles.Controller(machine, roles, (fun _ -> failwith "must not run"))
        Assert.Equal(Some "gaming", again.State |> Option.map _.Name)
    finally
        File.Delete(Roles.statePath machine)

[<Fact>]
let ``a role job runs without an allow entry; other kinds still need one`` () =
    let machine = $"test-roles-{Guid.NewGuid():N}"
    let store = Inventory.Store { Config.defaults with Roots = [||] }
    try
        let c = Roles.Controller(machine, [| role "general" "light"; role "worker" "full" |], (fun _ -> Task.FromResult []))
        let r = (Agent.runJob store [||] c { Id = 1L; Kind = "role"; Path = None; Arg = Some "worker" }).Result
        Assert.True(r.Ok, r.Output)
        Assert.Equal(Some "worker", c.State |> Option.map _.Name)
        let f = (Agent.runJob store [||] c { Id = 2L; Kind = "fetch"; Path = None; Arg = None }).Result
        Assert.False f.Ok
    finally
        File.Delete(Roles.statePath machine)

[<Fact>]
let ``the hub only queues declared roles`` () =
    let declared = [| info "general" "light"; info "gaming" "none" |]
    let job arg path : Hub.JobBody = { Kind = "role"; Path = path; Arg = arg }
    Assert.Equal(Ok(), Hub.checkJob declared (job (Some "gaming") None))
    Assert.True(Result.isError (Hub.checkJob declared (job (Some "media") None)))
    Assert.True(Result.isError (Hub.checkJob [||] (job (Some "gaming") None)))
    Assert.True(Result.isError (Hub.checkJob declared (job None None)))
    Assert.True(Result.isError (Hub.checkJob declared (job (Some "gaming") (Some "/srv"))))

let private state name work by : RoleState =
    { Name = name; Work = work; Since = DateTimeOffset.Now; By = by
      Declared = [| info "general" "light"; { info "gaming" "none" with Start = Some [| "sunshine.exe" |] } |]
      Applied = [| "start C:\\Games\\sunshine.exe: started" |] }

let private snap role : Snapshot =
    { Machine = { Hostname = "h"; Os = "windows"; ScannedAt = DateTimeOffset.Now; ScanMs = 1L; Roots = [||] }
      Repos = [||]; Services = [||]; Resources = None; Networks = None; Access = None; Role = role }

[<Fact>]
let ``a role change is an event, the first default isn't`` () =
    Assert.Empty(Events.diff (Some(snap None)) (snap (Some(state "general" "light" "default"))))
    let ds = Events.diff (Some(snap (Some(state "general" "light" "default")))) (snap (Some(state "gaming" "none" "hub")))
    Assert.Equal<(string * string option) list>([ "role_changed", Some "gaming" ], ds |> List.map (fun d -> d.Kind, d.Subject))

[<Fact>]
let ``viewers see the role but not who switched it or what ran`` () =
    let r = (Redact.snapshot (snap (Some(state "gaming" "none" "admin@example.com")))).Role.Value
    Assert.Equal(("gaming", "hub"), (r.Name, r.By))
    Assert.Empty r.Applied
    Assert.True(r.Declared |> Array.forall (fun d -> d.Start.IsNone && d.Stop.IsNone))

let private summary name work (lastPush: DateTimeOffset option) connected : Storage.MachineSummary =
    { Id = 1L; Name = name; NodeName = None; Login = None; Os = None; Status = "approved"; Local = false
      FirstSeenAt = DateTimeOffset.Now; LastPushAt = lastPush; ScannedAt = None; PushSeconds = Some 60
      Repos = 0; Dirty = 0; Unsynced = 0; ServicesUp = 0; ServicesTotal = 0; Hidden = [||]
      CpuPercent = None; GpuPercent = None; MemPercent = None; IdleSeconds = None; Connected = connected
      AgentCommit = None; Update = None; Role = work |> Option.map (fun _ -> "r"); Work = work; RoleSince = None }

[<Fact>]
let ``availability is the role's work while reachable, none when not`` () =
    let now = DateTimeOffset.Now
    let rows =
        Storage.availability now
            [| summary "devbox" (Some "full") (Some now) true
               summary "gamebox" (Some "full") (Some(now.AddMinutes -10.)) false
               summary "pi" None (Some now) true
               { summary "new" (Some "full") (Some now) true with Status = "pending" } |]
    Assert.Equal<(string * string) list>([ "devbox", "full"; "gamebox", "none"; "pi", "unknown" ], rows |> Array.map (fun r -> r.Machine, r.Available) |> List.ofArray)

[<Fact>]
let ``role cards name actions without full paths`` () =
    let act c u p : Config.RoleAction = { Command = c; Args = None; Unit = u; Process = p }
    Assert.Equal("sunshine.exe", Roles.actionLabel (act (Some "C:\\Program Files\\Sunshine\\sunshine.exe") None None))
    Assert.Equal("steam://open/bigpicture", Roles.actionLabel (act (Some "steam://open/bigpicture") None None))
    Assert.Equal("unit jellyfin", Roles.actionLabel (act None (Some "jellyfin") None))
    Assert.Equal("ollama", Roles.actionLabel (act None None (Some "ollama")))
