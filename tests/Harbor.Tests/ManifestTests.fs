module Harbor.Tests.ManifestTests

open System
open Xunit
open Harbor
open Harbor.Domain

let private os = { Id = "zorin"; Version = "18.1"; PrettyName = "Zorin OS 18.1"; Kernel = "7.0.0-42-generic"; Arch = "x64" }
let private pkgs (xs: (string * string) list) = xs |> List.map (fun (n, v) -> { Name = n; Version = v }) |> Array.ofList
let private dpkg xs = { Manager = "dpkg"; Packages = pkgs xs; Upgrades = [||]; Error = None }
let private at = DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)

[<Fact>]
let ``package listings parse`` () =
    let d = Manifest.parseDpkg "amd64" "ii \tgit\t1:2.43.0\tamd64\nrc \told\t1.0\tamd64\nii \tlibc6\t2.39\ti386\nii \ttzdata\t2025a\tall\n"
    Assert.Equal<Package[]>(pkgs [ "git", "1:2.43.0"; "libc6:i386", "2.39"; "tzdata", "2025a" ], d)
    let u = Manifest.parseAptUpgradable "Listing...\nappstream/noble,noble 1.8.6 all [upgradable from: 1.8.5]\n"
    Assert.Equal<Upgrade[]>([| { Name = "appstream"; From = "1.8.5"; To = "1.8.6" } |], u)
    let s = Manifest.parseSnapList "Name  Version  Rev  Tracking  Publisher  Notes\nalacritty  0.17.0  180  latest/stable  snapcrafters*  classic\n"
    Assert.Equal<Package[]>(pkgs [ "alacritty", "0.17.0" ], s)
    Assert.Equal<Package[]>(pkgs [ "com.brave.Browser", "1.99.56"; "org.x.App", "" ], Manifest.parseFlatpak "com.brave.Browser\t1.99.56\norg.x.App\t\n")
    let rel = Manifest.parseOsRelease "PRETTY_NAME=\"Zorin OS 18.1\"\nID=zorin\n# comment\n"
    Assert.Equal("Zorin OS 18.1", rel["PRETTY_NAME"])

[<Fact>]
let ``same content gives the same hash whatever the order or time`` () =
    let a = Manifest.seal at os [| dpkg [ "git", "1"; "curl", "2" ] |] [||]
    let b = Manifest.seal (at.AddHours 5.) os [| dpkg [ "curl", "2"; "git", "1" ] |] [||]
    let c = Manifest.seal at os [| dpkg [ "curl", "3"; "git", "1" ] |] [||]
    Assert.Equal(a.Hash, b.Hash)
    Assert.NotEqual<string>(a.Hash, c.Hash)

[<Fact>]
let ``diff lists installed, removed and changed, only for managers on both sides`` () =
    let a = Manifest.seal at os [| dpkg [ "git", "1"; "curl", "2"; "old", "1" ] |] [||]
    let b = Manifest.seal at os [| dpkg [ "git", "1"; "curl", "3"; "new", "1" ]; { dpkg [ "x", "1" ] with Manager = "snap" } |] [||]
    let changes = Manifest.diff a b |> Array.map (fun c -> c.Name, c.Before, c.After)
    Assert.Equal<_[]>([| "curl", Some "2", Some "3"; "new", None, Some "1"; "old", Some "1", None |], changes)

[<Fact>]
let ``the recap week ends at 00:00 on the configured weekday`` () =
    let thu = DateTimeOffset(2026, 11, 12, 15, 30, 0, TimeSpan.FromHours -5.)
    let start, end' = Recap.period DayOfWeek.Monday thu
    Assert.Equal(DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.FromHours -5.), end')
    Assert.Equal(end'.AddDays -7., start)
    let _, onTheDay = Recap.period DayOfWeek.Thursday thu
    Assert.Equal(DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.FromHours -5.), onTheDay)

let private repo name behind ahead dirty (lastCommit: DateTimeOffset) =
    { Path = $"/srv/{name}"; Name = name; Remotes = [||]
      Identity = { Name = None; Email = None; Scope = None }
      Branch = Some { Head = Some "main"; Oid = Some "a"; Upstream = Some "origin/main"; Ahead = ahead; Behind = behind }
      Changes = Some { Staged = 0; Unstaged = (if dirty then 1 else 0); Untracked = 0; Conflicted = 0; Paths = [||] }
      LastCommit = Some { Date = lastCommit; Author = "me"; Subject = "x" }
      LastModified = Some lastCommit; WorktreeOf = None; LastFetch = None; FetchError = None; Error = None; Branches = None; BehindSince = None; InFlight = None }

[<Fact>]
let ``recap rules use transition times and thresholds`` () =
    let now = at
    let state r behindSince dirtySince : Recap.RepoState = { Machine = "devbox"; Repo = r; BehindSince = behindSince; DirtySince = dirtySince }
    let repos =
        [| state (repo "fresh-behind" 3 0 false (now.AddDays -1.)) (Some(now.AddDays -2.)) None   // behind 2 days: not yet
           state (repo "old-behind" 3 0 false (now.AddDays -1.)) (Some(now.AddDays -10.)) None   // behind 10 days
           state (repo "dirty" 0 0 true (now.AddDays -1.)) None (Some(now.AddDays -8.))
           state (repo "asleep" 0 0 false (now.AddDays -45.)) None None
           state (repo "unpushed" 0 2 false (now.AddDays -9.)) None None |]
    let behind, dirty, unpushed, neglected = Recap.repoNotes Recap.defaults now repos
    Assert.Equal<string[]>([| "old-behind" |], behind |> Array.map _.Name)
    Assert.Equal<string[]>([| "dirty" |], dirty |> Array.map _.Name)
    Assert.Equal<string[]>([| "unpushed" |], unpushed |> Array.map _.Name)
    Assert.Equal<string[]>([| "asleep" |], neglected |> Array.map _.Name)

[<Fact>]
let ``drift lists packages two machines have at different versions`` () =
    let m1 = Manifest.seal at os [| dpkg [ "git", "1"; "curl", "2"; "only-here", "1" ] |] [||]
    let m2 = Manifest.seal at os [| dpkg [ "git", "1"; "curl", "3" ] |] [||]
    let d = Recap.drift [ "a", m1; "b", m2 ]
    Assert.Equal<string[]>([| "curl" |], d |> Array.map _.Name)
    Assert.Equal<(string * string)[]>([| "a", "2"; "b", "3" |], d[0].Versions)

[<Fact>]
let ``only changed or removed rows are rewritten`` () =
    let key (k, _) = k
    Assert.Equal(Storage.All [| "a", 1 |], Storage.rowChanges None [| "a", 1 |] key)
    match Storage.rowChanges (Some [| "a", 1; "b", 2; "c", 3 |]) [| "a", 1; "b", 5; "d", 4 |] key with
    | Storage.Changed(gone, rows) ->
        Assert.Equal<string list>([ "c" ], gone)
        Assert.Equal<(string * int) list>([ "b", 5; "d", 4 ], rows)
    | other -> failwith $"expected Changed, got {other}"
    Assert.Equal(Storage.Changed([], []), Storage.rowChanges (Some [| "a", 1 |]) [| "a", 1 |] key)

[<Theory>]
[<InlineData(26300, "Professional", "25H2", "Windows 11 Pro 25H2")>]
[<InlineData(19045, "Core", "22H2", "Windows 10 Home 22H2")>]
[<InlineData(22000, "", "", "Windows 11")>]
let ``Windows is named by build, not by the 10.0 version it reports`` (build: int, edition: string, display: string, expected: string) =
    Assert.Equal(expected, Manifest.windowsName build (Some edition) (Some display))

[<Fact>]
let ``access entries need an address of the right kind`` () =
    let entry kind url host = { Kind = kind; Name = None; Url = url; Host = host; Port = None } : AccessEntry
    Assert.Equal(Some "https://devbox.example.ts.net", (Config.checkAccess (entry "display" (Some "https://devbox.example.ts.net/") None)).Url)
    Assert.Equal(Some "desktop-win", (Config.checkAccess (entry "rdp" None (Some "desktop-win"))).Host)
    Assert.ThrowsAny<exn>(fun () -> Config.checkAccess (entry "display" (Some "file:///etc/passwd") None) |> ignore) |> ignore
    Assert.ThrowsAny<exn>(fun () -> Config.checkAccess (entry "rdp" None None) |> ignore) |> ignore
    Assert.ThrowsAny<exn>(fun () -> Config.checkAccess (entry "ssh" None (Some "h")) |> ignore) |> ignore
