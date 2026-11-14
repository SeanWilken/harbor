module Harbor.Domain

open System

type Remote = { Name: string; Url: string }

/// Effective git identity for a repo, with the config scope it came from (local/global/system).
type Identity =
    { Name: string option
      Email: string option
      Scope: string option }

type Branch =
    { Head: string option // None = detached
      Oid: string option // None = no commits yet
      Upstream: string option
      Ahead: int
      Behind: int }

type Changes =
    { Staged: int
      Unstaged: int
      Untracked: int
      Conflicted: int
      Paths: string[] }

type Commit =
    { Date: DateTimeOffset
      Author: string
      Subject: string }

/// A local branch from `git for-each-ref refs/heads`.
type LocalBranch =
    { Name: string
      Upstream: string option
      Ahead: int
      Behind: int
      /// The upstream branch was deleted on the remote.
      Gone: bool
      Date: DateTimeOffset option }

/// A commit on a local branch that no remote-tracking ref contains.
type UnpushedCommit =
    { Sha: string
      Date: DateTimeOffset
      Subject: string
      /// The local branch it was reached from.
      Branch: string }

/// Work that exists only in this clone: commits on local branches no remote has, and stashes.
/// Measured against remote-tracking refs, so it's as fresh as the last fetch.
type InFlight =
    { Unpushed: UnpushedCommit[] // newest first, at most 20
      UnpushedCount: int
      Stashes: int }

type Repo =
    { Path: string
      Name: string
      Remotes: Remote[]
      Identity: Identity
      Branch: Branch option
      Changes: Changes option
      LastCommit: Commit option
      LastModified: DateTimeOffset option
      /// Main working tree when this is a linked worktree (`git worktree add`); they share refs.
      WorktreeOf: string option
      /// Last `git fetch` by anyone (FETCH_HEAD mtime): ahead/behind is only as fresh as this.
      LastFetch: DateTimeOffset option
      /// Why harbor's own last fetch of this repo failed, if it did.
      FetchError: string option
      Error: string option
      /// Added after v1, so stored payloads without it still read.
      Branches: LocalBranch[] option
      /// When behind: commit date of the oldest upstream commit not yet in HEAD, i.e. how long
      /// this clone has been missing upstream work.
      BehindSince: DateTimeOffset option
      /// None for repos without remotes (everything is local there) and older agents.
      InFlight: InFlight option }

type ServiceState =
    | Running
    | Stopped
    | Unknown

type ServiceStatus =
    { Name: string
      Check: string
      State: ServiceState
      Detail: string }

type Machine =
    { Hostname: string
      Os: string
      ScannedAt: DateTimeOffset
      ScanMs: int64
      /// Scan roots that existed at scan time; the hub doesn't report repos under a missing
      /// root (unmounted drive) as removed.
      Roots: string[] }

type Gpu =
    { Name: string
      Percent: float
      MemUsedBytes: int64
      MemTotalBytes: int64 }

type Disk =
    { Mount: string
      Format: string
      TotalBytes: int64
      FreeBytes: int64 }

/// Processes grouped by name ("dotnet ×3"), with CPU since the previous sample.
type TopProcess =
    { Name: string
      Count: int
      CpuPercent: float
      RssBytes: int64 }

/// A point-in-time sample. Percentages are averages since the previous sample (about one push
/// interval); None where the platform or hardware can't say.
type Resources =
    { Cores: int
      CpuPercent: float option
      MemTotalBytes: int64 option
      MemAvailableBytes: int64 option
      Load1: float option
      UptimeSeconds: int64
      /// Since the last keyboard/mouse input in the desktop session; None without one.
      IdleSeconds: int64 option
      Gpus: Gpu[]
      Disks: Disk[]
      Top: TopProcess[] }

type NetAddress = { Address: string; PrefixLength: int }

/// Kind: "ethernet" | "wifi" | "tailnet" | "other". Virtual bridges and loopback are left out.
type NetInterface =
    { Name: string
      Kind: string
      Mac: string option
      Addresses: NetAddress[]
      Gateway: string option
      /// Tells two networks with the same private subnet apart (home vs office 192.168.1.0/24).
      GatewayMac: string option }

/// A way into this machine besides harbor, from its config: "display" (a desktop viewer page, e.g.
/// DisplayOverInternet's receiver, which does its own device pairing) or "rdp" (a Remote Desktop host).
/// Only addresses: never credentials.
type AccessEntry =
    { Kind: string
      Name: string option
      Url: string option
      Host: string option
      Port: int option }

/// A role this machine's config declares, and how much outside work it takes while in it:
/// "full" (spare capacity, heavy work and inference welcome), "light" (small jobs only) or "none".
/// Title, description and duties were added after v0 (options, so older snapshots still read).
/// Start and Stop are short labels (an app's file name, a URL, "unit x"), never full paths.
type RoleInfo =
    { Name: string
      Work: string
      Title: string option
      Description: string option
      Power: string option
      Start: string[] option
      Stop: string[] option }

/// The machine's current duty: one at a time, switched by a hub admin or at the machine (tray).
/// A statement other systems can route work by; harbor reports it and doesn't enforce it.
type RoleState =
    { Name: string
      Work: string
      Since: DateTimeOffset
      /// "default", "tray" or the hub admin who switched it.
      By: string
      Declared: RoleInfo[]
      /// What the last switch's actions did, one line each (empty for the default).
      Applied: string[] }

type Snapshot =
    { Machine: Machine
      Repos: Repo[]
      Services: ServiceStatus[]
      Resources: Resources option
      Networks: NetInterface[] option
      Access: AccessEntry[] option
      /// None when the machine declares no roles (and for older agents).
      Role: RoleState option }

type OsInfo =
    { Id: string // os-release ID ("zorin", "ubuntu") or "windows"
      Version: string
      PrettyName: string
      Kernel: string
      Arch: string }

type Package = { Name: string; Version: string }

/// A pending upgrade the package manager already knows about (no refresh is run).
type Upgrade = { Name: string; From: string; To: string }

/// One package manager's view. Error set when it exists but listing failed.
type PackageSet =
    { Manager: string // "dpkg" | "snap" | "flatpak" | ...
      Packages: Package[]
      Upgrades: Upgrade[]
      Error: string option }

/// Where a repo is and what it tracks. No HEAD: that changes with every commit and lives in
/// snapshots; a manifest should change when the machine changes.
type ManifestRepo =
    { Path: string
      RemoteKey: string option
      Branch: string option }

/// What is installed on a machine, sorted so equal machines give equal JSON. Hash covers
/// everything except TakenAt, so an unchanged machine isn't resent or stored twice.
type Manifest =
    { TakenAt: DateTimeOffset
      Hash: string
      Os: OsInfo
      Sets: PackageSet[]
      Repos: ManifestRepo[] }

/// One commit from `git log`, for the hub's timeline.
type LoggedCommit =
    { Sha: string
      Date: DateTimeOffset // author date
      Author: string
      Subject: string }

type RepoCommits = { Path: string; Commits: LoggedCommit[] }

/// Work the hub asks an agent to do. Kind is checked against the agent's `allow` list.
/// Arg: for "diff", a commit sha or "worktree" (uncommitted changes).
type JobRequest = { Id: int64; Kind: string; Path: string option; Arg: string option }

type JobResult =
    { Id: int64
      Ok: bool
      Output: string
      FinishedAt: DateTimeOffset }

/// Agent -> hub. Commits are sent for repos whose HEAD the hub hasn't acknowledged yet.
/// Fields added after v1 are options so stored payloads still deserialize.
type Push =
    { Version: int
      Machine: string
      PushSeconds: int
      Snapshot: Snapshot
      Commits: RepoCommits[]
      JobResults: JobResult[] option
      /// Sent only when its hash differs from what the hub last stored.
      Manifest: Manifest option
      /// One-time code from the hub's "Pair a device", sent until the machine is approved.
      PairCode: string option
      /// The agent's own build (git commit), so the hub can say when a newer harbor exists.
      AgentCommit: string option }

type PushReply =
    { Status: string
      Message: string option
      Jobs: JobRequest[] option
      /// Hash of the newest manifest the hub holds for this machine.
      ManifestHash: string option
      /// "accepted" or "rejected" when the push carried a pairing code.
      PairResult: string option
      /// Newer harbor commits than this agent's build, when the hub knows of any.
      Update: HarborUpdate option }

and HarborUpdate = { Behind: int; Latest: string; LatestAt: DateTimeOffset }
