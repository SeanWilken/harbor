module Harbor.Tests.EventsTests

open System
open Xunit
open Harbor
open Harbor.Domain

let private at = DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)

let private repo path =
    { Path = path
      Name = IO.Path.GetFileName path
      Remotes = [||]
      Identity = { Name = None; Email = None; Scope = None }
      Branch = Some { Head = Some "main"; Oid = Some "a"; Upstream = Some "origin/main"; Ahead = 0; Behind = 0 }
      Changes = Some { Staged = 0; Unstaged = 0; Untracked = 0; Conflicted = 0; Paths = [||] }
      LastCommit = None
      LastModified = None
      WorktreeOf = None
      LastFetch = None
      FetchError = None
      Error = None
      Branches = None; BehindSince = None; InFlight = None }

let private snap roots repos services =
    { Machine = { Hostname = "h"; Os = "linux"; ScannedAt = at; ScanMs = 1L; Roots = roots }
      Repos = repos
      Services = services
      Resources = None
      Networks = None
      Access = None }

let private svc name state = { Name = name; Check = "port 1"; State = state; Detail = "" }

let private kinds (ds: Events.Draft list) = ds |> List.map (fun d -> d.Kind, d.RepoPath, d.Subject)

[<Fact>]
let ``first snapshot is a baseline with no events`` () =
    Assert.Empty(Events.diff None (snap [| "/srv" |] [| repo "/srv/a" |] [| svc "pg" Running |]))

[<Fact>]
let ``dirty, ahead and branch changes each produce an event`` () =
    let a = repo "/srv/a"
    let a' =
        { a with
            Branch = Some { a.Branch.Value with Head = Some "feat"; Ahead = 2 }
            Changes = Some { a.Changes.Value with Unstaged = 1; Paths = [| "x.fs" |] } }
    let ds = Events.diff (Some(snap [| "/srv" |] [| a |] [||])) (snap [| "/srv" |] [| a' |] [||])
    Assert.Equal<_ list>(
        [ "dirty", Some "/srv/a", None; "ahead_behind", Some "/srv/a", None; "branch_changed", Some "/srv/a", Some "feat" ],
        kinds ds)

[<Fact>]
let ``becoming clean is an event, staying dirty is not`` () =
    let d = { repo "/srv/a" with Changes = Some { Staged = 1; Unstaged = 0; Untracked = 0; Conflicted = 0; Paths = [||] } }
    let s = snap [| "/srv" |]
    Assert.Equal<_ list>([ "clean", Some "/srv/a", None ], kinds (Events.diff (Some(s [| d |] [||])) (s [| repo "/srv/a" |] [||])))
    Assert.Empty(Events.diff (Some(s [| d |] [||])) (s [| d |] [||]))

[<Fact>]
let ``repos are added and removed, except under a root that has gone missing`` () =
    let prev = snap [| "/srv"; "/mnt/usb" |] [| repo "/srv/old"; repo "/mnt/usb/x" |] [||]
    let next = snap [| "/srv" |] [| repo "/srv/new" |] [||]
    Assert.Equal<_ list>(
        [ "repo_added", Some "/srv/new", None; "repo_removed", Some "/srv/old", None ],
        kinds (Events.diff (Some prev) next))

[<Fact>]
let ``services going up or down are events; unknown is not a transition`` () =
    let prev = snap [||] [||] [| svc "pg" Running; svc "web" Stopped; svc "x" Running |]
    let next = snap [||] [||] [| svc "pg" Stopped; svc "web" Running; svc "x" Unknown |]
    Assert.Equal<_ list>([ "service_down", None, Some "pg"; "service_up", None, Some "web" ], kinds (Events.diff (Some prev) next))

[<Theory>]
[<InlineData("git@github.com:SeanWilken/harbor.git", "github.com/seanwilken/harbor")>]
[<InlineData("https://github.com/SeanWilken/harbor", "github.com/seanwilken/harbor")>]
[<InlineData("https://sean@github.com/seanwilken/harbor.git/", "github.com/seanwilken/harbor")>]
[<InlineData("ssh://git@github.com:22/seanwilken/harbor.git", "github.com/seanwilken/harbor")>]
[<InlineData("http://devbox:3000/me/notes.git", "devbox/me/notes")>]
let ``remote urls of one repo share a key`` (url: string, key: string) =
    Assert.Equal(Some key, GitParse.remoteKey url)

[<Theory>]
[<InlineData("/srv/git/x.git")>]
[<InlineData("../x")>]
[<InlineData("C:\\repos\\x")>]
[<InlineData("file:///srv/git/x.git")>]
let ``local path remotes have no key`` (url: string) =
    Assert.Equal(None, GitParse.remoteKey url)

[<Fact>]
let ``log parses NUL-separated commits with unit-separated fields`` () =
    let out = "abc\0312026-11-10T09:00:00-05:00\031Sean\031fix: a; b\000\ndef\0312026-11-09T09:00:00-05:00\031Sean\031init\000"
    let cs = GitParse.log out
    Assert.Equal<string[]>([| "abc"; "def" |], cs |> Array.map _.Sha)
    Assert.Equal("fix: a; b", cs[0].Subject)
    Assert.Equal(DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero), cs[0].Date)
