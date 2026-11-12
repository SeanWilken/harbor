module Harbor.Tests.ResourcesTests

open System
open System.IO
open Xunit
open Harbor

[<Fact>]
let ``cpu percent is busy jiffies over total between two /proc/stat readings`` () =
    let a = Resources.parseProcStat "cpu  100 0 50 800 50 0 0 0 0 0\ncpu0 1 2 3 4\n"
    let b = Resources.parseProcStat "cpu  160 0 70 900 70 0 0 0 0 0\n"
    Assert.Equal(Some(150L, 1000L), a)
    Assert.Equal(Some 40., Resources.percent a.Value b.Value) // 80 busy of 200
    Assert.Equal(None, Resources.percent b.Value b.Value)

[<Fact>]
let ``meminfo, loadavg, nvidia-smi and the GNOME idle monitor parse`` () =
    Assert.Equal((Some(32L <<< 30), Some(1024L)), Resources.parseMeminfo "MemTotal:       33554432 kB\nMemFree: 5 kB\nMemAvailable:       1 kB\n")
    Assert.Equal(Some 2.41, Resources.parseLoad "2.41 1.90 1.20 3/1200 4242\n")
    let gpus = Resources.parseNvidiaSmi "NVIDIA GeForce RTX 2080, 21, 384, 8192\n"
    Assert.Equal("NVIDIA GeForce RTX 2080", gpus[0].Name)
    Assert.Equal(21., gpus[0].Percent)
    Assert.Equal(8192L * 1048576L, gpus[0].MemTotalBytes)
    Assert.Empty(Resources.parseNvidiaSmi "No devices were found\n")
    Assert.Equal(Some 8L, Resources.parseIdle "(uint64 8724,)\n")
    Assert.Equal(None, Resources.parseIdle "Error: GDBus.Error")

[<Fact>]
let ``top processes group by name, heaviest cpu first`` () =
    let t = Resources.top 2 [ "dotnet", 10., 100L; "postgres", 1., 50L; "dotnet", 5., 200L; "bash", 0., 10L ]
    Assert.Equal<string[]>([| "dotnet"; "postgres" |], t |> Array.map _.Name)
    Assert.Equal(2, t[0].Count)
    Assert.Equal(15., t[0].CpuPercent)
    Assert.Equal(300L, t[0].RssBytes)

[<Fact>]
let ``disks drop pseudo filesystems, tiny ones, and bind-mount duplicates`` () =
    let gb n = int64 n * (1L <<< 30)
    let ds =
        Resources.disks
            [ "/", DriveType.Fixed, "ext4", gb 900, gb 800
              "/mnt/srv", DriveType.Fixed, "ext4", gb 1000, gb 940
              "/srv", DriveType.Fixed, "ext4", gb 1000, gb 940
              "/snap/core/1", DriveType.Fixed, "squashfs", gb 2, 0L
              "/boot/efi", DriveType.Removable, "vfat", 100_000_000L, 1L
              "/dev/shm", DriveType.Ram, "tmpfs", gb 16, gb 16 ]
    Assert.Equal<string[]>([| "/"; "/srv" |], ds |> Array.map _.Mount)

[<Fact>]
let ``arp tables from Linux and Windows give ip to mac`` () =
    let linux =
        "IP address       HW type     Flags       HW address            Mask     Device\n"
        + "192.168.50.1     0x1         0x2         02:11:22:33:44:55     *        enp6s0\n"
        + "192.168.50.9     0x1         0x0         00:00:00:00:00:00     *        enp6s0\n"
    Assert.Equal<Map<string, string>>(Map [ "192.168.50.1", "02:11:22:33:44:55" ], Network.parseArp linux)
    let windows = "Interface: 192.168.1.20 --- 0x7\n  Internet Address      Physical Address      Type\n  192.168.1.1           AA-BB-CC-DD-EE-FF     dynamic\n"
    Assert.Equal<Map<string, string>>(Map [ "192.168.1.1", "aa:bb:cc:dd:ee:ff" ], Network.parseArp windows)

[<Fact>]
let ``tailnet addresses are recognised`` () =
    Assert.True(Network.isTailnet (Net.IPAddress.Parse "100.101.1.30"))
    Assert.False(Network.isTailnet (Net.IPAddress.Parse "100.128.0.1"))
    Assert.True(Network.isTailnet (Net.IPAddress.Parse "fd7a:115c:a1e0::1"))
    Assert.False(Network.isTailnet (Net.IPAddress.Parse "192.168.50.71"))

[<Fact>]
let ``local branches parse tracking state`` () =
    let out =
        "main\031origin/main\031\0312026-11-12T09:00:00-05:00\n"
        + "feat/ui\031origin/feat/ui\031ahead 3, behind 2\0312026-11-11T09:00:00-05:00\n"
        + "old\031origin/old\031gone\0312025-01-01T00:00:00Z\n"
        + "scratch\031\031\0312026-11-01T00:00:00Z\n"
    let bs = GitParse.branches out
    Assert.Equal<string[]>([| "main"; "feat/ui"; "old"; "scratch" |], bs |> Array.map _.Name)
    Assert.Equal((3, 2), (bs[1].Ahead, bs[1].Behind))
    Assert.True bs[2].Gone
    Assert.Equal(None, bs[3].Upstream)

[<Fact>]
let ``process names keep only the program`` () =
    Assert.Equal("msedge", Resources.processName "msedge --type=renderer --field-trial-handle=3,i,31478")
    Assert.Equal("python3", Resources.processName "/usr/bin/python3 worker.py")
    Assert.Equal("Code", Resources.processName "Code")
    Assert.Equal("dotnet", Resources.processName "dotnet")
