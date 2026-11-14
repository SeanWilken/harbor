/// Hub persistence. Pushes are ingested one at a time (in-process gate + a transaction-scoped
/// advisory lock) so event ids commit in order and the slice 3 feed cursor never skips a row.
module Harbor.Storage

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open SqlHydra.Query
open SqlHydra.Query.NpgsqlExtensions
open Harbor.Domain
open Harbor.Enrol
open Harbor.HarborDb
open Harbor.HarborDb.``public``

type Sender =
    | LocalAgent // the hub's own in-process agent (mode `both`): trusted, auto-approved
    | Remote of Tailscale.Peer

/// GET /api/machines row; counts come from the latest snapshot, so pending machines have them too.
type MachineSummary =
    { Id: int64
      Name: string
      NodeName: string option
      Login: string option
      Os: string option
      Status: string
      Local: bool
      FirstSeenAt: DateTimeOffset
      LastPushAt: DateTimeOffset option
      ScannedAt: DateTimeOffset option
      PushSeconds: int option
      Repos: int
      Dirty: int
      Unsynced: int
      ServicesUp: int
      ServicesTotal: int
      /// Repo paths hidden in the UI; excluded from the counts above.
      Hidden: string[]
      /// From the latest snapshot's resources; None for older agents or unknown hardware.
      CpuPercent: float option
      GpuPercent: float option
      MemPercent: float option
      IdleSeconds: int64 option
      /// Holds a live hub link right now (the hub's own agent always does).
      Connected: bool
      /// The agent's harbor build and newer known commits (None = up to date or unknown build).
      AgentCommit: string option
      Update: HarborUpdate option
      /// Current role and its work level ("full" | "light" | "none"); None when none are declared.
      Role: string option
      Work: string option
      RoleSince: DateTimeOffset option }

/// GET /api/availability row: what a scheduler should assume about each approved machine now.
/// `available` is the role's work level while the machine is reachable, "none" when it isn't
/// (a stale snapshot is never capacity), "unknown" when it declares no roles.
type Availability =
    { Machine: string
      Role: string option
      Work: string option
      Since: DateTimeOffset option
      Online: bool
      Available: string
      LastPushAt: DateTimeOffset option }

/// GET /api/machines/{id}/jobs row.
type JobInfo =
    { Id: int64
      Kind: string
      Path: string option
      RequestedAt: DateTimeOffset
      RequestedBy: string
      SentAt: DateTimeOffset option
      FinishedAt: DateTimeOffset option
      Ok: bool option
      Output: string option
      Arg: string option }

type Copy =
    { MachineId: int64
      Machine: string
      Path: string
      Branch: string option
      HeadOid: string option
      Ahead: int
      Behind: int
      Dirty: bool
      LastCommitAt: DateTimeOffset option }

/// One remote cloned on two or more approved machines.
type SharedRepo = { RemoteKey: string; SameHead: bool; Copies: Copy[] }

/// GET /api/machines/{id}/manifests row.
type ManifestInfo =
    { Id: int64
      MachineId: int64
      TakenAt: DateTimeOffset
      ReceivedAt: DateTimeOffset
      Hash: string
      Os: string
      PackageCount: int }

type RecapInfo = { Id: int64; PeriodStart: DateTimeOffset; PeriodEnd: DateTimeOffset; CreatedAt: DateTimeOffset }

let private utc (d: DateTimeOffset) = d.UtcDateTime
let private dto (d: DateTime) = DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc))

let private toJson (push: Push) = JsonSerializer.Serialize(push, Config.jsonOptions)

let private manifestOfJson (json: string) : Manifest option =
    try
        match JsonSerializer.Deserialize<Manifest>(json, Config.jsonOptions) with
        | null -> None
        | m -> Some m
    with _ ->
        None

let private manifestInfo (m: manifest) : ManifestInfo =
    { Id = m.id; MachineId = m.machine_id; TakenAt = dto m.taken_at; ReceivedAt = dto m.received_at
      Hash = m.hash; Os = m.os; PackageCount = m.package_count }

let private ofJson (json: string) : Push option =
    try
        JsonSerializer.Deserialize<Push>(json, Config.jsonOptions) |> Option.ofObj
    with _ ->
        None

let private isDirty (c: Changes option) =
    c |> Option.exists (fun c -> c.Staged + c.Unstaged + c.Untracked + c.Conflicted > 0)

let private remoteKeyOf (r: Repo) =
    GitParse.primaryRemote r.Remotes |> Option.bind (fun rm -> GitParse.remoteKey rm.Url)

let private repoRow (machineId: int64) (r: Repo) : repo_write =
    let b = r.Branch
    let c = r.Changes
    let n f = c |> Option.map f |> Option.defaultValue 0
    { machine_id = machineId
      path = r.Path
      name = r.Name
      remote_key = remoteKeyOf r
      branch = b |> Option.bind _.Head
      head_oid = b |> Option.bind _.Oid
      upstream = b |> Option.bind _.Upstream
      ahead = b |> Option.map _.Ahead |> Option.defaultValue 0
      behind = b |> Option.map _.Behind |> Option.defaultValue 0
      staged = n _.Staged
      unstaged = n _.Unstaged
      untracked = n _.Untracked
      conflicted = n _.Conflicted
      last_commit_at = r.LastCommit |> Option.map (_.Date >> utc)
      last_modified_at = r.LastModified |> Option.map utc
      error = r.Error }

let private serviceRow (machineId: int64) (s: ServiceStatus) : service =
    { machine_id = machineId
      name = s.Name
      check_kind = s.Check
      state = (match s.State with Running -> "running" | Stopped -> "stopped" | Unknown -> "unknown")
      detail = s.Detail }

type RowChanges<'row> =
    /// No previous state to compare with: rewrite every row.
    | All of 'row[]
    /// Keys that disappeared, and rows that are new or differ.
    | Changed of gone: string list * rows: 'row list

/// Which rows a push actually changes, by key and full-row equality.
let rowChanges (prev: 'row[] option) (next: 'row[]) (key: 'row -> string) : RowChanges<'row> =
    match prev with
    | None -> All next
    | Some prev ->
        let before = prev |> Array.map (fun r -> key r, r) |> Map.ofArray
        let afterKeys = next |> Array.map key |> Set.ofArray
        let gone = before |> Map.keys |> Seq.filter (fun k -> not (afterKeys.Contains k)) |> List.ofSeq
        let rows = next |> Array.filter (fun r -> before.TryFind(key r) <> Some r) |> List.ofArray
        Changed(gone, rows)

let private eventRow machineId (occurred: DateTime) (kind: string) repoPath subject (data: string) : event_write =
    { machine_id = machineId
      kind = kind
      occurred_at = occurred
      recorded_at = DateTime.UtcNow
      repo_path = repoPath
      subject = subject
      data = data
      actor = None }

/// An admin action, attributed.
let private auditRow machineId (actor: string) (kind: string) repoPath subject : event_write =
    { eventRow machineId DateTime.UtcNow kind repoPath subject "{}" with actor = Some actor }

/// A job is handed to the agent if it was never sent, or was sent this long ago with no result
/// (agent restarted, reply lost). The agent ignores ids it has already started.
let resendAfter = TimeSpan.FromMinutes 5.

let isDue (now: DateTime) (j: job) =
    j.finished_at.IsNone && j.sent_at |> Option.forall (fun sent -> now - sent > resendAfter)

let private maxOutput = 8000

let private jobInfo (j: job) : JobInfo =
    { Id = j.id
      Kind = j.kind
      Path = j.path
      RequestedAt = dto j.requested_at
      RequestedBy = j.requested_by
      SentAt = j.sent_at |> Option.map dto
      FinishedAt = j.finished_at |> Option.map dto
      Ok = j.ok
      Output = j.output
      Arg = j.arg }

let summarize (m: machine) (payload: Push option) (hidden: string[]) : MachineSummary =
    let snap = payload |> Option.map _.Snapshot
    let repos =
        snap
        |> Option.map _.Repos
        |> Option.defaultValue [||]
        |> Array.filter (fun r -> not (Array.contains r.Path hidden))
    let services = snap |> Option.map _.Services |> Option.defaultValue [||]
    let res = snap |> Option.bind _.Resources
    { Id = m.id
      Name = m.name
      NodeName = m.node_name
      Login = m.login
      Os = m.os
      Status = m.status
      Local = (m.node_id = "local")
      FirstSeenAt = dto m.first_seen_at
      LastPushAt = m.last_push_at |> Option.map dto
      ScannedAt = snap |> Option.map _.Machine.ScannedAt
      PushSeconds = m.push_seconds
      Repos = repos.Length
      Dirty = repos |> Array.filter (fun r -> isDirty r.Changes) |> Array.length
      Unsynced = repos |> Array.filter (fun r -> r.Branch |> Option.exists (fun b -> b.Ahead + b.Behind > 0)) |> Array.length
      ServicesUp = services |> Array.filter (fun s -> s.State = Running) |> Array.length
      ServicesTotal = services.Length
      Hidden = hidden
      CpuPercent = res |> Option.bind _.CpuPercent
      GpuPercent = res |> Option.bind (fun r -> r.Gpus |> Array.tryHead |> Option.map _.Percent)
      MemPercent =
        res
        |> Option.bind (fun r ->
            match r.MemTotalBytes, r.MemAvailableBytes with
            | Some t, Some a when t > 0L -> Some(Math.Round(100. * float (t - a) / float t, 1))
            | _ -> None)
      IdleSeconds = res |> Option.bind _.IdleSeconds
      Connected = false
      AgentCommit = payload |> Option.bind _.AgentCommit
      Update = None
      Role = snap |> Option.bind _.Role |> Option.map _.Name
      Work = snap |> Option.bind _.Role |> Option.map _.Work
      RoleSince = snap |> Option.bind _.Role |> Option.map _.Since }

/// Online: linked to the hub now, or pushed within three push intervals.
let availability (now: DateTimeOffset) (ms: MachineSummary[]) : Availability[] =
    ms
    |> Array.filter (fun m -> m.Status = "approved")
    |> Array.map (fun m ->
        let window = TimeSpan.FromSeconds(float (3 * (defaultArg m.PushSeconds 300)))
        let online = m.Connected || m.LastPushAt |> Option.exists (fun t -> now - t < window)
        { Machine = m.Name
          Role = m.Role
          Work = m.Work
          Since = m.RoleSince
          Online = online
          Available =
            match m.Work with
            | _ when not online -> "none"
            | Some w -> w
            | None -> "unknown"
          LastPushAt = m.LastPushAt })

/// Group approved machines' clones by remote key; only remotes on 2+ machines are "shared".
let shareGroups (rows: (repo * string) seq) : SharedRepo[] =
    rows
    |> Seq.choose (fun (r, machineName) -> r.remote_key |> Option.map (fun k -> k, (r, machineName)))
    |> Seq.groupBy fst
    |> Seq.map (fun (key, xs) ->
        let copies =
            xs
            |> Seq.map (fun (_, (r, name)) ->
                { MachineId = r.machine_id
                  Machine = name
                  Path = r.path
                  Branch = r.branch
                  HeadOid = r.head_oid
                  Ahead = r.ahead
                  Behind = r.behind
                  Dirty = r.dirty |> Option.defaultValue false
                  LastCommitAt = r.last_commit_at |> Option.map dto })
            |> Seq.sortBy (fun c -> c.Machine, c.Path)
            |> Seq.toArray
        { RemoteKey = key
          SameHead = (copies |> Array.map _.HeadOid |> Array.distinct |> Array.length) = 1
          Copies = copies })
    |> Seq.filter (fun g -> (g.Copies |> Array.map _.MachineId |> Array.distinct |> Array.length) > 1)
    |> Seq.sortBy _.RemoteKey
    |> Seq.toArray

type Db(connectionString: string) =
    let factory = QueryContextFactory.Create connectionString
    let gate = new SemaphoreSlim(1, 1)
    let pairingLimiter = Pairing.Limiter(5, TimeSpan.FromMinutes 10.)
    let jobQueued = Event<string * string>() // (node id, machine name): wake that agent

    let lockIngest (ctx: QueryContext) =
        task {
            use cmd = ctx.Connection.CreateCommand()
            cmd.Transaction <- ctx.Transaction |> Option.toObj
            cmd.CommandText <- "select pg_advisory_xact_lock(7420)"
            let! _ = cmd.ExecuteScalarAsync()
            ()
        }

    let latestPayload (ctx: QueryContext) (machineId: int64) =
        task {
            let! row =
                selectTask ctx {
                    for s in snapshot_latest do
                    where (s.machine_id = machineId)
                    select s.payload
                    tryHead
                }
            return row |> Option.bind ofJson
        }

    let saveSnapshot (ctx: QueryContext) (machineId: int64) (push: Push) =
        task {
            let! _ =
                insertTask ctx {
                    for s in snapshot_latest do
                    entity
                        { machine_id = machineId
                          scanned_at = utc push.Snapshot.Machine.ScannedAt
                          received_at = DateTime.UtcNow
                          payload = toJson { push with Manifest = None } } // manifests have their own table
                    onConflictDoUpdate s.machine_id (s.scanned_at, s.received_at, s.payload)
                }
            ()
        }

    let insertEvents (ctx: QueryContext) (rows: event_write list) =
        task {
            if not rows.IsEmpty then
                let! _ = insertTask ctx { into event; writeEntities rows }
                ()
        }

    /// New commits only; the same sha seen in another clone (or machine) of the remote is skipped.
    let recordCommits (ctx: QueryContext) (machineId: int64) (push: Push) =
        task {
            let keyOf =
                push.Snapshot.Repos
                |> Array.map (fun r -> r.Path, remoteKeyOf r |> Option.defaultValue $"local:{machineId}:{r.Path}")
                |> Map.ofArray
            let incoming =
                [ for rc in push.Commits do
                      match keyOf.TryFind rc.Path with
                      | Some key -> for c in rc.Commits -> key, rc.Path, c
                      | None -> () ]
                |> List.distinctBy (fun (key, _, c) -> key, c.Sha)
            if not incoming.IsEmpty then
                let keys = incoming |> List.map (fun (k, _, _) -> k) |> List.distinct
                let shas = incoming |> List.map (fun (_, _, c) -> c.Sha) |> List.distinct
                let! known =
                    selectTask ctx {
                        for c in git_commit do
                        where (c.repo_key |=| keys && c.sha |=| shas)
                        select (c.repo_key, c.sha)
                    }
                let known = Set.ofSeq known
                let fresh = incoming |> List.filter (fun (k, _, c) -> not (known.Contains(k, c.Sha)))
                if not fresh.IsEmpty then
                    let now = DateTime.UtcNow
                    let! _ =
                        insertTask ctx {
                            into git_commit
                            entities (
                                fresh
                                |> List.map (fun (k, _, c) ->
                                    { repo_key = k
                                      sha = c.Sha
                                      authored_at = utc c.Date
                                      author = c.Author
                                      subject = c.Subject
                                      first_seen_machine = machineId
                                      first_seen_at = now })
                            )
                        }
                    do!
                        fresh
                        |> List.map (fun (_, path, c) ->
                            eventRow machineId (utc c.Date) "commit_seen" (Some path) (Some c.Sha)
                                (JsonSerializer.Serialize {| author = c.Author; subject = c.Subject |}))
                        |> insertEvents ctx
        }

    /// Rows, commits and events for an approved machine. `prev` None = baseline (no diff events).
    let storeApproved (ctx: QueryContext) (machineId: int64) (prev: Push option) (push: Push) =
        task {
            let snap = push.Snapshot
            // Only rows that changed since the previous push are rewritten; most pushes touch none.
            // Without a previous push (first store, just approved) everything is rewritten.
            let repoChanges =
                rowChanges (prev |> Option.map (fun p -> p.Snapshot.Repos |> Array.map (repoRow machineId)))
                           (snap.Repos |> Array.map (repoRow machineId)) _.path
            match repoChanges with
            | All rows ->
                let! _ = deleteTask ctx { for r in repo do where (r.machine_id = machineId) }
                if rows.Length > 0 then
                    let! _ = insertTask ctx { into repo; writeEntities rows }
                    ()
            | Changed(gone, rows) ->
                let stale = gone @ (rows |> List.map _.path)
                if not stale.IsEmpty then
                    let! _ = deleteTask ctx { for r in repo do where (r.machine_id = machineId && isIn r.path stale) }
                    ()
                if not rows.IsEmpty then
                    let! _ = insertTask ctx { into repo; writeEntities rows }
                    ()
            let serviceChanges =
                rowChanges (prev |> Option.map (fun p -> p.Snapshot.Services |> Array.map (serviceRow machineId)))
                           (snap.Services |> Array.map (serviceRow machineId)) _.name
            match serviceChanges with
            | All rows ->
                let! _ = deleteTask ctx { for x in service do where (x.machine_id = machineId) }
                if rows.Length > 0 then
                    let! _ = insertTask ctx { into service; entities rows }
                    ()
            | Changed(gone, rows) ->
                let stale = gone @ (rows |> List.map _.name)
                if not stale.IsEmpty then
                    let! _ = deleteTask ctx { for x in service do where (x.machine_id = machineId && isIn x.name stale) }
                    ()
                if not rows.IsEmpty then
                    let! _ = insertTask ctx { into service; entities rows }
                    ()
            do! recordCommits ctx machineId push
            let scanned = utc snap.Machine.ScannedAt
            do!
                Events.diff (prev |> Option.map _.Snapshot) snap
                |> List.map (fun d -> eventRow machineId scanned d.Kind d.RepoPath d.Subject d.Data)
                |> insertEvents ctx
        }

    let recordJobResults (ctx: QueryContext) (machineId: int64) (results: JobResult[]) =
        task {
            for r in results do
                let output = if r.Output.Length > maxOutput then r.Output[.. maxOutput - 1] + "\n…" else r.Output
                let! n =
                    updateTask ctx {
                        for j in job do
                        set j.finished_at (Some(utc r.FinishedAt))
                        set j.ok (Some r.Ok)
                        set j.output (Some output)
                        where (j.id = r.Id && j.machine_id = machineId && j.finished_at = None)
                    }
                if n > 0 then
                    let! row = selectTask ctx { for j in job do where (j.id = r.Id); tryHead }
                    do!
                        insertEvents ctx
                            [ eventRow machineId (utc r.FinishedAt) "job_finished" (row |> Option.bind _.path) (Some(string r.Id))
                                  (JsonSerializer.Serialize {| kind = row |> Option.map _.kind; ok = r.Ok |}) ]
        }

    /// Open jobs due for (re)sending; marks them sent.
    let dueJobs (ctx: QueryContext) (machineId: int64) : Task<JobRequest[]> =
        task {
            let now = DateTime.UtcNow
            let! open' = selectTask ctx { for j in job do where (j.machine_id = machineId && j.finished_at = None); orderBy j.id }
            let due = open' |> Seq.filter (isDue now) |> Seq.toArray
            for j in due do
                let! _ = updateTask ctx { for x in job do set x.sent_at (Some now); where (x.id = j.id) }
                ()
            return due |> Array.map (fun j -> { Id = j.id; Kind = j.kind; Path = j.path; Arg = j.arg })
        }

    let hiddenPaths (ctx: QueryContext) =
        task {
            let! rows = selectTask ctx { for h in repo_hidden do select (h.machine_id, h.path) }
            return rows |> Seq.groupBy fst |> Seq.map (fun (id, xs) -> id, xs |> Seq.map snd |> Seq.toArray) |> Map.ofSeq
        }

    let serialized (work: QueryContext -> Task<'a>) : Task<'a> =
        task {
            do! gate.WaitAsync()
            try
                use! ctx = factory.OpenContextAsync()
                ctx.BeginTransaction()
                do! lockIngest ctx
                let! result = work ctx
                ctx.CommitTransaction()
                return result
            finally
                gate.Release() |> ignore
        }

    let latestManifest (ctx: QueryContext) (machineId: int64) =
        selectTask ctx { for x in manifest do where (x.machine_id = machineId); orderByDescending x.id; tryHead }

    /// Keep it if it differs from the newest one; returns the hash the hub now holds.
    let storeManifest (ctx: QueryContext) (machineId: int64) (pushed: Manifest option) : Task<string option> =
        task {
            let! last = latestManifest ctx machineId
            match pushed, last with
            | None, _ -> return last |> Option.map _.hash
            | Some m, Some l when l.hash = m.Hash -> return Some l.hash
            | Some m, _ ->
                let! _ =
                    insertTask ctx {
                        for x in manifest do
                        writeEntity
                            { machine_id = machineId
                              taken_at = utc m.TakenAt
                              received_at = DateTime.UtcNow
                              hash = m.Hash
                              os = m.Os.PrettyName
                              package_count = m.Sets |> Array.sumBy _.Packages.Length
                              payload = JsonSerializer.Serialize(m, Config.jsonOptions) }
                    }
                match last |> Option.bind (fun l -> manifestOfJson l.payload) with
                | Some prev ->
                    let changes = Manifest.diff prev m
                    let count f = changes |> Array.filter f |> Array.length
                    let data =
                        JsonSerializer.Serialize
                            {| installed = count (fun c -> c.Before.IsNone)
                               removed = count (fun c -> c.After.IsNone)
                               changed = count (fun c -> c.Before.IsSome && c.After.IsSome)
                               os = (if prev.Os <> m.Os then Some m.Os.PrettyName else None) |}
                    do! insertEvents ctx [ eventRow machineId (utc m.TakenAt) "manifest_changed" None (Some m.Hash) data ]
                | None -> () // first manifest: a baseline, not a change
                return Some m.Hash
        }

    /// Commits of the same repository newer than `sha`, from the commits agents have reported
    /// (local, possibly unpushed ones included). None when the build commit isn't known.
    let harborUpdate (ctx: QueryContext) (sha: string option) : Task<HarborUpdate option> =
        task {
            match sha with
            | None -> return None
            | Some sha ->
                use cmd = ctx.Connection.CreateCommand()
                cmd.Transaction <- ctx.Transaction |> Option.toObj
                cmd.CommandText <-
                    """with b as (select repo_key, authored_at from git_commit where sha = $1 limit 1)
                       select count(*), (array_agg(c.sha order by c.authored_at desc))[1], max(c.authored_at)
                       from git_commit c join b on c.repo_key = b.repo_key where c.authored_at > b.authored_at"""
                cmd.Parameters.Add(Npgsql.NpgsqlParameter(Value = sha)) |> ignore
                use! r = cmd.ExecuteReaderAsync()
                let! row = r.ReadAsync()
                if row && r.GetInt64 0 > 0L then
                    return Some { Behind = int (r.GetInt64 0); Latest = r.GetString 1; LatestAt = dto (r.GetDateTime 2) }
                else
                    return None
        }

    /// A valid, unused, unexpired code: mark it used and return (code id, who created it).
    let redeem (ctx: QueryContext) (nodeId: string) (code: string) : Task<(int64 * string) option> =
        task {
            let now = DateTimeOffset.UtcNow
            if pairingLimiter.Blocked(nodeId, now) || not (Pairing.isWellFormed (Pairing.normalize code)) then
                pairingLimiter.Fail(nodeId, now)
                return None
            else
                let h = Pairing.hash code
                let nowUtc = now.UtcDateTime
                let! row =
                    selectTask ctx {
                        for c in pairing_code do
                        where (c.code_hash = h && c.used_at = None && c.expires_at > nowUtc)
                        tryHead
                    }
                match row with
                | None ->
                    pairingLimiter.Fail(nodeId, now)
                    return None
                | Some c ->
                    pairingLimiter.Clear nodeId
                    let! _ = updateTask ctx { for x in pairing_code do set x.used_at (Some nowUtc); where (x.id = c.id) }
                    return Some(c.id, c.created_by)
        }

    let attachCode (ctx: QueryContext) (codeId: int64) (machineId: int64) =
        task {
            let! _ = updateTask ctx { for x in pairing_code do set x.machine_id (Some machineId); where (x.id = codeId) }
            ()
        }

    member _.Ingest(sender: Sender, push: Push) : Task<Result<PushReply, string>> =
        serialized (fun ctx ->
            task {
                let nodeId, peer =
                    match sender with
                    | LocalAgent -> "local", None
                    | Remote p -> p.NodeId, Some p
                let! existing =
                    selectTask ctx {
                        for m in machine do
                        where (m.node_id = nodeId && m.name = push.Machine)
                        tryHead
                    }
                // A pairing code approves a new or pending machine on the spot (never a rejected one).
                let! paired =
                    match push.PairCode, existing with
                    | Some code, None
                    | Some code, Some { status = "pending" } when nodeId <> "local" -> redeem ctx nodeId code
                    | _ -> Task.FromResult None
                let outcome =
                    match sender, paired with
                    | LocalAgent, _ -> PushOutcome.Store
                    | Remote _, Some _ -> PushOutcome.Store
                    | Remote p, None -> Enrol.push (Peer p) (existing |> Option.map (fun m -> Status.ofDb m.status))
                let now = DateTime.UtcNow
                let pairResult =
                    match push.PairCode, paired with
                    | None, _ -> None
                    | Some _, Some _ -> Some "accepted"
                    | Some _, None -> Some "rejected"
                match outcome, existing with
                | Refuse reason, _ -> return Error reason
                | _, Some m0 ->
                    let m =
                        match paired with
                        | Some _ -> { m0 with status = "approved"; approved_at = Some now }
                        | None -> m0
                    let! prev = if paired.IsSome then Task.FromResult None else latestPayload ctx m.id // paired: baseline, like approval
                    let! _ =
                        updateTask ctx {
                            for x in machine do
                            set x.node_name (peer |> Option.map _.NodeName |> Option.orElse m.node_name)
                            set x.login (peer |> Option.map _.Login |> Option.orElse m.login)
                            set x.os (Some push.Snapshot.Machine.Os)
                            set x.push_seconds (Some push.PushSeconds)
                            set x.last_push_at (Some now)
                            where (x.id = m.id)
                        }
                    match paired with
                    | Some(codeId, by) ->
                        let! _ =
                            updateTask ctx {
                                for x in machine do
                                set x.status "approved"
                                set x.approved_at (Some now)
                                where (x.id = m.id)
                            }
                        do! insertEvents ctx [ { auditRow m.id by "machine_approved" None (Some "pairing code") with data = """{"via":"pairing code"}""" } ]
                        do! attachCode ctx codeId m.id
                    | None -> ()
                    do! saveSnapshot ctx m.id push
                    let mutable jobs = None
                    let mutable held = None
                    if outcome = PushOutcome.Store then
                        do! storeApproved ctx m.id prev push
                        do! recordJobResults ctx m.id (push.JobResults |> Option.defaultValue [||])
                        let! due = dueJobs ctx m.id
                        jobs <- if due.Length = 0 then None else Some due
                        let! h = storeManifest ctx m.id push.Manifest
                        held <- h
                    let message =
                        match paired, pairResult with
                        | Some(_, by), _ -> Some $"paired (code from {by})"
                        | None, Some _ -> Some "pairing code not accepted (wrong, used or expired)"
                        | None, None -> None
                    let! update = harborUpdate ctx push.AgentCommit
                    return Ok { Status = m.status; Message = message; Jobs = jobs; ManifestHash = held; PairResult = pairResult; Update = update }
                | _, None ->
                    let status = if sender = LocalAgent || paired.IsSome then Approved else Pending
                    let! id =
                        insertTask ctx {
                            for x in machine do
                            writeEntity
                                { node_id = nodeId
                                  name = push.Machine
                                  node_name = peer |> Option.map _.NodeName
                                  login = peer |> Option.map _.Login
                                  os = Some push.Snapshot.Machine.Os
                                  status = Status.toDb status
                                  push_seconds = Some push.PushSeconds
                                  first_seen_at = now
                                  last_push_at = Some now
                                  approved_at = (if status = Approved then Some now else None) }
                            getId x.id
                        }
                    do! insertEvents ctx [ eventRow id now "machine_enrolled" None None (JsonSerializer.Serialize {| node = peer |> Option.map _.NodeName |}) ]
                    match paired with
                    | Some(codeId, by) ->
                        do! insertEvents ctx [ { auditRow id by "machine_approved" None (Some "pairing code") with data = """{"via":"pairing code"}""" } ]
                        do! attachCode ctx codeId id
                    | None -> ()
                    do! saveSnapshot ctx id push
                    let mutable held = None
                    if status = Approved then
                        do! storeApproved ctx id None push
                        let! h = storeManifest ctx id push.Manifest
                        held <- h
                    return
                        Ok
                            { Status = Status.toDb status
                              Message =
                                match paired, status with
                                | Some(_, by), _ -> Some $"paired (code from {by})"
                                | None, Pending ->
                                    Some(if push.PairCode.IsSome then "pairing code not accepted (wrong, used or expired); waiting for approval" else "waiting for approval on the hub")
                                | _ -> None
                              Jobs = None
                              ManifestHash = held
                              PairResult = pairResult
                              Update = None }
            })

    /// Approve (then process the held snapshot as a baseline) or reject. False if no such machine.
    member _.SetStatus(machineId: int64, status: Status, actor: string) : Task<bool> =
        serialized (fun ctx ->
            task {
                let! existing = selectTask ctx { for m in machine do where (m.id = machineId); tryHead }
                match existing with
                | None -> return false
                | Some m ->
                    let now = DateTime.UtcNow
                    let! _ =
                        updateTask ctx {
                            for x in machine do
                            set x.status (Status.toDb status)
                            set x.approved_at (if status = Approved then Some now else m.approved_at)
                            where (x.id = machineId)
                        }
                    match status with
                    | Approved when m.status <> "approved" ->
                        do! insertEvents ctx [ auditRow machineId actor "machine_approved" None None ]
                        match! latestPayload ctx machineId with
                        | Some held -> do! storeApproved ctx machineId None held
                        | None -> ()
                    | Rejected ->
                        if m.status <> "rejected" then do! insertEvents ctx [ auditRow machineId actor "machine_rejected" None None ]
                        let! _ = deleteTask ctx { for r in repo do where (r.machine_id = machineId) }
                        let! _ = deleteTask ctx { for s in service do where (s.machine_id = machineId) }
                        ()
                    | _ -> ()
                    return true
            })

    member _.Machines(connected: string * string -> bool) : Task<MachineSummary[]> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! machines = selectTask ctx { for m in machine do orderBy m.name; thenBy m.id }
            let! payloads = selectTask ctx { for s in snapshot_latest do select (s.machine_id, s.payload) }
            let byId = payloads |> Seq.map (fun (id, p) -> id, p) |> Map.ofSeq
            let! hidden = hiddenPaths ctx
            let summaries =
                machines
                |> Seq.map (fun m ->
                    { summarize m (byId.TryFind m.id |> Option.bind ofJson) (hidden.TryFind m.id |> Option.defaultValue [||]) with
                        Connected = m.node_id = "local" || connected (m.node_id, m.name) })
                |> Seq.toArray
            for i in 0 .. summaries.Length - 1 do
                let! u = harborUpdate ctx summaries[i].AgentCommit
                summaries[i] <- { summaries[i] with Update = u }
            return summaries
        }

    member _.Snapshot(machineId: int64) : Task<Snapshot option> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! p = latestPayload ctx machineId
            return p |> Option.map _.Snapshot
        }

    member _.Shared() : Task<SharedRepo[]> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! rows =
                selectTask ctx {
                    for r in repo do
                    join m in machine on (r.machine_id = m.id)
                    where (m.status = "approved")
                    select (r, m.name)
                }
            let! hidden = hiddenPaths ctx
            let visible (r: repo, _) =
                not (hidden.TryFind r.machine_id |> Option.exists (Array.contains r.path))
            return shareGroups (rows |> Seq.filter visible)
        }

    /// Queue a job for an approved machine; an identical open job is reused rather than duplicated.
    member _.RequestJob(machineId: int64, kind: string, path: string option, arg: string option, requestedBy: string) : Task<Result<int64, string>> =
        serialized (fun ctx ->
            task {
                let! m = selectTask ctx { for x in machine do where (x.id = machineId); tryHead }
                match m with
                | None -> return Error "no such machine"
                | Some m when m.status <> "approved" -> return Error $"machine is {m.status}"
                | Some m ->
                    let! open' = selectTask ctx { for j in job do where (j.machine_id = machineId && j.finished_at = None) }
                    // the newest role request wins: an older one still waiting (machine offline, or
                    // resent after a lost result) must not land after it
                    if kind = "role" then
                        let note = "superseded by a switch to " + defaultArg arg "?"
                        for j in open' |> Seq.filter (fun j -> j.kind = "role" && j.arg <> arg) do
                            let! _ =
                                updateTask ctx {
                                    for x in job do
                                    set x.finished_at (Some DateTime.UtcNow)
                                    set x.ok (Some false)
                                    set x.output (Some note)
                                    where (x.id = j.id)
                                }
                            ()
                    match open' |> Seq.tryFind (fun j -> j.kind = kind && j.path = path && j.arg = arg) with
                    | Some j -> return Ok j.id
                    | None ->
                        let! id =
                            insertTask ctx {
                                for j in job do
                                writeEntity
                                    { machine_id = machineId
                                      kind = kind
                                      path = path
                                      requested_at = DateTime.UtcNow
                                      requested_by = requestedBy
                                      sent_at = None
                                      finished_at = None
                                      ok = None
                                      output = None
                                      arg = arg }
                                getId j.id
                            }
                        do! insertEvents ctx [ auditRow machineId requestedBy "job_requested" path (Some $"{kind} #{id}") ]
                        jobQueued.Trigger(m.node_id, m.name)
                        return Ok id
            })

    member _.Jobs(machineId: int64) : Task<JobInfo[]> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! rows = selectTask ctx { for j in job do where (j.machine_id = machineId); orderByDescending j.id; take 20 }
            return rows |> Seq.map jobInfo |> Seq.toArray
        }

    member _.SetHidden(machineId: int64, path: string, hidden: bool, actor: string) : Task<unit> =
        serialized (fun ctx ->
            task {
                do! insertEvents ctx [ auditRow machineId actor (if hidden then "repo_hidden" else "repo_unhidden") (Some path) None ]
                if hidden then
                    let! _ =
                        insertTask ctx {
                            for h in repo_hidden do
                            entity { machine_id = machineId; path = path; hidden_at = DateTime.UtcNow }
                            onConflictDoNothing (h.machine_id, h.path)
                        }
                    ()
                else
                    let! _ = deleteTask ctx { for h in repo_hidden do where (h.machine_id = machineId && h.path = path) }
                    ()
            })

    member _.JobQueued = jobQueued.Publish

    /// A fresh one-time code (shown once; only its hash is kept).
    member _.CreatePairingCode(createdBy: string) : Task<string * DateTimeOffset> =
        task {
            use! ctx = factory.OpenContextAsync()
            let code = Pairing.generate ()
            let expires = DateTimeOffset.UtcNow + Pairing.lifetime
            let! _ =
                insertTask ctx {
                    for x in pairing_code do
                    writeEntity
                        { code_hash = Pairing.hash code
                          created_by = createdBy
                          created_at = DateTime.UtcNow
                          expires_at = expires.UtcDateTime
                          used_at = None
                          machine_id = None }
                }
            return code, expires
        }

    /// Has the code with this hash been used, and by which machine? (For the "Pair a device" window.)
    member _.PairingState(code: string) : Task<(DateTimeOffset * string option) option> =
        task {
            use! ctx = factory.OpenContextAsync()
            let h = Pairing.hash code
            let! row = selectTask ctx { for c in pairing_code do where (c.code_hash = h); tryHead }
            match row with
            | None -> return None
            | Some c ->
                let! name =
                    match c.machine_id with
                    | Some id -> selectTask ctx { for m in machine do where (m.id = id); select m.name; tryHead }
                    | None -> Task.FromResult None
                return Some(dto c.expires_at, name)
        }

    member _.Manifests(machineId: int64) : Task<ManifestInfo[]> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! rows = selectTask ctx { for x in manifest do where (x.machine_id = machineId); orderByDescending x.id; take 100 }
            return rows |> Seq.map manifestInfo |> Seq.toArray
        }

    member _.Manifest(id: int64) : Task<(ManifestInfo * Manifest) option> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! row = selectTask ctx { for x in manifest do where (x.id = id); tryHead }
            return row |> Option.bind (fun r -> manifestOfJson r.payload |> Option.map (fun m -> manifestInfo r, m))
        }

    /// Everything a recap needs, for approved machines: their latest snapshot repos (hidden ones
    /// left out), when each repo last became behind or dirty, and their manifests now and at `start`.
    member _.RecapInputs(start: DateTimeOffset) : Task<Recap.RepoState[] * Recap.MachineManifests[]> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! machines = selectTask ctx { for m in machine do where (m.status = "approved"); orderBy m.name }
            let! hidden = hiddenPaths ctx
            use cmd = ctx.Connection.CreateCommand()
            cmd.CommandText <-
                """select machine_id, repo_path, kind, max(occurred_at) from event
                   where repo_path is not null and (
                         (kind = 'ahead_behind' and (data->>'behind')::int > 0 and (data->>'prevBehind')::int = 0)
                      or kind = 'dirty')
                   group by machine_id, repo_path, kind"""
            let since = Collections.Generic.Dictionary<struct (int64 * string * string), DateTimeOffset>()
            use! reader = cmd.ExecuteReaderAsync()
            while! reader.ReadAsync() do
                since[struct (reader.GetInt64 0, reader.GetString 1, reader.GetString 2)] <- dto (reader.GetDateTime 3)
            do! reader.CloseAsync()
            let found k = match since.TryGetValue k with | true, t -> Some t | _ -> None
            let repos = ResizeArray()
            let manifests = ResizeArray()
            for m in machines do
                let! p = latestPayload ctx m.id
                let hid = hidden.TryFind m.id |> Option.defaultValue [||]
                for r in p |> Option.map _.Snapshot.Repos |> Option.defaultValue [||] do
                    if not (Array.contains r.Path hid) then
                        let state: Recap.RepoState =
                            { Machine = m.name
                              Repo = r
                              BehindSince = found (struct (m.id, r.Path, "ahead_behind"))
                              DirtySince = found (struct (m.id, r.Path, "dirty")) }
                        repos.Add state
                let! latest = latestManifest ctx m.id
                let startUtc = utc start
                let! atStart =
                    selectTask ctx {
                        for x in manifest do
                        where (x.machine_id = m.id && x.taken_at <= startUtc)
                        orderByDescending x.id
                        tryHead
                    }
                let mm: Recap.MachineManifests =
                    { Machine = m.name
                      Latest = latest |> Option.bind (fun l -> manifestOfJson l.payload)
                      AtStart = atStart |> Option.bind (fun l -> manifestOfJson l.payload) }
                manifests.Add mm
            return repos.ToArray(), manifests.ToArray()
        }

    /// Store a scheduled recap; false when that week already has one.
    member _.SaveRecap(r: Recap.Report, markdown: string) : Task<bool> =
        task {
            use! ctx = factory.OpenContextAsync()
            try
                let! _ =
                    insertTask ctx {
                        for x in recap do
                        writeEntity
                            { period_start = utc r.PeriodStart
                              period_end = utc r.PeriodEnd
                              created_at = DateTime.UtcNow
                              payload = JsonSerializer.Serialize(r, Config.jsonOptions)
                              markdown = markdown }
                    }
                return true
            with :? Npgsql.PostgresException as e when e.SqlState = "23505" -> // that week exists
                return false
        }

    member _.HasRecap(periodEnd: DateTimeOffset) : Task<bool> =
        task {
            use! ctx = factory.OpenContextAsync()
            let e = utc periodEnd
            let! row = selectTask ctx { for x in recap do where (x.period_end = e); select x.id; tryHead }
            return row.IsSome
        }

    member _.Recaps() : Task<RecapInfo[]> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! rows = selectTask ctx { for x in recap do orderByDescending x.period_end; take 52 }
            return
                rows
                |> Seq.map (fun x -> { Id = x.id; PeriodStart = dto x.period_start; PeriodEnd = dto x.period_end; CreatedAt = dto x.created_at })
                |> Seq.toArray
        }

    member _.Recap(id: int64) : Task<(string * string) option> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! row = selectTask ctx { for x in recap do where (x.id = id); tryHead }
            return row |> Option.map (fun x -> x.payload, x.markdown)
        }

    /// Retention, run daily: keeps the database to what recaps and the UI use. Admin actions
    /// (events with an actor) are kept indefinitely. Returns rows removed or cleared per table.
    member _.Prune() : Task<(string * int) list> =
        serialized (fun ctx ->
            task {
                let run (label: string) (sql: string) =
                    task {
                        use cmd = ctx.Connection.CreateCommand()
                        cmd.Transaction <- ctx.Transaction |> Option.toObj
                        cmd.CommandText <- sql
                        let! n = cmd.ExecuteNonQueryAsync()
                        return label, n
                    }
                let! events = run "events" "delete from event where actor is null and occurred_at < now() - interval '90 days'"
                let! manifests =
                    run "manifests"
                        """delete from manifest m where m.taken_at < now() - interval '90 days'
                           and m.id <> (select max(id) from manifest l where l.machine_id = m.machine_id)"""
                let! jobs = run "job output" "update job set output = null where output is not null and finished_at < now() - interval '30 days'"
                // diffs are code: keep them only long enough to read
                let! diffs = run "diff output" "update job set output = null where kind = 'diff' and output is not null and finished_at < now() - interval '1 day'"
                let! recaps = run "recaps" "delete from recap where id not in (select id from recap order by period_end desc limit 52)"
                let! codes = run "pairing codes" "delete from pairing_code where used_at is null and expires_at < now() - interval '1 day'"
                let! checks = run "site checks" "delete from site_check where checked_at < now() - interval '30 days'"
                return [ events; manifests; jobs; diffs; recaps; codes; checks ] |> List.filter (fun (_, n) -> n > 0)
            })

    member _.SaveCheck(c: Sites.Check) : Task<unit> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! _ =
                insertTask ctx {
                    for x in site_check do
                    writeEntity
                        { site = c.Site
                          checked_at = utc c.At
                          ok = c.Ok
                          status = c.Status
                          ms = c.Ms
                          error = c.Error
                          cert_expires = c.CertExpires |> Option.map utc
                          cert_issuer = c.CertIssuer
                          version = c.Version }
                }
            ()
        }

    /// Every site's checks since `since`, oldest first.
    member _.SiteChecks(since: DateTimeOffset) : Task<Sites.Check[]> =
        task {
            use! ctx = factory.OpenContextAsync()
            let from = utc since
            let! rows = selectTask ctx { for x in site_check do where (x.checked_at >= from); orderBy x.checked_at; thenBy x.id }
            return
                rows
                |> Seq.map (fun x ->
                    ({ Site = x.site; At = dto x.checked_at; Ok = x.ok; Status = x.status; Ms = x.ms; Error = x.error
                       CertExpires = x.cert_expires |> Option.map dto; CertIssuer = x.cert_issuer; Version = x.version }: Sites.Check))
                |> Seq.toArray
        }

    /// (node id, name): the key agent links and wakes use.
    member _.MachineKey(machineId: int64) : Task<(string * string) option> =
        task {
            use! ctx = factory.OpenContextAsync()
            return! selectTask ctx { for m in machine do where (m.id = machineId); select (m.node_id, m.name); tryHead }
        }

    /// approved / pending / rejected, or "unknown" before the machine's first push.
    member _.StatusOf(nodeId: string, name: string) : Task<string> =
        task {
            use! ctx = factory.OpenContextAsync()
            let! m = selectTask ctx { for x in machine do where (x.node_id = nodeId && x.name = name); tryHead }
            return m |> Option.map _.status |> Option.defaultValue "unknown"
        }

    interface IDisposable with
        member _.Dispose() = factory.Dispose()

