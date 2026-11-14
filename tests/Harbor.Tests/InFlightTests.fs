module Harbor.Tests.InFlightTests

open System
open System.IO
open Xunit
open Harbor
open Harbor.Domain

let private sh (dir: string) (args: string list) =
    match (Proc.run (TimeSpan.FromSeconds 10.) (Some dir) [] ("git" :: args)).Result with
    | Ok o when o.Code = 0 -> o.Out
    | other -> failwithf "git %A: %A" args other

let private commit (dir: string) (file: string) (text: string) =
    File.WriteAllText(Path.Combine(dir, file), text)
    sh dir [ "add"; file ] |> ignore
    sh dir [ "commit"; "-qm"; $"{file} {text}" ] |> ignore

/// A bare origin and one clone with: a pushed commit, one unpushed on main, two on a local-only
/// branch, a stash, a modified tracked file and an untracked one.
let private setup () =
    let root = Directory.CreateTempSubdirectory("harbor-inflight").FullName
    let origin = Path.Combine(root, "origin.git")
    sh root [ "init"; "-q"; "--bare"; "-b"; "main"; origin ] |> ignore
    let d = Path.Combine(root, "work", "w")
    sh root [ "clone"; "-q"; origin; d ] |> ignore
    sh d [ "config"; "user.email"; "t@example.org" ] |> ignore
    sh d [ "config"; "user.name"; "T" ] |> ignore
    commit d "x.txt" "1"
    sh d [ "push"; "-q"; "origin"; "main" ] |> ignore
    commit d "x.txt" "2"
    sh d [ "switch"; "-qc"; "feat" ] |> ignore
    commit d "y.txt" "a"
    commit d "y.txt" "b"
    sh d [ "switch"; "-q"; "main" ] |> ignore
    File.WriteAllText(Path.Combine(d, "x.txt"), "stashed")
    sh d [ "stash"; "-q" ] |> ignore
    File.WriteAllText(Path.Combine(d, "x.txt"), "edited")
    File.WriteAllText(Path.Combine(d, "secret.env"), "TOKEN=do-not-send")
    root, d

[<Fact>]
let ``unpushed commits, their branches and stashes are reported`` () =
    let root, d = setup ()
    try
        let r = (Git.scan d).Result
        let f = r.InFlight.Value
        Assert.Equal(3, f.UnpushedCount)
        Assert.Equal(1, f.Stashes)
        Assert.Equal<string[]>([| "feat"; "feat"; "main" |], f.Unpushed |> Array.map _.Branch |> Array.sort)
        Assert.Contains(f.Unpushed, fun c -> c.Subject = "y.txt b")
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``diffs show a commit or tracked changes, never untracked contents or other folders`` () =
    let root, d = setup ()
    try
        let s = Inventory.Store({ Config.defaults with Roots = [| Path.Combine(root, "work") |] })
        s.Rescan().Wait()
        let run arg path = (Agent.runJob s Config.defaultAllow (Roles.Controller("test", [||])) { Id = 1L; Kind = "diff"; Path = Some path; Arg = Some arg }).Result
        let sha = (sh d [ "rev-parse"; "feat" ]).Trim()
        let shown = run sha d
        Assert.True(shown.Ok, shown.Output)
        Assert.Contains("+b", shown.Output)
        let work = run "worktree" d
        Assert.True(work.Ok, work.Output)
        Assert.Contains("+edited", work.Output)
        Assert.Contains("secret.env", work.Output)
        Assert.DoesNotContain("do-not-send", work.Output)
        Assert.False((run "--output=/tmp/x" d).Ok)
        Assert.False((run "HEAD~1" d).Ok)
        Assert.False((run sha root).Ok) // not a scanned repo
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``the hub accepts diff jobs only for a repo and a commit or the worktree`` () =
    let job kind path arg : Hub.JobBody = { Kind = kind; Path = path; Arg = arg }
    Assert.Equal(Ok(), Hub.checkJob [||] (job "diff" (Some "/r") (Some "worktree")))
    Assert.Equal(Ok(), Hub.checkJob [||] (job "diff" (Some "/r") (Some "0123abcd")))
    Assert.Equal(Ok(), Hub.checkJob [||] (job "fetch" None None))
    for bad in [ job "diff" None (Some "worktree"); job "diff" (Some "/r") (Some "main"); job "diff" (Some "/r") None
                 job "fetch" None (Some "x"); job "rm" None None ] do
        Assert.True(Result.isError (Hub.checkJob [||] bad), string bad)
