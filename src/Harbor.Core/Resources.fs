/// CPU, memory, GPU, disks, top processes and input idle time. Linux reads /proc; Windows uses
/// kernel32/user32; both use .NET for processes, drives and uptime. Anything unavailable is None.
module Harbor.Resources

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open System.Threading.Tasks
open Harbor.Domain

let private inv = CultureInfo.InvariantCulture
let private lines (s: string) = s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
let private tryInt64 (s: string) = match Int64.TryParse(s.Trim(), NumberStyles.Integer, inv) with | true, v -> Some v | _ -> None
let private tryFloat (s: string) = match Double.TryParse(s.Trim(), NumberStyles.Float, inv) with | true, v -> Some v | _ -> None

// ---- pure parsers ----

/// First `cpu ` line of /proc/stat -> (busy, total) jiffies. Idle includes iowait.
let parseProcStat (text: string) : (int64 * int64) option =
    lines text
    |> Array.tryFind (fun l -> l.StartsWith "cpu ")
    |> Option.bind (fun l ->
        let v = l.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.skip 1 |> Array.choose tryInt64 |> Array.truncate 8
        if v.Length < 4 then None
        else
            let total = Array.sum v
            Some(total - v[3] - (if v.Length > 4 then v[4] else 0L), total))

/// Busy share of the interval between two (busy, total) readings.
let percent (busy0: int64, total0: int64) (busy1: int64, total1: int64) =
    if total1 > total0 then Some(Math.Round(100. * float (busy1 - busy0) / float (total1 - total0), 1)) else None

/// /proc/meminfo -> (MemTotal, MemAvailable) in bytes.
let parseMeminfo (text: string) =
    let get key =
        lines text
        |> Array.tryPick (fun l ->
            if l.StartsWith(key + ":") then l.Substring(key.Length + 1).Trim().Split(' ')[0] |> tryInt64 |> Option.map ((*) 1024L)
            else None)
    get "MemTotal", get "MemAvailable"

/// /proc/loadavg -> 1-minute load.
let parseLoad (text: string) = text.Split(' ') |> Array.tryHead |> Option.bind tryFloat

/// `nvidia-smi --query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits`
let parseNvidiaSmi (out: string) : Gpu[] =
    lines out
    |> Array.choose (fun l ->
        match l.Split(',') |> Array.map _.Trim() with
        | [| name; util; used; total |] ->
            match tryFloat util, tryInt64 used, tryInt64 total with
            | Some u, Some m, Some t -> Some { Name = name; Percent = u; MemUsedBytes = m * 1048576L; MemTotalBytes = t * 1048576L }
            | _ -> None
        | _ -> None)

/// GNOME's IdleMonitor over gdbus: `(uint64 8724,)` (milliseconds) -> seconds.
let parseIdle (out: string) =
    let m = Regex.Match(out, @"uint64 (\d+)")
    if m.Success then tryInt64 m.Groups[1].Value |> Option.map (fun ms -> ms / 1000L) else None

/// A process's short name. On Linux .NET can return a rewritten argv ("msedge --type=renderer
/// --field-trial-handle=…"), which is noise and can carry session tokens: keep the program only.
let processName (raw: string) =
    let first = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead |> Option.defaultValue "?"
    let name = first.Replace('\\', '/').Split('/') |> Array.last
    if name.Length > 40 then name.Substring(0, 40) else name

/// Group per-process samples by name, heaviest CPU first (then memory).
let top (n: int) (procs: (string * float * int64) seq) : TopProcess[] =
    procs
    |> Seq.groupBy (fun (name, _, _) -> name)
    |> Seq.map (fun (name, ps) ->
        { Name = name
          Count = Seq.length ps
          CpuPercent = ps |> Seq.sumBy (fun (_, c, _) -> c) |> fun c -> Math.Round(c, 1)
          RssBytes = ps |> Seq.sumBy (fun (_, _, r) -> r) })
    |> Seq.sortByDescending (fun p -> p.CpuPercent, p.RssBytes)
    |> Seq.truncate n
    |> Array.ofSeq

let private pseudoFs = set [ "squashfs"; "efivarfs"; "tracefs"; "overlay"; "tmpfs"; "devtmpfs"; "ramfs" ]

/// Real disks: fixed or removable, at least 1 GiB, one entry per filesystem (bind mounts like
/// /mnt/srv and /srv show once, under the shorter path).
let disks (drives: (string * DriveType * string * int64 * int64) seq) : Disk[] =
    drives
    |> Seq.filter (fun (_, t, fmt, total, _) ->
        (t = DriveType.Fixed || t = DriveType.Removable) && not (pseudoFs.Contains fmt) && total >= (1L <<< 30))
    |> Seq.groupBy (fun (_, _, fmt, total, free) -> fmt, total, free)
    |> Seq.map (fun (_, ds) ->
        let mount, _, fmt, total, free = ds |> Seq.minBy (fun (m, _, _, _, _) -> m.Length, m)
        { Mount = mount; Format = fmt; TotalBytes = total; FreeBytes = free })
    |> Seq.sortBy _.Mount
    |> Array.ofSeq

// ---- platform readers ----

module private Win =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type MemStatus =
        val mutable Length: uint32
        val mutable Load: uint32
        val mutable TotalPhys: uint64
        val mutable AvailPhys: uint64
        val mutable TotalPageFile: uint64
        val mutable AvailPageFile: uint64
        val mutable TotalVirtual: uint64
        val mutable AvailVirtual: uint64
        val mutable AvailExtendedVirtual: uint64

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type LastInput =
        val mutable Size: uint32
        val mutable Time: uint32

    // FILETIME is two uint32s: the same layout as one int64.
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool GetSystemTimes(int64& idle, int64& kernel, int64& user)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool GlobalMemoryStatusEx(MemStatus& status)

    [<DllImport("user32.dll")>]
    extern bool GetLastInputInfo(LastInput& info)

    /// Kernel time includes idle time.
    let cpu () =
        let mutable idle = 0L
        let mutable kernel = 0L
        let mutable user = 0L
        if GetSystemTimes(&idle, &kernel, &user) then Some(kernel + user - idle, kernel + user) else None

    let mem () =
        let mutable s = MemStatus()
        s.Length <- uint32 (Marshal.SizeOf<MemStatus>())
        if GlobalMemoryStatusEx(&s) then Some(int64 s.TotalPhys), Some(int64 s.AvailPhys) else None, None

    /// Only works from the interactive session (not a session-0 service).
    let idle () =
        let mutable li = LastInput()
        li.Size <- uint32 (Marshal.SizeOf<LastInput>())
        if GetLastInputInfo(&li) then Some(int64 (uint32 Environment.TickCount - li.Time) / 1000L) else None

let private isWindows = OperatingSystem.IsWindows()
let private read (path: string) = try Some(File.ReadAllText path) with _ -> None

let private cpuTimes () =
    if isWindows then Win.cpu () else read "/proc/stat" |> Option.bind parseProcStat

let private memory () =
    if isWindows then Win.mem ()
    else
        match read "/proc/meminfo" with
        | Some t -> parseMeminfo t
        | None -> None, None

let private load () = if isWindows then None else read "/proc/loadavg" |> Option.bind parseLoad

let private idle () : Task<int64 option> =
    task {
        if isWindows then
            return (try Win.idle () with _ -> None)
        else
            let argv =
                [ "gdbus"; "call"; "--session"; "--dest"; "org.gnome.Mutter.IdleMonitor"
                  "--object-path"; "/org/gnome/Mutter/IdleMonitor/Core"; "--method"; "org.gnome.Mutter.IdleMonitor.GetIdletime" ]
            match! Proc.run (TimeSpan.FromSeconds 3.) None [] argv with
            | Ok o when o.Code = 0 -> return parseIdle o.Out
            | _ -> return None
    }

/// AMD exposes busy % in sysfs; NVIDIA needs nvidia-smi (on PATH on Linux and Windows).
let private amdGpus () =
    if isWindows then [||]
    else
        try
            Directory.GetDirectories("/sys/class/drm", "card*")
            |> Array.choose (fun d ->
                read (Path.Combine(d, "device", "gpu_busy_percent"))
                |> Option.bind tryFloat
                |> Option.map (fun p ->
                    let mem f = read (Path.Combine(d, "device", f)) |> Option.bind tryInt64 |> Option.defaultValue 0L
                    { Name = $"amdgpu {Path.GetFileName d}"; Percent = p
                      MemUsedBytes = mem "mem_info_vram_used"; MemTotalBytes = mem "mem_info_vram_total" }))
        with _ ->
            [||]

let private driveRows () =
    DriveInfo.GetDrives()
    |> Array.choose (fun d ->
        try
            if d.IsReady then Some(d.Name, d.DriveType, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace) else None
        with _ ->
            None)

/// (cpu time, rss) per live process keyed by (pid, name) so a reused pid doesn't count.
let private processTimes () =
    Process.GetProcesses()
    |> Array.choose (fun p ->
        use p = p
        try Some((p.Id, processName p.ProcessName), (p.TotalProcessorTime, p.WorkingSet64)) with _ -> None)
    |> Map.ofArray

/// Keeps the previous CPU readings so each sample reports the average since the last one.
/// Samples closer than 5 s apart return the cached one, so extra callers don't shrink the window.
type Sampler() =
    let gate = new Threading.SemaphoreSlim(1, 1)
    let mutable prevCpu = None
    let mutable prevProcs = Map.empty
    let mutable prevAt = Stopwatch.GetTimestamp()
    let mutable last: Resources option = None
    let mutable nvidia = true // stop trying once nvidia-smi is missing

    let gpus () =
        task {
            if not nvidia then
                return amdGpus ()
            else
                let q = [ "nvidia-smi"; "--query-gpu=name,utilization.gpu,memory.used,memory.total"; "--format=csv,noheader,nounits" ]
                match! Proc.run (TimeSpan.FromSeconds 5.) None [] q with
                | Ok o when o.Code = 0 -> return parseNvidiaSmi o.Out
                | Ok _ -> return amdGpus ()
                | Error _ ->
                    nvidia <- false
                    return amdGpus ()
        }

    member _.Sample() : Task<Resources> =
        task {
            do! gate.WaitAsync()
            try
                match last with
                | Some r when Stopwatch.GetElapsedTime(prevAt).TotalSeconds < 5. -> return r
                | _ ->
                    if prevCpu.IsNone then // first sample: take a 1 s baseline so it has percentages too
                        prevCpu <- cpuTimes ()
                        prevProcs <- processTimes ()
                        prevAt <- Stopwatch.GetTimestamp()
                        do! Task.Delay 1000
                    let cores = Environment.ProcessorCount
                    let cpu = cpuTimes ()
                    let procs = processTimes ()
                    let wall = Stopwatch.GetElapsedTime(prevAt).TotalSeconds
                    let shares =
                        procs
                        |> Seq.map (fun kv ->
                            let (_, name), (t, rss) = kv.Key, kv.Value
                            let used =
                                match prevProcs.TryFind kv.Key with
                                | Some(t0, _) when wall > 0. -> 100. * (t - t0).TotalSeconds / (wall * float cores)
                                | _ -> 0.
                            name, max 0. used, rss)
                    let! gpus = gpus ()
                    let! idle = idle ()
                    let memTotal, memAvail = memory ()
                    let r =
                        { Cores = cores
                          CpuPercent = match prevCpu, cpu with Some a, Some b -> percent a b | _ -> None
                          MemTotalBytes = memTotal
                          MemAvailableBytes = memAvail
                          Load1 = load ()
                          UptimeSeconds = Environment.TickCount64 / 1000L
                          IdleSeconds = idle
                          Gpus = gpus
                          Disks = disks (driveRows ())
                          Top = top 6 shares }
                    prevCpu <- cpu
                    prevProcs <- procs
                    prevAt <- Stopwatch.GetTimestamp()
                    last <- Some r
                    return r
            finally
                gate.Release() |> ignore
        }
