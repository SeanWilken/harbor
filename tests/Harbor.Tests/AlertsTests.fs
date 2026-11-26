module Harbor.Tests.AlertsTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open Xunit
open Harbor
open Harbor.Domain

let private now = DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)

let private summary name (lastPush: DateTimeOffset option) : Storage.MachineSummary =
    { Id = 7L; Name = name; NodeName = None; Login = None; Os = None; Status = "approved"; Local = false
      FirstSeenAt = now; LastPushAt = lastPush; ScannedAt = None; PushSeconds = Some 300
      Repos = 0; Dirty = 0; Unsynced = 0; ServicesUp = 0; ServicesTotal = 0; Hidden = [||]
      CpuPercent = None; GpuPercent = None; MemPercent = None; IdleSeconds = None; Connected = true
      AgentCommit = None; Update = None; Role = None; Work = None; RoleSince = None }

let private repo path (remotes: Remote[]) (unpushed: (DateTimeOffset * string) list) : Repo =
    { Path = path
      Name = Path.GetFileName path
      Remotes = remotes
      Identity = { Name = None; Email = None; Scope = None }
      Branch = None; Changes = None; LastCommit = None; LastModified = None; WorktreeOf = None
      LastFetch = None; FetchError = None; Error = None; Branches = None; BehindSince = None
      InFlight =
        Some { Unpushed = unpushed |> List.map (fun (d, s) -> { Sha = "abc1234"; Date = d; Subject = s; Branch = "main" }) |> Array.ofList
               UnpushedCount = unpushed.Length
               Stashes = 0 } }

let private snap repos role : Snapshot =
    { Machine = { Hostname = "h"; Os = "linux"; ScannedAt = now; ScanMs = 1L; Roots = [||] }
      Repos = repos; Services = [||]; Resources = None; Networks = None; Access = None; Role = role; Releases = None }

let private origin = [| { Name = "origin"; Url = "https://github.com/example/app.git" } |]

[<Fact>]
let ``a machine is offline once its report is three intervals old`` () =
    let found =
        Alerts.offline now
            [| summary "devbox" (Some(now.AddMinutes -5.))
               summary "pi" (Some(now.AddMinutes -20.))
               summary "new" None // never reported: pending enrolment, not an outage
               { summary "gone" (Some(now.AddDays -3.)) with Status = "rejected" } |]
    Assert.Equal<string list>([ "pi" ], found |> Array.map _.Subject |> List.ofArray)
    Assert.Equal("critical", found[0].Severity)

[<Fact>]
let ``commits only in one clone alert after the threshold, unless hidden or there is no remote`` () =
    let old = now.AddDays -5.
    let m = { summary "devbox" (Some now) with Hidden = [| "/src/hidden" |] }
    let s =
        snap
            [| repo "/src/app" origin [ now.AddDays -1., "newer"; old, "oldest" ]
               repo "/src/fresh" origin [ now.AddHours -2., "today" ]
               repo "/src/hidden" origin [ old, "old" ]
               repo "/src/scratch" [||] [ old, "no remote at all" ] |]
            None
    let found = Alerts.unpushed now 3. m s
    Assert.Equal<string list>([ "devbox:/src/app" ], found |> Array.map _.Subject |> List.ofArray)
    Assert.Equal("app on devbox: 2 commits not pushed", found[0].Title)
    Assert.Contains("oldest", found[0].Detail.Value)

[<Fact>]
let ``a failed role action alerts until the next switch`` () =
    let role ok : RoleState =
        { Name = "gaming"; Work = "none"; Since = now; By = "hub"; Declared = [||]
          Applied = [| "stop ollama: ok"; (if ok then "start sunshine.exe: started" else "start sunshine.exe: failed (not found)") |] }
    let m = summary "gamebox" (Some now)
    Assert.Empty(Alerts.roleFailed m (snap [||] (Some(role true))))
    let found = Alerts.roleFailed m (snap [||] (Some(role false)))
    Assert.Equal("start sunshine.exe: failed (not found)", found[0].Detail.Value)

let private site url : Config.SiteConfig = { Name = "shop"; Url = url; Provider = None; Health = None; EveryMinutes = 5 }

let private check ok (cert: DateTimeOffset option) : Sites.Check =
    { Site = "shop"; At = now; Ok = ok; Status = None; Ms = None; Error = (if ok then None else Some "timed out")
      CertExpires = cert; CertIssuer = None; Version = None }

[<Fact>]
let ``a site is down after two failed checks in a row, and its certificate warns inside two weeks`` () =
    let s = site "https://shop.example.com"
    Assert.Empty(Alerts.sites now s [| check true None; check false None |]) // one failure is a blip
    let down = Alerts.sites now s [| check true None; check false None; check false None |]
    Assert.Equal<string list>([ "site_down" ], down |> Array.map _.Rule |> List.ofArray)
    Assert.Empty(Alerts.sites now s [| check true (Some(now.AddDays 30.)) |])
    let cert = Alerts.sites now s [| check true (Some(now.AddDays 10.)) |]
    Assert.Equal("shop: certificate expires in 10 days", cert[0].Title)
    Assert.Equal("warn", cert[0].Severity)
    let soon = Alerts.sites now s [| check true (Some(now.AddDays 2.)) |]
    Assert.Equal("critical", soon[0].Severity)

let private alert rule subject machineId : Storage.Alert =
    { Id = 1L; Rule = rule; Subject = subject; MachineId = machineId; Severity = "warn"; Title = "t"; Detail = None
      OpenedAt = now; SeenAt = now; ResolvedAt = None; AckedAt = None; AckedBy = None }

[<Fact>]
let ``alert actions post only for admins, and a failed role offers a retry and the default role`` () =
    let role : RoleState =
        { Name = "gaming"; Work = "none"; Since = now; By = "hub"; Applied = [||]
          Declared =
            [| { Name = "general"; Work = "light"; Title = None; Description = None; Power = None; Start = None; Stop = None }
               { Name = "gaming"; Work = "none"; Title = None; Description = None; Power = None; Start = None; Stop = None } |] }
    let a = alert "role_failed" "gamebox" (Some 7L)
    let labels admin = Alerts.actions admin [||] (Some(snap [||] (Some role))) a |> Array.map _.Label |> List.ofArray
    Assert.Equal<string list>([ "Open machine"; "Retry gaming"; "Switch to general" ], labels true)
    Assert.Equal<string list>([ "Open machine" ], labels false)
    let retry = Alerts.actions true [||] (Some(snap [||] (Some role))) a |> Array.find (fun x -> x.Label = "Retry gaming")
    Assert.Equal(Some "/api/machines/7/jobs", retry.Post)
    Assert.Equal(Some { Alerts.Kind = "role"; Alerts.Arg = "gaming" }, retry.Body)
    Assert.True(retry.Confirm.IsSome)
    // a DNS check has nothing to open in a browser
    let dns = Alerts.actions true [| { site "dns://100.101.1.5/example.com" with Name = "pi-dns" } |] None (alert "site_down" "pi-dns" None)
    Assert.Equal<string list>([ "Check now" ], dns |> Array.map _.Label |> List.ofArray)

[<Fact>]
let ``a dns query is well formed and only its own reply is accepted`` () =
    let q = Sites.dnsQuery 0xBEEFus "example.com"
    Assert.Equal<byte[]>([| 0xBEuy; 0xEFuy; 1uy; 0uy; 0uy; 1uy |], q[0..5])
    Assert.Equal<byte[]>([| 7uy; byte 'e' |], q[12..13])
    let reply rcode = Array.concat [ [| 0xBEuy; 0xEFuy; 0x81uy; byte (0x80 ||| rcode) |]; Array.zeroCreate 8 ]
    Assert.Equal(Some 0, Sites.dnsRcode 0xBEEFus (reply 0))
    Assert.Equal(Some 3, Sites.dnsRcode 0xBEEFus (reply 3))
    Assert.Equal(None, Sites.dnsRcode 0x1234us (reply 0))
    Assert.Equal(None, Sites.dnsRcode 0xBEEFus q) // a query, not a reply

[<Fact>]
let ``a dns check is up when the resolver answers and down when nothing does`` () =
    task {
        use server = new UdpClient(IPEndPoint(IPAddress.Loopback, 0))
        let port = (server.Client.LocalEndPoint :?> IPEndPoint).Port
        let answer =
            task {
                let! r = server.ReceiveAsync()
                let reply = Array.copy r.Buffer
                reply[2] <- reply[2] ||| 0x80uy
                let! _ = server.SendAsync(reply, reply.Length, r.RemoteEndPoint)
                ()
            }
        let! up = Sites.check (site $"dns://127.0.0.1:{port}/example.com")
        do! answer
        Assert.True(up.Ok, $"%A{up.Error}")
        Assert.Equal(Some 0, up.Status)
        server.Close()
        let! down = Sites.check (site $"dns://127.0.0.1:{port}/example.com")
        Assert.False down.Ok
    }

[<Fact>]
let ``dns sites load from config, but not with a health endpoint`` () =
    let load (sites: string) =
        let path = Path.Combine(Path.GetTempPath(), $"harbor-alerts-{Guid.NewGuid():N}.json")
        File.WriteAllText(path, $$"""{ "hub": { "database": "Host=x", "sites": {{sites}} } }""")
        try Config.load (Some path) |> Result.map (fun (c, _) -> c.Hub.Value.Sites) finally File.Delete path
    match load """[ { "name": "pi-dns", "url": "dns://100.101.1.5/example.com", "everyMinutes": 1 } ]""" with
    | Ok [| s |] -> Assert.Equal(1, s.EveryMinutes)
    | other -> failwithf "%A" other
    Assert.True(Result.isError (load """[ { "name": "pi-dns", "url": "dns://100.101.1.5", "health": "/x" } ]"""))
