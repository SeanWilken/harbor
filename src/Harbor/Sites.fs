/// Websites checked from outside (hub.sites): is it up, how fast, when does its certificate expire,
/// which version is it serving. The hub needs no credentials and nothing installed on the host.
module Harbor.Sites

open System
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Harbor.Config

type Check =
    { Site: string
      At: DateTimeOffset
      /// Answered below 400 (after up to 5 redirects) with a certificate the system trusts.
      Ok: bool
      Status: int option
      Ms: int option
      Error: string option
      CertExpires: DateTimeOffset option
      CertIssuer: string option
      /// From the health endpoint, when the site has one.
      Version: string option }

let private versionKeys = [| "version"; "commit"; "sha"; "build"; "release"; "revision" |]

/// A health body's version: a top-level version/commit/sha/... field of a JSON object, else a short
/// plain-text body (a bare "1.4.2" or commit id). Anything longer isn't a version.
let versionOf (body: string) : string option =
    let text = body.Trim()
    let shortText (s: string) = if s.Length > 0 && s.Length <= 64 && not (s.Contains '\n') && not (s.StartsWith "<") then Some s else None
    if text.StartsWith "{" then
        try
            use doc = JsonDocument.Parse text
            versionKeys
            |> Array.tryPick (fun k ->
                doc.RootElement.EnumerateObject()
                |> Seq.tryFind (fun p -> p.Name.Equals(k, StringComparison.OrdinalIgnoreCase))
                |> Option.bind (fun p ->
                    match p.Value.ValueKind with
                    | JsonValueKind.String -> p.Value.GetString() |> Option.ofObj
                    | JsonValueKind.Number -> Some(p.Value.GetRawText())
                    | _ -> None))
            |> Option.bind shortText
        with _ ->
            None
    else
        shortText text

/// The innermost message, without .NET's exception type names.
let private reason (ex: exn) =
    match ex with
    | :? TaskCanceledException | :? OperationCanceledException -> "timed out"
    | _ -> ex.GetBaseException().Message

/// A fresh handler per check so the TLS handshake (and its certificate) is seen every time.
let private client (seen: X509Certificate2 option ref) (rejected: SslPolicyErrors option ref) =
    let handler =
        new SocketsHttpHandler(
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds 10.)
    handler.SslOptions.RemoteCertificateValidationCallback <-
        RemoteCertificateValidationCallback(fun _ cert _ errors ->
            match cert with
            | null -> ()
            | c -> seen.Value <- Some(new X509Certificate2(c))
            if errors <> SslPolicyErrors.None then rejected.Value <- Some errors
            errors = SslPolicyErrors.None)
    let http = new HttpClient(handler, Timeout = TimeSpan.FromSeconds 20.)
    http.DefaultRequestHeaders.UserAgent.ParseAdd "harbor-site-check/1"
    http

let private healthVersion (http: HttpClient) (url: string) =
    task {
        try
            use! r = http.GetAsync url
            if not r.IsSuccessStatusCode then return None
            else
                let! body = r.Content.ReadAsStringAsync()
                return versionOf (if body.Length > 65536 then body.Substring(0, 65536) else body)
        with _ ->
            return None
    }

let check (site: SiteConfig) : Task<Check> =
    task {
        let seen = ref None
        let rejected = ref None
        use http = client seen rejected
        let at = DateTimeOffset.Now
        let sw = Diagnostics.Stopwatch.StartNew()
        let! result =
            task {
                try
                    use! r = http.GetAsync(site.Url, HttpCompletionOption.ResponseHeadersRead)
                    return Ok(int r.StatusCode, int sw.ElapsedMilliseconds)
                with ex ->
                    match rejected.Value with
                    | Some errors -> return Error $"certificate not trusted ({errors})"
                    | None -> return Error(reason ex)
            }
        let! version = match site.Health with Some h -> healthVersion http h | None -> Task.FromResult None
        let cert = seen.Value
        return
            { Site = site.Name
              At = at
              Ok = (match result with Ok(code, _) -> code < 400 | Error _ -> false)
              Status = (match result with Ok(code, _) -> Some code | Error _ -> None)
              Ms = (match result with Ok(_, ms) -> Some ms | Error _ -> None)
              Error = (match result with Ok(code, _) when code >= 400 -> Some $"HTTP {code}" | Ok _ -> None | Error e -> Some e)
              CertExpires = cert |> Option.map (fun c -> DateTimeOffset(c.NotAfter))
              CertIssuer = cert |> Option.map (fun c -> c.GetNameInfo(X509NameType.SimpleName, true))
              Version = version }
    }

/// GET /api/sites row: the latest check and the last day at a glance.
type Point = { At: DateTimeOffset; Ok: bool; Ms: int option }

type Summary =
    { Name: string
      Url: string
      Provider: string option
      EveryMinutes: int
      Last: Check option
      /// Share of the last 24 hours' checks that were up; None before the first check.
      Uptime: float option
      AvgMs: int option
      /// When the site last went up or down within the window; None = steady all window.
      Since: DateTimeOffset option
      CertDays: int option
      Points: Point[] }

/// `checks`: this site's checks over the window, oldest first.
let summarize (now: DateTimeOffset) (site: SiteConfig) (checks: Check[]) : Summary =
    let last = Array.tryLast checks
    let times = checks |> Array.choose _.Ms
    let since =
        checks
        |> Array.pairwise
        |> Array.filter (fun (a, b) -> a.Ok <> b.Ok)
        |> Array.tryLast
        |> Option.map (fun (_, b) -> b.At)
    { Name = site.Name
      Url = site.Url
      Provider = site.Provider
      EveryMinutes = site.EveryMinutes
      Last = last
      Uptime = (if checks.Length = 0 then None else Some(Math.Round(100. * float (checks |> Array.filter _.Ok).Length / float checks.Length, 2)))
      AvgMs = (if times.Length = 0 then None else Some(int (Array.averageBy float times)))
      Since = since
      CertDays = last |> Option.bind _.CertExpires |> Option.map (fun e -> int (floor (e - now).TotalDays))
      Points = checks |> Array.map (fun c -> { At = c.At; Ok = c.Ok; Ms = c.Ms }) |> Array.skip (max 0 (checks.Length - 96)) }

/// Runs each site on its own cadence; Now() runs all of them at once (the UI's "check now").
type Checker(sites: SiteConfig[], save: Check -> Task<unit>) =
    let mutable due = sites |> Array.map (fun s -> s.Name, DateTimeOffset.MinValue) |> Map.ofArray
    let gate = new SemaphoreSlim(1, 1)
    let runAll (pick: SiteConfig -> bool) =
        task {
            do! gate.WaitAsync()
            try
                let chosen = sites |> Array.filter pick
                let! results = chosen |> Array.map check |> Task.WhenAll
                for c in results do
                    try do! save c with ex -> eprintfn $"sites: saving {c.Site}: {ex.Message}"
                for s in chosen do
                    due <- due.Add(s.Name, DateTimeOffset.Now.AddMinutes(float s.EveryMinutes))
            finally
                gate.Release() |> ignore
        }
    member _.Sites = sites
    member _.Now() = runAll (fun _ -> true)
    member _.Loop(stop: CancellationToken) : Task =
        task {
            try
                while not stop.IsCancellationRequested do
                    let now = DateTimeOffset.Now
                    try do! runAll (fun s -> due[s.Name] <= now)
                    with ex -> eprintfn $"sites: {ex.Message}"
                    do! Task.Delay(TimeSpan.FromSeconds 20., stop)
            with :? OperationCanceledException ->
                ()
        }
