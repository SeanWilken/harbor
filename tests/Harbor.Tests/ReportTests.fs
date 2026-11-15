module Harbor.Tests.ReportTests

open System
open Xunit
open Harbor
open Harbor.Domain

let private now = DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)

let private machine id name : Storage.MachineSummary =
    { Id = id; Name = name; NodeName = None; Login = None; Os = Some "linux"; Status = "approved"; Local = false
      FirstSeenAt = now; LastPushAt = Some(now.AddMinutes -1.); ScannedAt = None; PushSeconds = Some 300
      Repos = 0; Dirty = 0; Unsynced = 0; ServicesUp = 0; ServicesTotal = 0; Hidden = [||]
      CpuPercent = None; GpuPercent = None; MemPercent = None; IdleSeconds = None; Connected = true
      AgentCommit = None; Update = None; Role = None; Work = None; RoleSince = None }

let private repo path (url: string option) oid : Repo =
    { Path = path
      Name = IO.Path.GetFileName path
      Remotes = url |> Option.map (fun u -> [| { Name = "origin"; Url = u } |]) |> Option.defaultValue [||]
      Identity = { Name = Some "Dev"; Email = Some "dev@example.com"; Scope = None }
      Branch = Some { Head = Some "main"; Oid = Some oid; Upstream = Some "origin/main"; Ahead = 0; Behind = 0 }
      Changes = Some { Staged = 0; Unstaged = 0; Untracked = 0; Conflicted = 0; Paths = [||] }
      LastCommit = None; LastModified = Some(now.AddDays -10.); WorktreeOf = None; LastFetch = None
      FetchError = None; Error = None; Branches = None; BehindSince = None
      InFlight = Some { Unpushed = [||]; UnpushedCount = 0; Stashes = 0 } }

let private snap repos : Snapshot =
    { Machine = { Hostname = "h"; Os = "linux"; ScannedAt = now; ScanMs = 1L; Roots = [||] }
      Repos = repos; Services = [||]; Resources = None; Networks = None; Access = None; Role = None }

let private url = "https://x-token:secret@github.com/example/app.git"

let private report () =
    let devbox = machine 1L "devbox"
    let gamebox = { machine 2L "gamebox" with Hidden = [| "/g/hidden" |] }
    let dirty =
        { repo "/g/app" (Some url) "b2" with
            Changes = Some { Staged = 1; Unstaged = 2; Untracked = 0; Conflicted = 0; Paths = [| "secret.txt" |] }
            InFlight = Some { Unpushed = [| { Sha = "c"; Date = now.AddDays -5.; Subject = "wip"; Branch = "main" } |]; UnpushedCount = 1; Stashes = 0 } }
    Report.build now [| devbox; gamebox |]
        (Map [ 1L, snap [| repo "/d/app" (Some url) "a1"; repo "/d/notes" None "n1" |]
               2L, snap [| dirty; repo "/g/hidden" (Some "https://github.com/example/hidden.git") "h1" |] ])
        [||]

[<Fact>]
let ``clones of one remote group together, without the remote URL or hidden repos`` () =
    let r = report ()
    let app = r.Repos |> Array.find (fun x -> x.Name = "app")
    Assert.Equal("github.com/example/app", app.Key)
    Assert.Equal<string list>([ "devbox"; "gamebox" ], app.Clones |> Array.map _.Machine |> List.ofArray)
    Assert.Equal(Some false, app.SameHead)
    Assert.DoesNotContain(r.Repos, fun x -> x.Name = "hidden")
    let json = Text.Json.JsonSerializer.Serialize(r, Config.jsonOptions)
    Assert.DoesNotContain("secret", json)
    Assert.DoesNotContain("dev@example.com", json)

[<Fact>]
let ``attention ranks by severity then age, and a repo without a remote is the only copy`` () =
    let r = report ()
    Assert.Equal<(string * string) list>(
        [ "dirty", "warn"; "unpushed", "warn"; "no_remote", "info" ], // same severity: the longest-standing first
        r.Attention |> Array.map (fun a -> a.Kind, a.Severity) |> List.ofArray)
    Assert.Equal("notes", (r.Repos |> Array.find (fun x -> x.Key = "devbox:/d/notes")).Name)
    // repos needing attention come first
    Assert.True(r.Repos[0].Attention.Length > 0)

[<Fact>]
let ``a stale machine's clones say so, and a diverged branch is flagged`` () =
    let m = { machine 1L "devbox" with LastPushAt = Some(now.AddHours -3.) }
    let r =
        { repo "/d/app" (Some url) "a1" with
            Branch = Some { Head = Some "main"; Oid = Some "a1"; Upstream = Some "origin/main"; Ahead = 2; Behind = 3 } }
    let report = Report.build now [| m |] (Map [ 1L, snap [| r |] ]) [||]
    let kinds = report.Attention |> Array.map _.Kind |> Set.ofArray
    Assert.True(kinds.Contains "stale_report" && kinds.Contains "diverged")
    Assert.False report.Machines[0].Online

[<Fact>]
let ``filters keep one machine's view, or only what needs attention`` () =
    let r = report ()
    let devbox = Report.filter (Some "devbox") false r
    Assert.True(devbox.Repos |> Array.forall (fun x -> x.Clones |> Array.forall (fun c -> c.Machine = "devbox")))
    Assert.True(devbox.Attention |> Array.forall (fun a -> a.Machine = "devbox"))
    let needing = Report.filter None true r
    Assert.True(needing.Repos |> Array.forall (fun x -> x.Attention.Length > 0))
    let md = Report.markdown r
    Assert.Contains("## Needs attention (2 of 2 repos)", md)
    Assert.Contains("gamebox on main: 1 commit on no remote", md)
