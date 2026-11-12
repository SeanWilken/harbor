module Harbor.Tests.EnrolTests

open System.Net
open Xunit
open Harbor
open Harbor.Enrol
open Harbor.Tailscale

let private laptop =
    { NodeId = "nEXAMPLE001CNTRL"; NodeName = "laptop.example.ts.net"; Login = "me@example.com"; Os = Some "windows" }

let private ip (s: string) = IPAddress.Parse s

[<Fact>]
let ``whois body gives stable id, trimmed dns name, login and os`` () =
    let body =
        """{ "Node": { "ID": 1, "StableID": "nEXAMPLE001CNTRL", "Name": "laptop.example.ts.net.",
                      "Hostinfo": { "Hostname": "laptop", "OS": "windows" } },
             "UserProfile": { "LoginName": "me@example.com", "DisplayName": "Me" } }"""
    Assert.Equal(Some laptop, parseWhoIs body)

[<Fact>]
let ``whois body without a user profile is not a peer`` () =
    Assert.Equal(None, parseWhoIs """{ "Node": { "StableID": "x", "Name": "y." } }""")

[<Fact>]
let ``status body lists our own tailscale addresses`` () =
    let body = """{ "Self": { "TailscaleIPs": ["100.101.1.10", "fd7a:115c:a1e0::1:10"] } }"""
    Assert.Equal<IPAddress[]>([| ip "100.101.1.10"; ip "fd7a:115c:a1e0::1:10" |], parseSelfAddresses body)

[<Theory>]
[<InlineData("100.101.1.20", true)>]
[<InlineData("::ffff:100.101.1.20", true)>]
[<InlineData("fd7a:115c:a1e0::1:10", true)>]
[<InlineData("100.128.0.1", false)>]
[<InlineData("192.168.50.71", false)>]
let ``tailnet addresses are the CGNAT and tailscale ULA ranges`` (addr: string, expected: bool) =
    Assert.Equal(expected, isTailnetAddress (ip addr))

[<Fact>]
let ``loopback is local whatever whois says`` () =
    Assert.Equal(Allowed Local, access IPAddress.Loopback None)
    Assert.Equal(Allowed Local, access IPAddress.IPv6Loopback None)

[<Fact>]
let ``LAN callers are denied even if whois would answer`` () =
    Assert.True(access (ip "192.168.50.72") (Some laptop) |> function Denied _ -> true | _ -> false)

[<Fact>]
let ``tailnet callers need a whois answer`` () =
    Assert.Equal(Allowed(Peer laptop), access (ip "100.101.1.20") (Some laptop))
    Assert.True(access (ip "100.101.1.20") None |> function Denied _ -> true | _ -> false)

[<Fact>]
let ``push outcome follows enrolment state`` () =
    let peer = Peer laptop
    Assert.Equal(Enrol, push peer None)
    Assert.Equal(Hold, push peer (Some Pending))
    Assert.Equal(Store, push peer (Some Approved))
    Assert.True(push peer (Some Rejected) |> function Refuse _ -> true | _ -> false)

[<Fact>]
let ``loopback pushes are refused because the sender is unidentifiable`` () =
    Assert.True(push Local (Some Approved) |> function Refuse _ -> true | _ -> false)

[<Fact>]
let ``admins are loopback or listed logins, case-insensitively`` () =
    Assert.True(canAdmin [||] Local)
    Assert.True(canAdmin [| "ME@example.com" |] (Peer laptop))
    Assert.False(canAdmin [| "someone@else.org" |] (Peer laptop))
