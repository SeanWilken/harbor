module Harbor.Tests.GitParseTests

open System
open System.IO
open Xunit
open Harbor
open Harbor.Domain

let private z (records: string list) = String.Join("\000", records) + "\000"

[<Fact>]
let ``status reads branch headers and counts each kind of change`` () =
    let out =
        z [ "# branch.oid 1111111111111111111111111111111111111111"
            "# branch.head main"
            "# branch.upstream origin/main"
            "# branch.ab +2 -3"
            "1 M. N... 100644 100644 100644 aaaa bbbb staged.fs"
            "1 .M N... 100644 100644 100644 aaaa bbbb dir with space/edited.fs"
            "2 R. N... 100644 100644 100644 aaaa bbbb R100 new name.fs"
            "old name.fs"
            "u UU N... 100644 100644 100644 100644 aaaa bbbb cccc clash.fs"
            "? scratch.txt" ]
    let b, c = GitParse.status out
    Assert.Equal(Some "main", b.Head)
    Assert.Equal(Some "origin/main", b.Upstream)
    Assert.Equal((2, 3), (b.Ahead, b.Behind))
    Assert.Equal((2, 1, 1, 1), (c.Staged, c.Unstaged, c.Untracked, c.Conflicted))
    Assert.Equal<string[]>([| "staged.fs"; "dir with space/edited.fs"; "new name.fs"; "clash.fs"; "scratch.txt" |], c.Paths)

[<Fact>]
let ``status of a fresh detached repo has no head or oid`` () =
    let b, _ = GitParse.status (z [ "# branch.oid (initial)"; "# branch.head (detached)" ])
    Assert.Equal(None, b.Head)
    Assert.Equal(None, b.Oid)

[<Fact>]
let ``remotes keeps fetch urls only`` () =
    let out = "origin\tgit@github.com:me/x.git (fetch)\norigin\tgit@github.com:me/x.git (push)\nup\thttps://e.org/y (fetch)\n"
    Assert.Equal<Remote[]>(
        [| { Name = "origin"; Url = "git@github.com:me/x.git" }; { Name = "up"; Url = "https://e.org/y" } |],
        GitParse.remotes out)

[<Fact>]
let ``identity takes the most specific scope`` () =
    let out = "global\tuser.name Sean\nglobal\tuser.email home@example.org\nlocal\tuser.email work@example.org\n"
    Assert.Equal({ Name = Some "Sean"; Email = Some "work@example.org"; Scope = Some "local" }, GitParse.identity out)

let private sh (dir: string) (args: string list) =
    let r = (Proc.run (TimeSpan.FromSeconds 10.) (Some dir) [] ("git" :: args)).Result
    match r with
    | Ok o when o.Code = 0 -> o.Out
    | other -> failwithf "git %A: %A" args other

[<Fact>]
let ``scan agrees with a real repository`` () =
    let dir = Directory.CreateTempSubdirectory("harbor-test").FullName
    try
        sh dir [ "init"; "-q"; "-b"; "trunk" ] |> ignore
        sh dir [ "config"; "user.email"; "t@example.org" ] |> ignore
        sh dir [ "config"; "user.name"; "T" ] |> ignore
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1")
        sh dir [ "add"; "a.txt" ] |> ignore
        sh dir [ "commit"; "-qm"; "first" ] |> ignore
        File.WriteAllText(Path.Combine(dir, "a.txt"), "2")
        File.WriteAllText(Path.Combine(dir, "b.txt"), "new")
        let r = (Git.scan dir).Result
        Assert.Equal(None, r.Error)
        Assert.Equal(Some "trunk", r.Branch |> Option.bind _.Head)
        Assert.Equal(Some "first", r.LastCommit |> Option.map _.Subject)
        Assert.Equal(Some "local", r.Identity.Scope)
        Assert.Equal(Some(0, 1, 1), r.Changes |> Option.map (fun c -> c.Staged, c.Unstaged, c.Untracked))
        Assert.True(r.LastModified.IsSome)
    finally
        Directory.Delete(dir, true)
