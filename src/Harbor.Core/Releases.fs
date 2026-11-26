/// Release units: compose folders in tracked repos, what they declare, what's running from them,
/// and the steps harbor can run on them (docs/RELEASE_INTEGRATION.md). Detection only reads files
/// (and lists containers); a release runs compose itself, plus the repo's migration runner.
module Harbor.Releases

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Harbor.Domain
open YamlDotNet.RepresentationModel

let composeNames = [| "compose.yaml"; "compose.yml"; "docker-compose.yaml"; "docker-compose.yml" |]

/// The order steps always run in; "status" is read-only and runs alone.
let stepOrder = [| "pull"; "build"; "migrate"; "up" |]

let private nn (s: string | null) = match s with null -> "" | s -> s

let private skipDirs = set [ "node_modules"; "bin"; "obj"; "dist"; "build"; "target"; "vendor"; "worktrees" ]

/// Folders (the repo root first, then up to `depth` levels down) that hold a compose file.
let findUnits (depth: int) (repo: string) : string list =
    let rec walk (dir: string) (level: int) =
        [ if composeNames |> Array.exists (fun n -> File.Exists(Path.Combine(dir, n))) then
              yield dir
          if level < depth then
              let subdirs =
                  try Directory.GetDirectories dir |> Array.sort with _ -> [||]
              for d in subdirs do
                  let name = nn (Path.GetFileName d)
                  if not (name.StartsWith ".") && not (skipDirs.Contains name) then
                      yield! walk d (level + 1) ]
    walk repo 0

/// KEY=VALUE lines of a .env file (quotes stripped, `export ` allowed, comments skipped).
let parseEnv (text: string) : Map<string, string> =
    text.Split '\n'
    |> Array.choose (fun line ->
        let l = line.Trim()
        let l = if l.StartsWith "export " then l.Substring 7 else l
        match l.IndexOf '=' with
        | i when i > 0 && not (l.StartsWith "#") ->
            let v = l.Substring(i + 1).Trim()
            let v =
                if v.Length >= 2 && ((v.StartsWith "\"" && v.EndsWith "\"") || (v.StartsWith "'" && v.EndsWith "'")) then
                    v.Substring(1, v.Length - 2)
                else
                    v
            Some(l.Substring(0, i).Trim(), v)
        | _ -> None)
    |> Map.ofArray

let private varPattern = Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::?-([^}]*))?\}|\$([A-Za-z_][A-Za-z0-9_]*)")

/// Variable names an image reference uses.
let varsIn (s: string) =
    [ for m in varPattern.Matches s -> if m.Groups[1].Success then m.Groups[1].Value else m.Groups[3].Value ]

/// Substitute ${X}, ${X:-default}, ${X-default} and $X; Error names the first unset variable.
let resolve (env: Map<string, string>) (s: string) : Result<string, string> =
    let mutable missing = None
    let out =
        varPattern.Replace(s, MatchEvaluator(fun m ->
            let name = if m.Groups[1].Success then m.Groups[1].Value else m.Groups[3].Value
            match env.TryFind name with
            | Some v when v <> "" -> v
            | _ when m.Groups[2].Success -> m.Groups[2].Value
            | Some v -> v
            | None ->
                missing <- Some name
                ""))
    match missing with
    | Some name -> Error name
    | None -> Ok out

type ParsedService = { Name: string; Image: string option; Builds: bool; Profiles: string[] }

let private scalar (n: YamlNode) =
    match n with
    | :? YamlScalarNode as s -> Option.ofObj s.Value
    | _ -> None

let private child (key: string) (m: YamlMappingNode) =
    m.Children
    |> Seq.tryFind (fun kv -> scalar kv.Key = Some key)
    |> Option.map _.Value

/// The services section of a compose file: names, raw image references, build sections, profiles.
let parseCompose (yaml: string) : Result<ParsedService[], string> =
    try
        let stream = YamlStream()
        stream.Load(new StringReader(yaml))
        match stream.Documents |> Seq.tryHead |> Option.map _.RootNode with
        | Some(:? YamlMappingNode as root) ->
            match child "services" root with
            | Some(:? YamlMappingNode as services) ->
                services.Children
                |> Seq.choose (fun kv ->
                    match scalar kv.Key, kv.Value with
                    | Some name, (:? YamlMappingNode as svc) ->
                        let profiles =
                            match child "profiles" svc with
                            | Some(:? YamlSequenceNode as xs) -> xs.Children |> Seq.choose scalar |> Array.ofSeq
                            | _ -> [||]
                        Some { Name = name; Image = child "image" svc |> Option.bind scalar; Builds = (child "build" svc).IsSome; Profiles = profiles }
                    | Some name, _ -> Some { Name = name; Image = None; Builds = false; Profiles = [||] }
                    | None, _ -> None)
                |> Array.ofSeq
                |> Ok
            | _ -> Error "no services section"
        | _ -> Error "not a compose file"
    with ex ->
        Error $"unreadable: {ex.Message}"

let private relative (repo: string) (path: string) =
    match Path.GetRelativePath(repo, path) with
    | "." -> ""
    | r -> r.Replace('\\', '/')

/// The runner a platform calls, per the contract.
let runnerName (windows: bool) = if windows then "migrate-db.ps1" else "migrate-db.sh"

/// A migrations folder (unit first, then repo root) and the runner next to it.
let migrations (windows: bool) (repo: string) (unitDir: string) : MigrationInfo option =
    let firstIn (dirs: string list) (pick: string -> bool) = dirs |> List.distinct |> List.tryFind pick
    let dirs = [ unitDir; repo ]
    firstIn dirs (fun d -> Directory.Exists(Path.Combine(d, "migrations")))
    |> Option.map (fun d ->
        let files = Directory.GetFiles(Path.Combine(d, "migrations"), "*.sql") |> Array.map (Path.GetFileNameWithoutExtension >> nn) |> Array.sort
        { Dir = relative repo (Path.Combine(d, "migrations"))
          Files = files.Length
          Latest = Array.tryLast files
          Runner =
            firstIn dirs (fun d -> File.Exists(Path.Combine(d, runnerName windows)))
            |> Option.map (fun d -> relative repo (Path.Combine(d, runnerName windows))) })

/// Everything about one unit except what's running (that comes from the container engine).
let describe (windows: bool) (repo: string) (unitDir: string) : ReleaseUnit =
    let compose = composeNames |> Array.find (fun n -> File.Exists(Path.Combine(unitDir, n)))
    let parsed = parseCompose (File.ReadAllText(Path.Combine(unitDir, compose)))
    let services = match parsed with Ok s -> s | Error _ -> [||]
    // only the variables image lines use are read from .env; nothing else in it leaves this function
    let wanted = services |> Array.collect (fun s -> s.Image |> Option.map varsIn |> Option.defaultValue [] |> Array.ofList) |> Set.ofArray
    let envFile = Path.Combine(unitDir, ".env")
    let env =
        if wanted.IsEmpty || not (File.Exists envFile) then Map.empty
        else parseEnv (File.ReadAllText envFile) |> Map.filter (fun k _ -> wanted.Contains k)
    let problems = ResizeArray<string>()
    match parsed with
    | Error e -> problems.Add $"{compose}: {e}"
    | Ok _ -> ()
    let releaseServices =
        services
        |> Array.map (fun s ->
            let image =
                s.Image
                |> Option.bind (fun raw ->
                    match resolve env raw with
                    | Ok i -> Some i
                    | Error name ->
                        problems.Add $"{s.Name}: image variable {name} is not set in .env"
                        None)
            { ReleaseService.Name = s.Name; Image = image; Builds = s.Builds; Profiles = s.Profiles })
    let mig = migrations windows repo unitDir
    match mig with
    | Some m when m.Runner.IsNone -> problems.Add $"{m.Dir} has {m.Files} migrations but no {runnerName windows} runner harbor can call"
    | _ -> ()
    let steps =
        [| if releaseServices |> Array.exists (fun s -> s.Image.IsSome) then "pull"
           if releaseServices |> Array.exists _.Builds then "build"
           if mig |> Option.exists (fun m -> m.Runner.IsSome) then "migrate"
           "up" |]
    { Repo = repo
      Path = relative repo unitDir
      Compose = compose
      Services = releaseServices
      Profiles = releaseServices |> Array.collect _.Profiles |> Array.distinct |> Array.sort
      Migrations = mig
      Steps = steps
      Running = [||]
      Problems = problems.ToArray() }

/// Units in one repo; a unit that can't be read becomes a problem, not a missing unit.
let detect (windows: bool) (repo: string) : ReleaseUnit[] =
    findUnits 3 repo
    |> List.map (fun dir ->
        try
            describe windows repo dir
        with ex ->
            { Repo = repo; Path = relative repo dir; Compose = ""; Services = [||]; Profiles = [||]; Migrations = None
              Steps = [||]; Running = [||]; Problems = [| ex.Message |] })
    |> Array.ofList

// ---- container engine ----

let private onPath (exe: string) =
    let names = if OperatingSystem.IsWindows() then [ exe + ".exe"; exe ] else [ exe ]
    (defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "PATH")) "").Split(Path.PathSeparator)
    |> Array.exists (fun d -> d <> "" && names |> List.exists (fun n -> File.Exists(Path.Combine(d, n))))

/// podman where it's installed, else docker; None when neither is.
let engine =
    lazy (if onPath "podman" then Some "podman" elif onPath "docker" then Some "docker" else None)

/// Containers compose started, keyed by its working-directory label.
let parseContainers (engine: string) (json: string) : (string * ReleaseContainer)[] =
    let str (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
        | true, v when v.ValueKind = JsonValueKind.Array && v.GetArrayLength() > 0 -> v[0].GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""
    let labels (e: JsonElement) : Map<string, string> =
        match e.TryGetProperty "Labels" with
        | true, v when v.ValueKind = JsonValueKind.Object ->
            v.EnumerateObject() |> Seq.map (fun p -> p.Name, (p.Value.GetString() |> Option.ofObj |> Option.defaultValue "")) |> Map.ofSeq
        | true, v when v.ValueKind = JsonValueKind.String -> // docker: "a=b,c=d"
            (v.GetString() |> Option.ofObj |> Option.defaultValue "").Split ','
            |> Array.choose (fun kv -> match kv.IndexOf '=' with i when i > 0 -> Some(kv.Substring(0, i), kv.Substring(i + 1)) | _ -> None)
            |> Map.ofArray
        | _ -> Map.empty
    let one (e: JsonElement) =
        let l = labels e
        match l.TryFind "com.docker.compose.project.working_dir" with
        | Some dir ->
            Some(dir.TrimEnd('/', '\\'),
                 { Name = str e "Names"
                   Service = l.TryFind "com.docker.compose.service" |> Option.defaultValue ""
                   Image = str e "Image"
                   State = str e "State" })
        | None -> None
    try
        if engine = "docker" then
            json.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun line -> use d = JsonDocument.Parse line in one d.RootElement)
        else
            use d = JsonDocument.Parse json
            d.RootElement.EnumerateArray() |> Seq.choose one |> Array.ofSeq
    with _ ->
        [||]

/// Every compose-started container on this machine (empty without an engine).
let containers () : Task<(string * ReleaseContainer)[]> =
    task {
        match engine.Value with
        | None -> return [||]
        | Some e ->
            let argv = if e = "docker" then [ e; "ps"; "-a"; "--format"; "{{json .}}" ] else [ e; "ps"; "-a"; "--format"; "json" ]
            match! Proc.run (TimeSpan.FromSeconds 10.) None [] argv with
            | Ok o when o.Code = 0 -> return parseContainers e o.Out
            | _ -> return [||]
    }

/// Units with what's running from each folder.
let withRunning (running: (string * ReleaseContainer)[]) (units: ReleaseUnit[]) =
    units
    |> Array.map (fun u ->
        let dir = Path.GetFullPath(Path.Combine(u.Repo, u.Path)).TrimEnd('/', '\\')
        { u with Running = running |> Array.filter (fun (d, _) -> d = dir) |> Array.map snd |> Array.sortBy _.Service })

// ---- requests and runs ----

/// What the hub asks for, as the job's arg (JSON).
type Request = { Unit: string; Profile: string option; Steps: string[] }

let encode (r: Request) = JsonSerializer.Serialize(r, Config.jsonOptions)

let decode (arg: string) =
    try
        match JsonSerializer.Deserialize<Request>(arg, Config.jsonOptions) with
        | null -> Error "empty release request"
        | r -> Ok r
    with ex ->
        Error $"unreadable release request: {ex.Message}"

/// The unit and its steps in run order, if `units` (one repo's) allow this request.
let check (units: ReleaseUnit[]) (r: Request) : Result<ReleaseUnit * string[], string> =
    match units |> Array.tryFind (fun u -> u.Path = r.Unit) with
    | None -> Error $"no release unit '{r.Unit}' in this repo"
    | Some u ->
        let allowed = if u.Steps |> Array.contains "migrate" then Array.append u.Steps [| "status" |] else u.Steps
        let unknown = r.Steps |> Array.filter (fun s -> not (Array.contains s allowed))
        match r.Profile with
        | _ when r.Steps.Length = 0 -> Error "a release needs at least one step"
        | _ when unknown.Length > 0 -> Error $"""not a step this unit offers: {String.Join(", ", unknown)}"""
        | _ when Array.contains "status" r.Steps && r.Steps.Length > 1 -> Error "status runs on its own"
        | _ when (Array.distinct r.Steps).Length <> r.Steps.Length -> Error "a step is listed twice"
        | Some p when not (Array.contains p u.Profiles) -> Error $"'{p}' isn't one of this unit's profiles"
        | _ ->
            let ordered = if r.Steps = [| "status" |] then r.Steps else stepOrder |> Array.filter (fun s -> Array.contains s r.Steps)
            Ok(u, ordered)

/// The command and working folder for one step; never anything from the request but the profile,
/// which `check` matched against the compose file's own profiles.
let command (engine: string) (windows: bool) (u: ReleaseUnit) (profile: string option) (step: string) : string * string list =
    let unitDir = Path.Combine(u.Repo, u.Path)
    let compose args =
        unitDir, [ engine; "compose"; "-f"; u.Compose ] @ (profile |> Option.map (fun p -> [ "--profile"; p ]) |> Option.defaultValue []) @ args
    let runner action json =
        let r = u.Migrations |> Option.bind _.Runner |> Option.defaultWith (fun () -> failwith "no migration runner")
        let path = Path.Combine(u.Repo, r)
        let dir = Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue unitDir
        if windows then dir, [ "pwsh"; "-NoProfile"; "-File"; path; "-Action"; action ] @ (if json then [ "-Json" ] else [])
        else dir, [ "bash"; path; action ] @ (if json then [ "--json" ] else [])
    match step with
    | "pull" -> compose [ "pull" ]
    | "build" -> compose [ "build" ]
    | "up" -> compose [ "up"; "-d"; "--force-recreate"; "--remove-orphans" ]
    | "migrate" -> runner "apply" false
    | "status" -> runner "status" true
    | s -> failwith $"unknown step {s}"

/// Keeps the end of a long log: what failed is at the bottom.
let tail (max: int) (s: string) =
    if s.Length <= max then s else "…(earlier output trimmed)\n" + s.Substring(s.Length - max)

let private stepTimeout = TimeSpan.FromMinutes 20.
let private busy = Collections.Concurrent.ConcurrentDictionary<string, unit>()

/// Run a checked request: steps in order, stopping at the first failure. One release per unit at a time.
let run (u: ReleaseUnit) (profile: string option) (steps: string[]) : Task<bool * string> =
    task {
        match engine.Value with
        | None when steps |> Array.exists (fun s -> s <> "migrate" && s <> "status") -> return false, "neither podman nor docker is installed here"
        | eng ->
            let key = Path.Combine(u.Repo, u.Path)
            if not (busy.TryAdd(key, ())) then
                return false, $"{u.Path} is already being released"
            else
                try
                    let log = StringBuilder()
                    let mutable ok = true
                    for step in steps do
                        if ok then
                            let dir, argv = command (defaultArg eng "podman") (OperatingSystem.IsWindows()) u profile step
                            if step <> "status" then log.AppendLine($"── {step}: {String.Join(' ', argv)}") |> ignore
                            match! Proc.run stepTimeout (Some dir) [] argv with
                            | Ok o when step = "status" ->
                                ok <- o.Code = 0
                                log.Append(if ok then o.Out else o.Out + o.Err) |> ignore
                            | Ok o ->
                                log.Append(o.Out).Append(o.Err) |> ignore
                                // a pull that fails only for images built here still lets the build and up run
                                if o.Code <> 0 && not (step = "pull" && Array.contains "build" steps) then
                                    ok <- false
                                    log.AppendLine($"── {step} failed (exit {o.Code})") |> ignore
                            | Error e ->
                                ok <- false
                                log.AppendLine($"── {step} failed: {e}") |> ignore
                    return ok, tail 64_000 (log.ToString())
                finally
                    busy.TryRemove key |> ignore
    }
