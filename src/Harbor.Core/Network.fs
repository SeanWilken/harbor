/// This machine's network interfaces, with each one's gateway and the gateway's hardware address
/// (what tells two networks with the same private subnet apart).
module Harbor.Network

open System
open System.IO
open System.Net
open System.Net.NetworkInformation
open System.Net.Sockets
open System.Text.RegularExpressions
open System.Threading.Tasks
open Harbor.Domain

/// Container/VM bridges and virtual adapters say nothing about where the machine is.
let private virtualName = Regex(@"^(lo|docker|podman|veth|br-|virbr|cni|flannel|vEthernet|vmnet|VirtualBox|Loopback)", RegexOptions.IgnoreCase)

let isTailnet (ip: IPAddress) =
    match ip.AddressFamily with
    | AddressFamily.InterNetwork ->
        let b = ip.GetAddressBytes()
        b[0] = 100uy && b[1] >= 64uy && b[1] <= 127uy // 100.64.0.0/10
    | AddressFamily.InterNetworkV6 -> ip.ToString().StartsWith("fd7a:115c:a1e0:", StringComparison.OrdinalIgnoreCase)
    | _ -> false

let kind (name: string) (t: NetworkInterfaceType) (addrs: IPAddress seq) =
    if name.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) || Seq.exists isTailnet addrs then "tailnet"
    // .NET on Linux reports Wi-Fi as Ethernet; sysfs knows better.
    elif t = NetworkInterfaceType.Wireless80211 || (OperatingSystem.IsLinux() && Directory.Exists $"/sys/class/net/{name}/wireless") then "wifi"
    elif t = NetworkInterfaceType.Ethernet || t = NetworkInterfaceType.GigabitEthernet || t = NetworkInterfaceType.FastEthernetT then "ethernet"
    else "other"

let private normMac (s: string) = s.Replace('-', ':').ToLowerInvariant()

/// IP -> MAC from /proc/net/arp (Linux) or `arp -a` (Windows). Incomplete entries are skipped.
let parseArp (text: string) : Map<string, string> =
    let mac = Regex(@"^\s*(\d+\.\d+\.\d+\.\d+)\s+(?:0x\w+\s+0x\w+\s+)?([0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5})\b", RegexOptions.Multiline)
    mac.Matches text
    |> Seq.map (fun m -> m.Groups[1].Value, normMac m.Groups[2].Value)
    |> Seq.filter (fun (_, m) -> m <> "00:00:00:00:00:00")
    |> Map.ofSeq

let private arpTable () : Task<Map<string, string>> =
    task {
        if OperatingSystem.IsWindows() then
            match! Proc.run (TimeSpan.FromSeconds 5.) None [] [ "arp"; "-a" ] with
            | Ok o when o.Code = 0 -> return parseArp o.Out
            | _ -> return Map.empty
        else
            return (try parseArp (File.ReadAllText "/proc/net/arp") with _ -> Map.empty)
    }

let private macOf (n: NetworkInterface) =
    match n.GetPhysicalAddress().GetAddressBytes() with
    | [||] -> None
    | b -> Some(b |> Array.map (fun x -> x.ToString "x2") |> String.concat ":")

/// Up, non-virtual interfaces that have at least one address.
let interfaces () : Task<NetInterface[]> =
    task {
        let! arp = arpTable ()
        return
            NetworkInterface.GetAllNetworkInterfaces()
            |> Array.filter (fun n ->
                n.OperationalStatus = OperationalStatus.Up
                && n.NetworkInterfaceType <> NetworkInterfaceType.Loopback
                && not (virtualName.IsMatch n.Name))
            |> Array.choose (fun n ->
                try
                    let props = n.GetIPProperties()
                    let addrs =
                        props.UnicastAddresses
                        |> Seq.filter (fun a -> not a.Address.IsIPv6LinkLocal)
                        |> Array.ofSeq
                    if addrs.Length = 0 then None
                    else
                        let gateway =
                            props.GatewayAddresses
                            |> Seq.map (fun g -> g.Address)
                            |> Seq.tryFind (fun a -> a.AddressFamily = AddressFamily.InterNetwork && not (a.Equals IPAddress.Any))
                            |> Option.map string
                        Some
                            { Name = n.Name
                              Kind = kind n.Name n.NetworkInterfaceType (addrs |> Seq.map _.Address)
                              Mac = macOf n
                              Addresses = addrs |> Array.map (fun a -> { Address = string a.Address; PrefixLength = a.PrefixLength })
                              Gateway = gateway
                              GatewayMac = gateway |> Option.bind arp.TryFind }
                with _ ->
                    None)
    }
