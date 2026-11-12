module Harbor.Tests.CleanupTests

open System
open System.IO
open System.Text.Json
open Xunit
open Harbor
open Harbor.Domain

[<Fact>]
let ``a linked worktree points at its main working tree`` () =
    Assert.Equal(
        Some "/srv/repositories/myAI/core",
        GitParse.worktreeOf "/srv/workspaces/suite/core" "/srv/repositories/myAI/core/.git\n")

[<Fact>]
let ``a normal repo and a submodule are not worktrees`` () =
    Assert.Equal(None, GitParse.worktreeOf "/home/me/src/harbor" "/home/me/src/harbor/.git")
    Assert.Equal(None, GitParse.worktreeOf "/srv/app/lib" "/srv/app/.git/modules/lib")

[<Fact>]
let ``discovery skips dot-folders below a root but not the root itself`` () =
    let root = Path.Combine(Path.GetTempPath(), $"harbor-disc-{Guid.NewGuid():N}")
    let repo (rel: string) = Directory.CreateDirectory(Path.Combine(root, rel, ".git")) |> ignore
    try
        repo "proj"
        repo ".oh-my-zsh"
        repo ".config/tool"
        let found = Discovery.find [||] true 5 [| root |] |> Array.map (fun p -> Path.GetRelativePath(root, p))
        Assert.Equal<string[]>([| "proj" |], found)
        let all = Discovery.find [||] false 5 [| root |] |> Array.map (fun p -> Path.GetRelativePath(root, p))
        Assert.Equal<string[]>([| ".config/tool"; ".oh-my-zsh"; "proj" |], all)
        Assert.Equal<string[]>([| Path.Combine(root, ".oh-my-zsh") |], Discovery.find [||] true 5 [| Path.Combine(root, ".oh-my-zsh") |])
    finally
        Directory.Delete(root, true)

let private job sent finished : HarborDb.``public``.job =
    { id = 1L; machine_id = 1L; kind = "fetch"; path = None; requested_at = DateTime.UtcNow; requested_by = "local"
      sent_at = sent; finished_at = finished; ok = None; output = None }

[<Fact>]
let ``jobs are sent once, resent only after the agent went quiet, never after finishing`` () =
    let now = DateTime.UtcNow
    Assert.True(Storage.isDue now (job None None))
    Assert.False(Storage.isDue now (job (Some(now.AddMinutes -1.)) None))
    Assert.True(Storage.isDue now (job (Some(now.AddMinutes -6.)) None))
    Assert.False(Storage.isDue now (job None (Some now)))

[<Fact>]
let ``an agent refuses job kinds it does not allow`` () =
    let store = Inventory.Store Config.defaults
    let r = (Agent.runJob store [||] { Id = 7L; Kind = "fetch"; Path = None }).Result
    Assert.False r.Ok
    Assert.Contains("not allowed", r.Output)

[<Fact>]
let ``payloads stored before jobs and worktrees still deserialize`` () =
    let v1 =
        """{ "version": 1, "machine": "devbox", "pushSeconds": 60, "commits": [],
             "snapshot": { "machine": { "hostname": "z", "os": "linux", "scannedAt": "2026-11-12T00:00:00Z", "scanMs": 1, "roots": [] },
                           "services": [],
                           "repos": [ { "path": "/srv/a", "name": "a", "remotes": [], "identity": {} } ] } }"""
    let push = JsonSerializer.Deserialize<Push>(v1, Config.jsonOptions)
    Assert.Equal(None, push.JobResults)
    Assert.Equal(None, push.Snapshot.Repos[0].WorktreeOf)
    Assert.Equal(None, push.Snapshot.Repos[0].LastFetch)
