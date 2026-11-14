module Harbor.Tests.SitesTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Xunit
open Harbor

let private loadHub (sites: string) =
    let path = Path.Combine(Path.GetTempPath(), $"harbor-sites-{Guid.NewGuid():N}.json")
    File.WriteAllText(path, $$"""{ "hub": { "database": "Host=x", "sites": {{sites}} } }""")
    try Config.load (Some path) |> Result.map (fun (c, _) -> c.Hub.Value.Sites) finally File.Delete path

[<Fact>]
let ``sites load with defaults and a health path resolved against the url`` () =
    match loadHub """[ { "name": "shop", "url": "https://shop.example.com", "provider": "digitalocean", "health": "/healthz" } ]""" with
    | Ok [| s |] ->
        Assert.Equal("https://shop.example.com/healthz", s.Health.Value)
        Assert.Equal(5, s.EveryMinutes)
        Assert.Equal(Some "digitalocean", s.Provider)
    | other -> failwithf "%A" other

[<Theory>]
[<InlineData("""[ { "name": "a", "url": "ftp://example.com" } ]""")>]
[<InlineData("""[ { "name": "a", "url": "https://user:pw@example.com" } ]""")>]
[<InlineData("""[ { "name": "a", "url": "https://a.example.com" }, { "name": "a", "url": "https://b.example.com" } ]""")>]
[<InlineData("""[ { "name": " ", "url": "https://a.example.com" } ]""")>]
let ``bad sites fail the config load`` (sites: string) =
    Assert.True(Result.isError (loadHub sites))

[<Theory>]
[<InlineData("""{"status":"ok","version":"1.4.2"}""", "1.4.2")>]
[<InlineData("""{"Commit":"0123abcd","uptime":5}""", "0123abcd")>]
[<InlineData("""{"build":42}""", "42")>]
[<InlineData("v2.0.1\n", "v2.0.1")>]
let ``a version is read from a health body`` (body: string, expected: string) =
    Assert.Equal(Some expected, Sites.versionOf body)

[<Theory>]
[<InlineData("""{"status":"ok"}""")>]
[<InlineData("<!doctype html><html>...</html>")>]
[<InlineData("")>]
let ``no version when the body doesn't name one`` (body: string) =
    Assert.Equal(None, Sites.versionOf body)

let private site url : Config.SiteConfig = { Name = "s"; Url = url; Provider = None; Health = None; EveryMinutes = 5 }

[<Fact>]
let ``a day's checks summarise to uptime, average time and the last change`` () =
    let now = DateTimeOffset.Now
    let c mins ok ms : Sites.Check =
        { Site = "s"; At = now.AddMinutes(float -mins); Ok = ok; Status = Some(if ok then 200 else 502); Ms = ms; Error = None
          CertExpires = Some(now.AddDays 20.5); CertIssuer = None; Version = None }
    let s = Sites.summarize now (site "https://s.example.com") [| c 20 true (Some 100); c 15 false (Some 300); c 10 true (Some 200); c 5 true (Some 200) |]
    Assert.Equal(Some 75., s.Uptime)
    Assert.Equal(Some 200, s.AvgMs)
    Assert.Equal(Some(now.AddMinutes -10.), s.Since)
    Assert.Equal(Some 20, s.CertDays)
    Assert.Equal(None, (Sites.summarize now (site "x") [||]).Uptime)

/// A tiny HTTP server on a free port that answers every request with `status`.
let private serve (status: int) (body: string) =
    let port =
        let l = new TcpListener(IPAddress.Loopback, 0)
        l.Start()
        let p = (l.LocalEndpoint :?> IPEndPoint).Port
        l.Stop()
        p
    let listener = new HttpListener()
    listener.Prefixes.Add $"http://127.0.0.1:{port}/"
    listener.Start()
    task {
        while listener.IsListening do
            try
                let! ctx = listener.GetContextAsync()
                ctx.Response.StatusCode <- status
                let bytes = Encoding.UTF8.GetBytes(body: string)
                do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
                ctx.Response.Close()
            with _ -> ()
    } |> ignore
    listener, $"http://127.0.0.1:{port}/"

[<Fact>]
let ``a live check records status, time and the health version`` () =
    let listener, url = serve 200 """{"version":"3.1.0"}"""
    try
        let c = (Sites.check { site url with Health = Some(url + "health") }).Result
        Assert.True(c.Ok, string c.Error)
        Assert.Equal(Some 200, c.Status)
        Assert.True(c.Ms.IsSome)
        Assert.Equal(Some "3.1.0", c.Version)
        Assert.Equal(None, c.CertExpires) // plain http: no certificate
    finally
        listener.Stop()

[<Fact>]
let ``server errors and unreachable sites are down, with a reason`` () =
    let listener, url = serve 502 "bad gateway"
    try
        let c = (Sites.check (site url)).Result
        Assert.False c.Ok
        Assert.Equal(Some "HTTP 502", c.Error)
    finally
        listener.Stop()
    let gone = (Sites.check (site url)).Result // nothing listens there any more
    Assert.False gone.Ok
    Assert.Equal(None, gone.Status)
    Assert.True(gone.Error.IsSome)
