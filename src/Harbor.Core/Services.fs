module Harbor.Services

open System
open System.Net.Http
open System.Net.Sockets
open System.Threading.Tasks
open Harbor.Config
open Harbor.Domain

let private timeout = TimeSpan.FromSeconds 3.
let private http = new HttpClient(Timeout = timeout)

let describe =
    function
    | Systemd u -> $"systemd {u}"
    | SystemdUser u -> $"systemd --user {u}"
    | Process n -> $"process {n}"
    | Port p -> $"port {p}"
    | Http u -> $"http {u}"
    | Podman c -> $"podman {c}"

let private state running detail = (if running then Running else Stopped), detail

/// `systemctl is-active` prints the state and exits non-zero unless it is "active".
let private systemd args =
    task {
        match! Proc.run timeout None [] ("systemctl" :: args) with
        | Ok o ->
            let s = o.Out.Trim()
            return if s = "active" then state true s elif s = "" then Unknown, o.Err.Trim() else state false s
        | Error e -> return Unknown, e
    }

let private proc (name: string) =
    let n = Diagnostics.Process.GetProcessesByName(name).Length
    Task.FromResult(if n > 0 then state true $"{n} running" else state false "not running")

let private port (p: int) =
    task {
        use client = new TcpClient()
        try
            do! client.ConnectAsync("127.0.0.1", p).WaitAsync timeout
            return state true "listening"
        with _ ->
            return state false "closed"
    }

let private httpCheck (url: string) =
    task {
        try
            use! r = http.GetAsync url
            let code = int r.StatusCode
            return state (code < 500) $"HTTP {code}"
        with ex ->
            return state false (ex.GetBaseException().Message)
    }

let private podman (name: string) =
    task {
        match! Proc.run timeout None [] [ "podman"; "inspect"; "--format"; "{{.State.Status}}"; name ] with
        | Ok o when o.Code = 0 -> let s = o.Out.Trim() in return state (s = "running") s
        | Ok _ -> return state false "no such container"
        | Error e -> return Unknown, e
    }

let private run =
    function
    | Systemd u -> systemd [ "is-active"; u ]
    | SystemdUser u -> systemd [ "--user"; "is-active"; u ]
    | Process n -> proc n
    | Port p -> port p
    | Http u -> httpCheck u
    | Podman c -> podman c

let check (s: ServiceConfig) : Task<ServiceStatus> =
    task {
        let! st, detail = run s.Check
        return { Name = s.Name; Check = describe s.Check; State = st; Detail = detail }
    }

let checkAll (services: ServiceConfig[]) = services |> Array.map check |> Task.WhenAll
