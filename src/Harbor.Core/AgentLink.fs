/// The agent's persistent connection to the hub: one WebSocket per agent, opened by the agent, so
/// agents never listen for anything. It carries live messages only. Durable work (jobs and their
/// results) still travels by push and reply, so nothing is replayed from this socket after a reconnect.
///
/// Wire: one JSON object per text message, at most 64 KiB, `{ "v": 1, "type": ..., ... }`.
///   agent -> hub   hello    { machine }         first message, within 5 s
///   hub -> agent   welcome  { status }          machine status on the hub (approved/pending/rejected/unknown)
///   hub -> agent   wake     { reason }          "job": push now (a job is waiting); "refresh": rescan, then push
/// Unknown types are ignored, so either side can add message families (display signaling later).
module Harbor.AgentLink

open System
open System.Collections.Concurrent
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

let path = "/api/agent/connect"
let maxMessage = 64 * 1024

type Message =
    { V: int
      Type: string
      Machine: string option
      Status: string option
      Reason: string option }

let message kind = { V = 1; Type = kind; Machine = None; Status = None; Reason = None }

let encode (m: Message) = JsonSerializer.Serialize(m, Config.jsonOptions)

let decode (text: string) =
    try
        match JsonSerializer.Deserialize<Message>(text, Config.jsonOptions) with
        | null -> None
        | m when m.V = 1 && not (String.IsNullOrEmpty m.Type) -> Some m
        | _ -> None
    with _ ->
        None

/// One text message, or None when the peer closed. Throws past maxMessage or on binary frames.
let receive (ws: WebSocket) (ct: CancellationToken) : Task<string option> =
    task {
        use buffer = new MemoryStream()
        let chunk = Array.zeroCreate<byte> 4096
        let mutable result = None
        let mutable fin = false
        while not fin do
            let! r = ws.ReceiveAsync(ArraySegment chunk, ct)
            if r.MessageType = WebSocketMessageType.Close then
                fin <- true
            elif r.MessageType <> WebSocketMessageType.Text then
                raise (InvalidDataException "expected text messages")
            else
                buffer.Write(chunk, 0, r.Count)
                if buffer.Length > int64 maxMessage then raise (InvalidDataException "message too large")
                if r.EndOfMessage then
                    result <- Some(Encoding.UTF8.GetString(buffer.ToArray()))
                    fin <- true
        return result
    }

/// A socket allows one send at a time.
type Connection(ws: WebSocket) =
    let sendGate = new SemaphoreSlim(1, 1)
    member _.Socket = ws
    member _.Send(m: Message, ct: CancellationToken) : Task =
        task {
            do! sendGate.WaitAsync ct
            try
                if ws.State = WebSocketState.Open then
                    do! ws.SendAsync(ArraySegment(Encoding.UTF8.GetBytes(encode m)), WebSocketMessageType.Text, true, ct)
            finally
                sendGate.Release() |> ignore
        }

/// Hub side: live agent connections by (Tailscale StableID, machine name), the same identity as pushes.
/// A new connection for a machine replaces the old one.
type Registry() =
    let conns = ConcurrentDictionary<struct (string * string), Connection>()
    let mutable local: string -> unit = ignore

    member _.Add(key: string * string, c: Connection) =
        let k = struct (fst key, snd key)
        match conns.TryGetValue k with
        | true, old when not (obj.ReferenceEquals(old, c)) ->
            conns[k] <- c
            old.Socket.Abort()
        | _ -> conns[k] <- c

    member _.Remove(key: string * string, c: Connection) =
        conns.TryRemove(Collections.Generic.KeyValuePair(struct (fst key, snd key), c)) |> ignore

    /// The hub's own in-process agent (node "local"), which has no socket.
    member _.SetLocal(onWake: string -> unit) = local <- onWake

    member _.IsConnected(nodeId: string, name: string) = conns.ContainsKey(struct (nodeId, name))

    /// Best effort: an agent that isn't connected picks the work up with its next timed push.
    member _.Wake(nodeId: string, name: string, reason: string) =
        if nodeId = "local" then local reason else
        match conns.TryGetValue(struct (nodeId, name)) with
        | true, c ->
            task {
                try
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
                    do! c.Send({ message "wake" with Reason = Some reason }, cts.Token)
                with _ ->
                    ()
            }
            |> ignore
        | _ -> ()

/// Hub side of one connection: hello within 5 s, register, welcome, then read until it closes.
/// `status` looks up the machine's approval state for the welcome.
let serve (ws: WebSocket) (registry: Registry) (nodeId: string) (status: string -> Task<string>) (stop: CancellationToken) : Task =
    task {
        let conn = Connection ws
        let mutable key = None
        try
            try
                use hello = CancellationTokenSource.CreateLinkedTokenSource stop
                hello.CancelAfter(TimeSpan.FromSeconds 5.)
                match! receive ws hello.Token with
                | Some text ->
                    match decode text with
                    | Some { Type = "hello"; Machine = Some name } when not (String.IsNullOrWhiteSpace name) && name.Length <= 128 ->
                        let k = nodeId, name
                        key <- Some k
                        registry.Add(k, conn)
                        let! s = status name
                        do! conn.Send({ message "welcome" with Status = Some s }, stop)
                        let mutable open' = true
                        while open' do
                            match! receive ws stop with
                            | None -> open' <- false
                            | Some _ -> () // nothing agent -> hub yet beyond hello; unknown types ignored
                    | _ -> do! ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "expected hello", stop)
                | None -> ()
            with
            | :? OperationCanceledException
            | :? WebSocketException
            | :? InvalidDataException -> ()
        finally
            key |> Option.iter (fun k -> registry.Remove(k, conn))
    }

let private wsUrl (hubUrl: string) =
    let u = UriBuilder(hubUrl + path)
    u.Scheme <- (if u.Scheme = "https" then "wss" else "ws")
    u.Uri

/// Agent side: keep a connection open, reconnecting with the push loop's backoff. A hello is sent
/// on connect; welcome and wake both wake the push loop (welcome covers a hub restart).
/// `onWake` gets the hub's reason: "job" (push now), "refresh" (rescan, then push), "welcome".
let run (hubUrl: string) (name: string) (backoff: int -> TimeSpan) (onWake: string -> unit) (onLinked: bool -> unit) (stop: CancellationToken) : Task =
    task {
        let mutable failures = 0
        let mutable lastState = ""
        let report (state: string) =
            if state <> lastState then
                eprintfn $"hub link: {state}"
                lastState <- state
        while not stop.IsCancellationRequested do
            use ws = new ClientWebSocket()
            ws.Options.KeepAliveInterval <- TimeSpan.FromSeconds 20.
            try
                use connect = CancellationTokenSource.CreateLinkedTokenSource stop
                connect.CancelAfter(TimeSpan.FromSeconds 15.)
                do! ws.ConnectAsync(wsUrl hubUrl, connect.Token)
                let conn = Connection ws
                do! conn.Send({ message "hello" with Machine = Some name }, stop)
                failures <- 0
                let mutable open' = true
                while open' do
                    match! receive ws stop with
                    | None -> open' <- false
                    | Some text ->
                        match decode text with
                        | Some { Type = "welcome"; Status = s } ->
                            report $"""connected ({defaultArg s "unknown"})"""
                            onLinked true
                            onWake "welcome"
                        | Some { Type = "wake"; Reason = r } -> onWake (defaultArg r "job")
                        | _ -> ()
                report "closed by the hub"
                onLinked false
            with
            | :? OperationCanceledException when stop.IsCancellationRequested -> ()
            | ex ->
                failures <- failures + 1
                report $"unavailable ({ex.GetBaseException().Message}); pushes continue on their timer"
                onLinked false
            if not stop.IsCancellationRequested then
                try
                    do! Task.Delay(backoff (max 1 failures), stop)
                with :? OperationCanceledException ->
                    ()
    }
