module Harbor.Tests.SecurityTests

open System
open Xunit
open Harbor
open Harbor.Domain
open Harbor.Enrol
open Harbor.Tailscale

let private peer login = Peer { NodeId = "n1"; NodeName = "ipad.tail.ts.net"; Login = login; Os = None }

[<Fact>]
let ``roles: loopback and listed admins are admins, listed viewers view, everyone else nothing`` () =
    let admins = [| "me@example.com" |]
    Assert.Equal(Some Admin, role admins [||] Local)
    Assert.Equal(Some Admin, role admins [||] (peer "ME@example.com"))
    Assert.Equal(Some Viewer, role admins [| "friend@example.com" |] (peer "friend@example.com"))
    Assert.Equal(None, role admins [| "friend@example.com" |] (peer "stranger@example.com"))
    Assert.Equal(Some Viewer, role admins [| "*" |] (peer "stranger@example.com"))
    Assert.Equal(None, role [||] [||] (peer "me@example.com")) // fails closed with nothing configured

[<Theory>]
[<InlineData("https://ghp_secret@github.com/me/x.git", "https://github.com/me/x.git")>]
[<InlineData("https://me:token@gitea.local:3000/me/x", "https://gitea.local:3000/me/x")>]
[<InlineData("ssh://git:pw@github.com:22/me/x", "ssh://git@github.com:22/me/x")>]
[<InlineData("git@github.com:me/x.git", "git@github.com:me/x.git")>]
[<InlineData("https://github.com/me/x", "https://github.com/me/x")>]
[<InlineData("/srv/git/x.git", "/srv/git/x.git")>]
let ``credentials are stripped from remote urls`` (url: string, expected: string) =
    Assert.Equal(expected, GitParse.scrubUrl url)

[<Fact>]
let ``viewers do not see processes, hardware addresses, emails, file names or raw errors`` () =
    let repo =
        { Path = "/srv/a"; Name = "a"; Remotes = [||]
          Identity = { Name = Some "Me"; Email = Some "me@example.com"; Scope = Some "global" }
          Branch = None
          Changes = Some { Staged = 1; Unstaged = 0; Untracked = 0; Conflicted = 0; Paths = [| "secrets.env" |] }
          LastCommit = None; LastModified = None; WorktreeOf = None; LastFetch = None
          FetchError = Some "fatal: could not read Username for 'https://x'"; Error = None; Branches = None; BehindSince = None }
    let snap =
        { Machine = { Hostname = "h"; Os = "linux"; ScannedAt = DateTimeOffset.UnixEpoch; ScanMs = 1L; Roots = [||] }
          Repos = [| repo |]
          Services = [| { Name = "pg"; Check = "port 5432"; State = Running; Detail = "connected to 127.0.0.1" } |]
          Resources = Some { Cores = 4; CpuPercent = Some 5.; MemTotalBytes = None; MemAvailableBytes = None; Load1 = None
                             UptimeSeconds = 1L; IdleSeconds = Some 3L; Gpus = [||]; Disks = [||]
                             Top = [| { Name = "keepassxc"; Count = 1; CpuPercent = 1.; RssBytes = 1L } |] }
          Networks = Some [| { Name = "eth0"; Kind = "ethernet"; Mac = Some "aa:bb"; Gateway = Some "192.168.1.1"; GatewayMac = Some "cc:dd"
                               Addresses = [| { Address = "192.168.1.5"; PrefixLength = 24 } |] }
                             { Name = "tailscale0"; Kind = "tailnet"; Mac = None; Gateway = None; GatewayMac = None
                               Addresses = [| { Address = "100.64.0.5"; PrefixLength = 32 } |] } |] }
    let r = Redact.snapshot snap
    Assert.Empty(r.Resources.Value.Top)
    Assert.Equal(Some 5., r.Resources.Value.CpuPercent) // figures stay
    Assert.Equal(None, r.Repos[0].Identity.Email)
    Assert.Empty(r.Repos[0].Changes.Value.Paths)
    Assert.Equal(1, r.Repos[0].Changes.Value.Staged) // counts stay
    Assert.Equal(Some "fetch failed", r.Repos[0].FetchError)
    Assert.Equal("", r.Services[0].Detail)
    let nets = r.Networks.Value
    Assert.True(nets |> Array.forall (fun n -> n.Mac.IsNone && n.GatewayMac.IsNone && n.Gateway.IsNone))
    Assert.Empty(nets[0].Addresses)
    Assert.Equal("100.64.0.5", nets[1].Addresses[0].Address)

[<Fact>]
let ``Serve callers: identified by login, refused without one, never allowed to push`` () =
    Assert.Equal(Allowed(Served "me@example.com"), served " me@example.com ")
    Assert.True(match served "" with Denied _ -> true | _ -> false)
    Assert.Equal(Some Admin, role [| "me@example.com" |] [||] (Served "me@example.com"))
    Assert.Equal(None, role [| "me@example.com" |] [||] (Served "other@example.com"))
    Assert.True(match push (Served "me@example.com") None with Refuse _ -> true | _ -> false)

[<Fact>]
let ``agent link messages round-trip and reject junk`` () =
    let hello = { AgentLink.message "hello" with Machine = Some "devbox" }
    Assert.Equal(Some hello, AgentLink.decode (AgentLink.encode hello))
    Assert.Equal(None, AgentLink.decode "not json")
    Assert.Equal(None, AgentLink.decode """{"v":2,"type":"hello"}""")
    Assert.Equal("wake", (AgentLink.decode """{"v":1,"type":"wake","reason":"job","extra":1}""").Value.Type)

[<Theory>]
[<InlineData("POST", "https://evil.example", "cross-site", "devbox:7420", false, true)>]   // a page elsewhere posting
[<InlineData("POST", "", "cross-site", "devbox:7420", false, true)>]                       // same, no Origin
[<InlineData("POST", "http://devbox:7420", "same-origin", "devbox:7420", false, false)>]     // harbor's own UI
[<InlineData("POST", "", "", "100.101.1.10:7420", false, false)>]                         // an agent push
[<InlineData("GET", "https://evil.example", "cross-site", "devbox:7420", false, false)>]   // reads are fine (no CORS)
[<InlineData("GET", "https://evil.example", "cross-site", "devbox:7420", true, true)>]     // cross-site websocket
[<InlineData("GET", "", "", "100.101.1.10:7420", true, false)>]                           // the agent link
let ``cross-site writes and websockets are refused, same-site and agents are not``
    (methodName: string, origin: string, fetchSite: string, host: string, ws: bool, refused: bool) =
    Assert.Equal(refused, Hub.crossSite methodName origin fetchSite host ws)
