module Harbor.Tests.DisplayProxyTests

open Xunit
open Harbor
open Harbor.Domain

let private display url = Some [| { Kind = "display"; Name = None; Url = Some url; Host = None; Port = None } |]
let private host = Some "devbox.example.ts.net"

[<Fact>]
let ``forwards only to the machine's own tailnet name over https`` () =
    Assert.Equal(Ok(System.Uri "https://devbox.example.ts.net"), DisplayProxy.upstream host (display "https://devbox.example.ts.net"))
    Assert.Equal(Ok(System.Uri "https://devbox.example.ts.net:8443"), DisplayProxy.upstream host (display "https://DEVBOX.example.ts.net:8443/"))
    for bad in [ "https://127.0.0.1:7420"; "https://localhost"; "https://192.168.50.10"; "https://other.example.ts.net"
                 "http://devbox.example.ts.net"; "https://user@devbox.example.ts.net"; "https://devbox.example.ts.net/x"
                 "https://devbox.example.ts.net/?a=1"; "https://devbox.example.ts.net.evil.test"; "not a url" ] do
        Assert.True(Result.isError (DisplayProxy.upstream host (display bad)), bad)

[<Fact>]
let ``nothing to forward without a display entry or a known tailnet name`` () =
    Assert.True(Result.isError (DisplayProxy.upstream host None))
    Assert.True(Result.isError (DisplayProxy.upstream host (Some [| { Kind = "rdp"; Name = None; Url = None; Host = Some "devbox"; Port = None } |])))
    Assert.True(Result.isError (DisplayProxy.upstream None (display "https://devbox.example.ts.net")))

[<Fact>]
let ``machine segments are plain lowercase names`` () =
    for ok in [ "devbox"; "devbox-2"; "pi4" ] do Assert.True(DisplayProxy.isMachineSegment ok, ok)
    for bad in [ ""; "Devbox"; "-x"; "a.b"; "a/b"; ".."; "%2e" ] do Assert.False(DisplayProxy.isMachineSegment bad, bad)
