/// One-time pairing codes. Pure: generation, normalisation, hashing and the attempt limiter.
module Harbor.Pairing

open System
open System.Security.Cryptography
open System.Text

/// No 0/O, 1/I/L, U: easy to read aloud and type on a phone keyboard. 30 symbols, 8 of them ~ 39 bits.
let alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789"
let length = 8
let lifetime = TimeSpan.FromMinutes 10.

let generate () =
    String(Array.init length (fun _ -> alphabet[RandomNumberGenerator.GetInt32 alphabet.Length]))

/// "7KQ4M2XP" -> "7KQ4-M2XP"
let display (code: string) = if code.Length = 8 then $"{code.Substring(0, 4)}-{code.Substring 4}" else code

let normalize (code: string) =
    String(code.ToUpperInvariant() |> Seq.filter Char.IsLetterOrDigit |> Array.ofSeq)

let isWellFormed (code: string) = code.Length = length && code |> Seq.forall (fun c -> alphabet.Contains c)

let hash (code: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes(normalize code)) |> Convert.ToHexStringLower

/// Wrong codes per caller (Tailscale node): after `limit` within `window`, codes from that node are
/// ignored until the window passes. Guessing 39 bits within 10 minutes at 5 tries is hopeless anyway.
type Limiter(limit: int, window: TimeSpan) =
    let failures = Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset list>()
    let recent (now: DateTimeOffset) (key: string) =
        match failures.TryGetValue key with
        | true, ts -> ts |> List.filter (fun t -> now - t < window)
        | _ -> []
    member _.Blocked(key: string, now: DateTimeOffset) = (recent now key).Length >= limit
    member _.Fail(key: string, now: DateTimeOffset) = failures[key] <- now :: recent now key
    member _.Clear(key: string) = failures.TryRemove key |> ignore
