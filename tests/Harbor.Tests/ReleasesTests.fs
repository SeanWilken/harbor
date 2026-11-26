module Harbor.Tests.ReleasesTests

open System
open System.IO
open Xunit
open Harbor
open Harbor.Domain

let private compose = """
services:
  api:
    image: ${API_IMAGE}
    environment:
      DB_PASSWORD: ${DB_PASSWORD}
    profiles: ["core", "all"]
  web:
    image: ${WEB_IMAGE:-example/web:local}
    build: ./web
    profiles: ["all"]
  db:
    image: postgres:16
    profiles: ["core", "all"]
"""

/// A repo with a monorepo-style deploy folder (compose, .env), migrations at the root and a runner.
let private withRepo (runner: bool) (test: string -> unit) =
    let repo = Path.Combine(Path.GetTempPath(), $"harbor-rel-{Guid.NewGuid():N}")
    let deploy = Path.Combine(repo, "deploy")
    Directory.CreateDirectory deploy |> ignore
    Directory.CreateDirectory(Path.Combine(repo, "migrations")) |> ignore
    Directory.CreateDirectory(Path.Combine(repo, "node_modules", "x")) |> ignore
    File.WriteAllText(Path.Combine(deploy, "docker-compose.yml"), compose)
    File.WriteAllText(Path.Combine(deploy, ".env"), "API_IMAGE=\"example/api:1.2\"\nDB_PASSWORD=hunter2\n# comment\n")
    File.WriteAllText(Path.Combine(repo, "node_modules", "x", "compose.yaml"), "services: {}")
    for n in [ "0001_init"; "0002_more" ] do File.WriteAllText(Path.Combine(repo, "migrations", n + ".sql"), "")
    if runner then File.WriteAllText(Path.Combine(repo, "migration-runner.sh"), "")
    try test repo finally Directory.Delete(repo, true)

[<Fact>]
let ``a deploy folder is found below the root, skipping dependency folders`` () =
    withRepo true (fun repo ->
        let units = Releases.detect false repo
        Assert.Equal<string list>([ "deploy" ], units |> Array.map _.Path |> List.ofArray)
        let u = units[0]
        Assert.Equal<string list>([ "all"; "core" ], List.ofArray u.Profiles)
        Assert.Equal(Some "example/api:1.2", (u.Services |> Array.find (fun s -> s.Name = "api")).Image)
        Assert.Equal(Some "example/web:local", (u.Services |> Array.find (fun s -> s.Name = "web")).Image)
        Assert.Equal<string list>([ "pull"; "build"; "migrate"; "up" ], List.ofArray u.Steps)
        Assert.Equal(Some "migration-runner.sh", u.Migrations.Value.Runner)
        Assert.Equal(Some "0002_more", u.Migrations.Value.Latest)
        Assert.Empty u.Problems
        // only image variables are read from .env
        Assert.DoesNotContain("hunter2", Text.Json.JsonSerializer.Serialize(units, Config.jsonOptions)))

[<Fact>]
let ``migrations without a runner are a problem, not a step`` () =
    withRepo false (fun repo ->
        let u = (Releases.detect false repo)[0]
        Assert.DoesNotContain("migrate", u.Steps)
        Assert.Contains(u.Problems, fun p -> p.Contains "no migration-runner.sh runner"))

[<Fact>]
let ``variables resolve from env or their default, and an unset one is named`` () =
    let env = Map [ "A", "x"; "EMPTY", "" ]
    Assert.Equal(Ok "x/y:1", Releases.resolve env "${A}/y:1")
    Assert.Equal(Ok "d", Releases.resolve env "${EMPTY:-d}")
    Assert.Equal(Ok "x", Releases.resolve env "$A")
    Assert.Equal(Error "B", Releases.resolve env "${B}")

[<Fact>]
let ``a request is checked against the unit: steps it offers, its profiles, run order`` () =
    withRepo true (fun repo ->
        let units = Releases.detect false repo
        let req steps profile : Releases.Request = { Unit = "deploy"; Profile = profile; Steps = steps }
        match Releases.check units (req [| "up"; "pull"; "migrate" |] (Some "core")) with
        | Ok(_, steps) -> Assert.Equal<string list>([ "pull"; "migrate"; "up" ], List.ofArray steps)
        | Error e -> failwith e
        Assert.True(Result.isOk (Releases.check units (req [| "status" |] None)))
        for bad in [ req [| "up" |] (Some "nope"); req [| "rm -rf" |] None; req [||] None
                     req [| "status"; "up" |] None; req [| "up"; "up" |] None
                     { req [| "up" |] None with Unit = "elsewhere" } ] do
            Assert.True(Result.isError (Releases.check units bad), $"%A{bad}")
        // the hub runs the same check, through the job endpoint's validation
        let body arg : Hub.JobBody = { Kind = "release"; Path = Some repo; Arg = Some arg }
        Assert.Equal(Ok(), Hub.checkJob [||] units (body (Releases.encode (req [| "up" |] (Some "all")))))
        Assert.True(Result.isError (Hub.checkJob [||] units { body "{}" with Path = Some "/elsewhere" }))
        Assert.True(Result.isError (Hub.checkJob [||] units (body "not json"))))

[<Fact>]
let ``steps become compose or runner commands, with only the checked profile from the request`` () =
    withRepo true (fun repo ->
        let u = (Releases.detect false repo)[0]
        let dir, argv = Releases.command "podman" false u (Some "core") "up"
        Assert.Equal(Path.Combine(repo, "deploy"), dir)
        Assert.Equal<string list>([ "podman"; "compose"; "-f"; "docker-compose.yml"; "--profile"; "core"; "up"; "-d"; "--force-recreate"; "--remove-orphans" ], argv)
        let dir, argv = Releases.command "podman" false u None "status"
        Assert.Equal(repo, dir)
        Assert.Equal<string list>([ "bash"; Path.Combine(repo, "migration-runner.sh"); "--action"; "status"; "--json" ], argv))

[<Fact>]
let ``running containers are matched to their unit by compose's working folder`` () =
    let json = """[{"Names":["app_api_1"],"Image":"example/api:1.2","State":"running",
                     "Labels":{"com.docker.compose.project.working_dir":"/r/deploy","com.docker.compose.service":"api"}},
                   {"Names":["other"],"Image":"x","State":"exited","Labels":{}}]"""
    let found = Releases.parseContainers "podman" json
    Assert.Equal(1, found.Length)
    let u : ReleaseUnit =
        { Repo = "/r"; Path = "deploy"; Compose = "compose.yaml"; Services = [||]; Profiles = [||]; Migrations = None
          Steps = [| "up" |]; Running = [||]; Problems = [||] }
    let r = Releases.withRunning found [| u |]
    Assert.Equal("api", r[0].Running[0].Service)
    let docker = """{"Names":"web","Image":"w","State":"running","Labels":"com.docker.compose.project.working_dir=/r/deploy,com.docker.compose.service=web"}"""
    let fromDocker = Releases.parseContainers "docker" docker
    Assert.Equal("web", (snd fromDocker[0]).Service)

[<Fact>]
let ``a unit's latest failed release alerts until one succeeds; status checks don't count`` () =
    let now = DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)
    let m : Storage.MachineSummary =
        { Id = 3L; Name = "devbox"; NodeName = None; Login = None; Os = None; Status = "approved"; Local = false
          FirstSeenAt = now; LastPushAt = Some now; ScannedAt = None; PushSeconds = Some 300
          Repos = 0; Dirty = 0; Unsynced = 0; ServicesUp = 0; ServicesTotal = 0; Hidden = [||]
          CpuPercent = None; GpuPercent = None; MemPercent = None; IdleSeconds = None; Connected = true
          AgentCommit = None; Update = None; Role = None; Work = None; RoleSince = None }
    let job id ok steps output : int64 * Storage.JobInfo =
        3L,
        { Id = id; Kind = "release"; Path = Some "/r"; RequestedAt = now; RequestedBy = "a"; SentAt = None
          FinishedAt = Some now; Ok = Some ok; Output = Some output
          Arg = Some(Releases.encode { Unit = "deploy"; Profile = None; Steps = steps }) }
    let failed = job 2L false [| "pull"; "up" |] "── pull: podman compose pull\nerr\n── pull failed (exit 1)"
    let found = Alerts.releaseFailed [| m |] [| job 3L false [| "status" |] "boom"; failed; job 1L true [| "up" |] "" |]
    Assert.Equal<string list>([ "deploy on devbox: release failed" ], found |> Array.map _.Title |> List.ofArray)
    Assert.Equal(Some "pull failed (exit 1)", found[0].Detail)
    Assert.Empty(Alerts.releaseFailed [| m |] [| job 4L true [| "up" |] ""; failed |])

[<Fact>]
let ``recipes keep harbor's step order, and a unit whose services all have profiles defaults to all`` () =
    Assert.Equal(Ok [| "pull"; "up" |], ReleaseView.checkRecipe [| "up"; "pull" |])
    Assert.True(Result.isError (ReleaseView.checkRecipe [| "status" |]))
    withRepo true (fun repo ->
        let units = Releases.detect false repo
        Assert.Equal(Some "all", ReleaseView.defaultProfile units[0]))
