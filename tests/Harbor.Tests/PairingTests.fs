module Harbor.Tests.PairingTests

open System
open Xunit
open Harbor

[<Fact>]
let ``codes are well formed and survive the ways people type them`` () =
    for _ in 1..200 do
        let c = Pairing.generate ()
        Assert.True(Pairing.isWellFormed c, c)
        Assert.Equal(Pairing.hash c, Pairing.hash ((Pairing.display c).ToLowerInvariant()))
        Assert.Equal(Pairing.hash c, Pairing.hash $"{c.Substring(0, 4)} {c.Substring 4}")
    Assert.False(Pairing.isWellFormed "7KQ4M2X0") // 0 is not in the alphabet
    Assert.False(Pairing.isWellFormed "7KQ4M2X")

[<Fact>]
let ``wrong codes from one caller are cut off after the limit, then allowed again`` () =
    let l = Pairing.Limiter(3, TimeSpan.FromMinutes 10.)
    let t0 = DateTimeOffset.UnixEpoch
    for i in 0..2 do l.Fail("node", t0.AddSeconds(float i))
    Assert.True(l.Blocked("node", t0.AddMinutes 1.))
    Assert.False(l.Blocked("other", t0.AddMinutes 1.))
    Assert.False(l.Blocked("node", t0.AddMinutes 11.))

let private status hub linked lastUpdate : AgentStatus.Status =
    { Machine = "pi"; Hub = "http://devbox:7420"; HubStatus = hub; Linked = linked; LastPushAt = None
      LastError = None; Pairing = false; PushSeconds = 60; Commit = None; Update = None; Role = None; Work = None; Roles = None; UpdatedAt = lastUpdate }

[<Fact>]
let ``tethered means approved, linked and the agent still writing`` () =
    let now = DateTimeOffset.UnixEpoch.AddDays 1.
    Assert.Equal(AgentStatus.Tethered, AgentStatus.tether now (status "approved" true now))
    Assert.True(match AgentStatus.tether now (status "approved" false now) with AgentStatus.Waiting _ -> true | _ -> false)
    Assert.True(match AgentStatus.tether now (status "pending" true now) with AgentStatus.Waiting _ -> true | _ -> false)
    Assert.True(match AgentStatus.tether now (status "rejected" true now) with AgentStatus.Untethered _ -> true | _ -> false)
    Assert.True(match AgentStatus.tether now (status "approved" true (now.AddMinutes -10.)) with AgentStatus.Untethered _ -> true | _ -> false)
    Assert.Equal("7KQ4M2XP", AgentStatus.normalizeCode "7kq4-m2xp ")
