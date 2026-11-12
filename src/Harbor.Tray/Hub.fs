/// The tray's view of the hub, if one runs on this machine (loopback = admin, so it can pair devices).
module Harbor.Tray.Hub

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Threading.Tasks
open Harbor

let loopback = "http://127.0.0.1:7420"

let private http = new HttpClient(Timeout = TimeSpan.FromSeconds 3.)

type Fleet = { Online: int; Stale: int; Pending: int; Total: int }

type private Summary =
    { Name: string
      Status: string
      LastPushAt: DateTimeOffset option
      PushSeconds: int option
      Connected: bool }

/// Same rule as the UI: online when linked, or pushed within ~3 intervals.
let private online (now: DateTimeOffset) (m: Summary) =
    let window = TimeSpan.FromSeconds(float (max 90 (3 * defaultArg m.PushSeconds 60)))
    m.Connected || (m.LastPushAt |> Option.exists (fun t -> now - t < window))

/// None when no hub answers on loopback.
let fleet () : Task<Fleet option> =
    task {
        try
            let! ms = http.GetFromJsonAsync<Summary[]>($"{loopback}/api/machines", Config.jsonOptions)
            match ms with
            | null -> return None
            | ms ->
                let now = DateTimeOffset.Now
                let approved = ms |> Array.filter (fun m -> m.Status = "approved")
                let up = approved |> Array.filter (online now) |> Array.length
                return
                    Some
                        { Online = up
                          Stale = approved.Length - up
                          Pending = ms |> Array.filter (fun m -> m.Status = "pending") |> Array.length
                          Total = approved.Length }
        with _ ->
            return None
    }

type Code = { Code: string; ExpiresAt: DateTimeOffset; Minutes: int }

let createCode () : Task<Result<Code, string>> =
    task {
        try
            use! r = http.PostAsync($"{loopback}/api/pairing", null)
            let! body = r.Content.ReadAsStringAsync()
            if r.IsSuccessStatusCode then
                match JsonSerializer.Deserialize<Code>(body, Config.jsonOptions) with
                | null -> return Error "empty reply from the hub"
                | c -> return Ok c
            else
                return Error $"hub answered {int r.StatusCode}: {body}"
        with ex ->
            return Error ex.Message
    }

type private CodeState = { ExpiresAt: DateTimeOffset; UsedBy: string option }

/// The machine that used the code, once one has.
let usedBy (code: string) : Task<string option> =
    task {
        try
            let! s = http.GetFromJsonAsync<CodeState>($"{loopback}/api/pairing/{Uri.EscapeDataString code}", Config.jsonOptions)
            return (match s with null -> None | s -> s.UsedBy)
        with _ ->
            return None
    }

/// Where agents should push, as the hub reports it.
let agentUrl () : Task<string option> =
    task {
        try
            use! r = http.GetAsync($"{loopback}/api/me")
            let! body = r.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse body
            match doc.RootElement.TryGetProperty "agentUrl" with
            | true, v when v.ValueKind = JsonValueKind.String -> return v.GetString() |> Option.ofObj
            | _ -> return None
        with _ ->
            return None
    }
