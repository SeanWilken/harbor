module Harbor.Tests.PullTests

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

/// A bare "origin" plus two clones under one root: `a` pushes, `b` is the one we pull into.
let private setup () =
    let root = Directory.CreateTempSubdirectory("harbor-pull").FullName
    let origin = Path.Combine(root, "origin.git")
    sh root [ "init"; "-q"; "--bare"; "-b"; "main"; origin ] |> ignore
    let clone name =
        let d = Path.Combine(root, "work", name)
        sh root [ "clone"; "-q"; origin; d ] |> ignore
        sh d [ "config"; "user.email"; "t@example.org" ] |> ignore
        sh d [ "config"; "user.name"; "T" ] |> ignore
        d
    let a = clone "a"
    commit a "x.txt" "1"
    sh a [ "push"; "-q"; "origin"; "main" ] |> ignore
    let b = clone "b"
    commit a "x.txt" "2"
    sh a [ "push"; "-q"; "origin"; "main" ] |> ignore
    root, a, b

let private store (root: string) =
    let s = Inventory.Store({ Config.defaults with Roots = [| Path.Combine(root, "work") |] })
    s.Rescan().Wait()
    s

let private pull (s: Inventory.Store) (path: string) =
    match s.Pull(Some path).Result with
    | Ok [| _, o |] -> o
    | other -> failwithf "%A" other

[<Fact>]
let ``a clean clone fast-forwards to what the other machine pushed`` () =
    let root, a, b = setup ()
    try
        Assert.Equal(Inventory.Pulled 1, pull (store root) b)
        Assert.Equal(sh a [ "rev-parse"; "HEAD" ], sh b [ "rev-parse"; "HEAD" ])
        Assert.Equal(Inventory.UpToDate, pull (store root) b)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``local edits or unpushed commits are never pulled over`` () =
    let root, _, b = setup ()
    try
        let before = sh b [ "rev-parse"; "HEAD" ]
        File.WriteAllText(Path.Combine(b, "x.txt"), "mine")
        Assert.Equal(Inventory.Skipped "uncommitted changes", pull (store root) b)
        Assert.Equal("mine", File.ReadAllText(Path.Combine(b, "x.txt")))
        sh b [ "checkout"; "-q"; "--"; "x.txt" ] |> ignore
        commit b "y.txt" "local"
        Assert.Equal(Inventory.Skipped "diverged (1 local, 1 upstream commits)", pull (store root) b)
        Assert.NotEqual<string>(before, sh b [ "rev-parse"; "HEAD" ]) // still the local commit, not origin's
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``pull reports skipped single repos as failures and hides up-to-date ones in a full run`` () =
    Assert.Equal((false, "/r: skipped, detached HEAD"), Agent.pullResult (Some "/r") [| "/r", Inventory.Skipped "detached HEAD" |])
    Assert.Equal((true, "every repo was up to date"), Agent.pullResult None [| "/r", Inventory.UpToDate |])
    Assert.Equal((true, "/s: pulled 2 commit(s)"), Agent.pullResult None [| "/r", Inventory.UpToDate; "/s", Inventory.Pulled 2 |])
