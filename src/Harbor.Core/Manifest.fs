/// What is installed on this machine: OS, packages per manager, pending upgrades, repos.
/// Collectors read local state only (no `apt update`, no store queries), so the same machine gives
/// the same manifest. Missing managers are skipped; a manager that fails is reported, not fatal.
module Harbor.Manifest

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Harbor.Domain

let private lines (s: string) = s.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

// ---- pure parsers ----

/// /etc/os-release: KEY=value or KEY="value".
let parseOsRelease (text: string) : Map<string, string> =
    lines text
    |> Array.choose (fun l ->
        match l.IndexOf '=' with
        | i when i > 0 && not (l.StartsWith "#") -> Some(l.Substring(0, i), l.Substring(i + 1).Trim('"', '\''))
        | _ -> None)
    |> Map.ofArray

/// `dpkg-query -W -f '${db:Status-Abbrev}\t${Package}\t${Version}\t${Architecture}\n'`: installed
/// ("ii") only. Foreign-architecture packages keep their suffix (libc6:i386).
let parseDpkg (native: string) (out: string) : Package[] =
    lines out
    |> Array.choose (fun l ->
        match l.Split('\t') with
        | [| status; name; version; arch |] when status.StartsWith "ii" ->
            let name = if arch = "all" || arch = native then name else $"{name}:{arch}"
            Some { Name = name; Version = version }
        | _ -> None)

/// `apt list --upgradable`: `name/suite version arch [upgradable from: old]`.
let parseAptUpgradable (out: string) : Upgrade[] =
    let re = RegularExpressions.Regex(@"^([^/\s]+)/\S+\s+(\S+)\s+\S+\s+\[upgradable from: ([^\]]+)\]")
    lines out
    |> Array.choose (fun l ->
        let m = re.Match l
        if m.Success then Some { Name = m.Groups[1].Value; From = m.Groups[3].Value; To = m.Groups[2].Value } else None)

/// `snap list`: header row, then Name Version Rev Tracking Publisher Notes.
let parseSnapList (out: string) : Package[] =
    lines out
    |> Array.skip 1
    |> Array.choose (fun l ->
        match l.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
        | cols when cols.Length >= 2 -> Some { Name = cols[0]; Version = cols[1] }
        | _ -> None)

/// `flatpak list --app --columns=application,version`: tab-separated, version may be empty.
let parseFlatpak (out: string) : Package[] =
    out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun l ->
        match l.Split('\t') with
        | [| app; version |] when app.Trim() <> "" -> Some { Name = app.Trim(); Version = version.Trim() }
        | [| app |] when app.Trim() <> "" -> Some { Name = app.Trim(); Version = "" }
        | _ -> None)

/// Sort everything, then hash the content (not the time it was taken).
let seal (takenAt: DateTimeOffset) (os: OsInfo) (sets: PackageSet[]) (repos: ManifestRepo[]) : Manifest =
    let sets =
        sets
        |> Array.map (fun s ->
            { s with
                Packages = s.Packages |> Array.sortBy (fun p -> p.Name, p.Version)
                Upgrades = s.Upgrades |> Array.sortBy _.Name })
        |> Array.sortBy _.Manager
    let repos = repos |> Array.sortBy _.Path
    let canonical = JsonSerializer.Serialize({| os = os; sets = sets; repos = repos |}, Config.jsonOptions)
    let hash = SHA256.HashData(Encoding.UTF8.GetBytes canonical) |> Convert.ToHexStringLower
    { TakenAt = takenAt; Hash = hash; Os = os; Sets = sets; Repos = repos }

// ---- diffs ----

type PackageChange =
    { Manager: string
      Name: string
      /// None = not installed on that side.
      Before: string option
      After: string option }

/// Packages that differ between two manifests (same machine over time, or two machines).
/// Only managers present on both sides are compared: dpkg vs nothing says nothing.
let diff (a: Manifest) (b: Manifest) : PackageChange[] =
    let byManager (m: Manifest) = m.Sets |> Array.filter (fun s -> s.Error.IsNone) |> Array.map (fun s -> s.Manager, s) |> Map.ofArray
    let ma, mb = byManager a, byManager b
    [| for KeyValue(mgr, sa) in ma do
           match mb.TryFind mgr with
           | None -> ()
           | Some sb ->
               let pa = sa.Packages |> Array.map (fun p -> p.Name, p.Version) |> Map.ofArray
               let pb = sb.Packages |> Array.map (fun p -> p.Name, p.Version) |> Map.ofArray
               for name in Set.union (pa.Keys |> Set.ofSeq) (pb.Keys |> Set.ofSeq) do
                   let before, after = pa.TryFind name, pb.TryFind name
                   if before <> after then
                       yield { Manager = mgr; Name = name; Before = before; After = after } |]
    |> Array.sortBy (fun c -> c.Manager, c.Name)

// ---- collectors ----

let private run (argv: string list) : Task<Result<string, string> option> =
    task {
        match! Proc.run (TimeSpan.FromSeconds 60.) None [ "LC_ALL", "C" ] argv with
        | Ok o when o.Code = 0 -> return Some(Ok o.Out)
        | Ok o -> return Some(Error((if o.Err.Trim() = "" then $"exit {o.Code}" else o.Err.Trim()).Split('\n')[0]))
        | Error e when e.Contains "No such file" || e.Contains "cannot find" || e.Contains "not found" -> return None // not installed
        | Error e -> return Some(Error e)
    }

let private set manager (packages: Result<Package[], string>) (upgrades: Upgrade[]) =
    match packages with
    | Ok ps -> { Manager = manager; Packages = ps; Upgrades = upgrades; Error = None }
    | Error e -> { Manager = manager; Packages = [||]; Upgrades = [||]; Error = Some e }

let private nativeArch () =
    match RuntimeInformation.OSArchitecture with
    | Architecture.X64 -> "amd64"
    | Architecture.Arm64 -> "arm64"
    | Architecture.X86 -> "i386"
    | a -> (string a).ToLowerInvariant()

let private dpkg () =
    task {
        match! run [ "dpkg-query"; "-W"; "-f"; "${db:Status-Abbrev}\t${Package}\t${Version}\t${Architecture}\n" ] with
        | None -> return None
        | Some r ->
            let! upgrades = run [ "apt"; "list"; "--upgradable" ]
            let ups = match upgrades with Some(Ok o) -> parseAptUpgradable o | _ -> [||]
            return Some(set "dpkg" (r |> Result.map (parseDpkg (nativeArch ()))) ups)
    }

let private simple manager argv parse =
    task {
        match! run argv with
        | None -> return None
        | Some r -> return Some(set manager (r |> Result.map parse) [||])
    }

/// Windows 11 still reports itself as 10.0; build 22000 and later is 11.
/// EditionID "Professional" -> "Pro", "Core" -> "Home"; DisplayVersion is e.g. "24H2".
let windowsName (build: int) (edition: string option) (displayVersion: string option) =
    let ed =
        match edition with
        | Some "Professional" -> Some "Pro"
        | Some "Core" -> Some "Home"
        | Some e when e <> "" -> Some e
        | _ -> None
    [ Some(if build >= 22000 then "Windows 11" else "Windows 10"); ed; displayVersion |> Option.filter ((<>) "") ]
    |> List.choose id
    |> String.concat " "

let private windowsDescription () =
    let reg (name: string) =
        if OperatingSystem.IsWindows() then
            try
                match Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", name, null) with
                | :? string as s -> Some s
                | _ -> None
            with _ -> None
        else None
    windowsName Environment.OSVersion.Version.Build (reg "EditionID") (reg "DisplayVersion")

/// The OS as people name it: os-release's PRETTY_NAME on Linux (what .NET reports), "Windows 11 Pro 24H2" on Windows.
let description () =
    if OperatingSystem.IsWindows() then windowsDescription () else RuntimeInformation.OSDescription

let os () : OsInfo =
    let arch = (string RuntimeInformation.OSArchitecture).ToLowerInvariant()
    if OperatingSystem.IsWindows() then
        { Id = "windows"; Version = string Environment.OSVersion.Version; PrettyName = description ()
          Kernel = string Environment.OSVersion.Version; Arch = arch }
    else
        let rel = try parseOsRelease (File.ReadAllText "/etc/os-release") with _ -> Map.empty
        let get k d = rel.TryFind k |> Option.defaultValue d
        { Id = get "ID" "linux"
          Version = get "VERSION" (get "VERSION_ID" "")
          PrettyName = get "PRETTY_NAME" RuntimeInformation.OSDescription
          Kernel = (try File.ReadAllText("/proc/sys/kernel/osrelease").Trim() with _ -> string Environment.OSVersion.Version)
          Arch = arch }

/// Linux package managers for now; Windows (winget) and language-level ones (npm -g, pipx,
/// dotnet tools) are later collectors.
let collect (repos: Repo[]) : Task<Manifest> =
    task {
        let dpkg = dpkg ()
        let snap = simple "snap" [ "snap"; "list" ] parseSnapList
        let flatpak = simple "flatpak" [ "flatpak"; "list"; "--app"; "--columns=application,version" ] parseFlatpak
        let! dpkg = dpkg
        let! snap = snap
        let! flatpak = flatpak
        let sets = [| dpkg; snap; flatpak |] |> Array.choose id
        let manifestRepos =
            repos
            |> Array.map (fun r ->
                { Path = r.Path
                  RemoteKey = GitParse.primaryRemote r.Remotes |> Option.bind (fun x -> GitParse.remoteKey x.Url)
                  Branch = r.Branch |> Option.bind _.Head })
        return seal DateTimeOffset.Now (os ()) sets manifestRepos
    }
