// Mirrors src/Harbor/Domain.fs (camelCase JSON; F# options are omitted when None).
export type Remote = { name: string; url: string };
export type Identity = { name?: string; email?: string; scope?: string };
export type Branch = { head?: string; oid?: string; upstream?: string; ahead: number; behind: number };
export type Changes = { staged: number; unstaged: number; untracked: number; conflicted: number; paths: string[] };
export type Commit = { date: string; author: string; subject: string };
/** A commit on a local branch that no remote-tracking ref has. */
export type UnpushedCommit = { sha: string; date: string; subject: string; branch: string };
/** Work only this clone has; as fresh as its last fetch. Absent for repos without remotes. */
export type InFlight = { unpushed: UnpushedCommit[]; unpushedCount: number; stashes: number };
export type LocalBranch = { name: string; upstream?: string; ahead: number; behind: number; gone: boolean; date?: string };
export type Repo = {
  path: string;
  name: string;
  remotes: Remote[];
  identity: Identity;
  branch?: Branch;
  changes?: Changes;
  lastCommit?: Commit;
  lastModified?: string;
  worktreeOf?: string;
  lastFetch?: string;
  fetchError?: string;
  error?: string;
  branches?: LocalBranch[];
  inFlight?: InFlight;
};
export type ServiceState = "running" | "stopped" | "unknown";
export type ServiceStatus = { name: string; check: string; state: ServiceState; detail: string };
export type Machine = { hostname: string; os: string; scannedAt: string; scanMs: number; roots?: string[] };
export type Gpu = { name: string; percent: number; memUsedBytes: number; memTotalBytes: number };
export type Disk = { mount: string; format: string; totalBytes: number; freeBytes: number };
export type TopProcess = { name: string; count: number; cpuPercent: number; rssBytes: number };
export type Resources = {
  cores: number; cpuPercent?: number; memTotalBytes?: number; memAvailableBytes?: number; load1?: number;
  uptimeSeconds: number; idleSeconds?: number; gpus: Gpu[]; disks: Disk[]; top: TopProcess[];
};
export type NetAddress = { address: string; prefixLength: number };
export type NetInterface = { name: string; kind: "ethernet" | "wifi" | "tailnet" | "other"; mac?: string; addresses: NetAddress[]; gateway?: string; gatewayMac?: string };
/** A way in besides harbor (agent config `access`): a desktop viewer page or an RDP host. */
export type AccessEntry = { kind: "display" | "rdp"; name?: string; url?: string; host?: string; port?: number };
/** How much outside work a machine takes in a role: heavy work and inference, small jobs, or none. */
export type Work = "full" | "light" | "none";
export type RoleInfo = { name: string; work: Work };
/** The machine's current duty (agent config `roles`); viewers see `by` as default/tray/hub and no `applied`. */
export type RoleState = { name: string; work: Work; since: string; by: string; declared: RoleInfo[]; applied: string[] };
export type Snapshot = { machine: Machine; repos: Repo[]; services: ServiceStatus[]; resources?: Resources; networks?: NetInterface[]; access?: AccessEntry[]; role?: RoleState };

// Hub API (src/Harbor/Storage.fs)
export type MachineStatus = "pending" | "approved" | "rejected";
export type MachineSummary = {
  id: number; name: string; nodeName?: string; login?: string; os?: string; status: MachineStatus; local: boolean;
  firstSeenAt: string; lastPushAt?: string; scannedAt?: string; pushSeconds?: number;
  repos: number; dirty: number; unsynced: number; servicesUp: number; servicesTotal: number;
  hidden: string[];
  cpuPercent?: number; gpuPercent?: number; memPercent?: number; idleSeconds?: number;
  /** Holds a live link to the hub right now (jobs reach it at once). */
  connected: boolean;
  /** The agent's harbor build, and newer harbor commits the hub knows of. */
  agentCommit?: string;
  update?: { behind: number; latest: string; latestAt: string };
  role?: string; work?: Work; roleSince?: string;
};
export type JobInfo = {
  id: number; kind: string; path?: string; requestedAt: string; requestedBy: string;
  sentAt?: string; finishedAt?: string; ok?: boolean; output?: string; arg?: string;
};
export type Copy = {
  machineId: number; machine: string; path: string; branch?: string; headOid?: string;
  ahead: number; behind: number; dirty: boolean; lastCommitAt?: string;
};
export type SharedRepo = { remoteKey: string; sameHead: boolean; copies: Copy[] };
/** Who is looking at the UI: a tailnet login, or "local" on the hub itself. */
export type Me = { login: string; node?: string; self?: boolean; browserUrl?: string; role: "admin" | "viewer"; admin: boolean; agentUrl?: string };

// Manifests and recaps (src/Harbor/Manifest.fs, Recap.fs)
export type OsInfo = { id: string; version: string; prettyName: string; kernel: string; arch: string };
export type Package = { name: string; version: string };
export type Upgrade = { name: string; from: string; to: string };
export type PackageSet = { manager: string; packages: Package[]; upgrades: Upgrade[]; error?: string };
export type ManifestRepo = { path: string; remoteKey?: string; branch?: string };
export type Manifest = { takenAt: string; hash: string; os: OsInfo; sets: PackageSet[]; repos: ManifestRepo[] };
export type ManifestInfo = { id: number; machineId: number; takenAt: string; receivedAt: string; hash: string; os: string; packageCount: number };
export type PackageChange = { manager: string; name: string; before?: string; after?: string };
export type ManifestDiff = {
  a: ManifestInfo; b: ManifestInfo; os?: { a: OsInfo; b: OsInfo };
  managers: { a: string[]; b: string[] }; changes: PackageChange[];
};
export type RepoNote = { machine: string; name: string; path: string; detail: string; since?: string };
export type MachineChanges = { machine: string; os: string; kernel: string; changes: PackageChange[]; upgrades: Upgrade[] };
export type Drift = { manager: string; name: string; versions: [string, string][] };
export type RecapReport = {
  periodStart: string; periodEnd: string;
  settings: { neglectedDays: number; dirtyDays: number; behindDays: number; weekday: string | number };
  behind: RepoNote[]; dirty: RepoNote[]; unpushed: RepoNote[]; neglected: RepoNote[];
  machines: MachineChanges[]; drift: Drift[]; driftMore: number;
};
export type RecapInfo = { id: number; periodStart: string; periodEnd: string; createdAt: string };
