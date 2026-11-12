/// tailscaled's LocalAPI over its unix socket: who is calling (WhoIs) and our own addresses.
module Harbor.Tailscale

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open System.Threading.Tasks

/// A tailnet node as WhoIs reports it.
type Peer =
    { NodeId: string // StableID: survives renames and re-auth
      NodeName: string
      Login: string
      Os: string option }

/// The snap keeps its socket under /var/snap; packaged installs use /run or /var/run.
let socketCandidates =
    [ "/var/snap/tailscale/common/socket/tailscaled.sock"
      "/run/tailscale/tailscaled.sock"
      "/var/run/tailscale/tailscaled.sock" ]

let findSocket (configured: string option) =
    match configured with
    | Some p -> if File.Exists p then Ok p else Error $"tailscale socket not found: {p}"
    | None ->
        socketCandidates
        |> List.tryFind File.Exists
        |> Option.map Ok
        |> Option.defaultValue (Error $"""no tailscaled socket at {String.Join(", ", socketCandidates)}""")

let private str (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
    | _ -> None

let private obj (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Object -> Some v
    | _ -> None

/// `GET /localapi/v0/whois?addr=ip:port` body.
let parseWhoIs (json: string) : Peer option =
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    match obj root "Node", obj root "UserProfile" with
    | Some node, Some user ->
        match str node "StableID", str node "Name", str user "LoginName" with
        | Some id, Some name, Some login ->
            Some
                { NodeId = id
                  NodeName = name.TrimEnd '.'
                  Login = login
                  Os = obj node "Hostinfo" |> Option.bind (fun h -> str h "OS") }
        | _ -> None
    | _ -> None

/// `GET /localapi/v0/status` body: this node's Tailscale IPs.
let parseSelfAddresses (json: string) : IPAddress[] =
    use doc = JsonDocument.Parse json
    match obj doc.RootElement "Self" with
    | Some self ->
        match self.TryGetProperty "TailscaleIPs" with
        | true, ips when ips.ValueKind = JsonValueKind.Array ->
            ips.EnumerateArray()
            |> Seq.choose (fun e ->
                match e.GetString() with
                | null -> None
                | s ->
                    match IPAddress.TryParse s with
                    | true, (ip: IPAddress | null) -> Option.ofObj ip
                    | _ -> None)
            |> Seq.toArray
        | _ -> [||]
    | None -> [||]

type Client(socket: string) =
    let handler =
        new SocketsHttpHandler(
            ConnectCallback =
                fun _ ct ->
                    task {
                        let s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                        try
                            do! s.ConnectAsync(UnixDomainSocketEndPoint socket, ct)
                        with ex ->
                            s.Dispose()
                            raise (IOException($"tailscaled socket {socket}: {ex.Message}", ex))
                        return new NetworkStream(s, true) :> Stream
                    }
                    |> ValueTask<Stream>
        )
    let http = new HttpClient(handler, BaseAddress = Uri "http://local-tailscaled.sock", Timeout = TimeSpan.FromSeconds 5.)
    do http.DefaultRequestHeaders.Add("Sec-Tailscale", "localapi")

    member _.Socket = socket

    /// None when tailscaled doesn't know the address (not a tailnet peer).
    member _.WhoIs(ip: IPAddress, port: int) : Task<Peer option> =
        task {
            let addr = if ip.AddressFamily = AddressFamily.InterNetworkV6 then $"[{ip}]:{port}" else $"{ip}:{port}"
            use! r = http.GetAsync($"/localapi/v0/whois?addr={Uri.EscapeDataString addr}")
            if r.StatusCode = HttpStatusCode.NotFound then
                return None
            else
                r.EnsureSuccessStatusCode() |> ignore
                let! body = r.Content.ReadAsStringAsync()
                return parseWhoIs body
        }

    member _.SelfAddresses() : Task<IPAddress[]> =
        task {
            let! body = http.GetStringAsync "/localapi/v0/status?peers=false"
            return parseSelfAddresses body
        }

    interface IDisposable with
        member _.Dispose() = http.Dispose()
