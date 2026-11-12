module Harbor.Proc

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

type Output = { Code: int; Out: string; Err: string }

/// Run argv to completion; Error on launch failure or timeout (the process is killed).
let run (timeout: TimeSpan) (cwd: string option) (env: (string * string) list) (argv: string list) : Task<Result<Output, string>> =
    task {
        match argv with
        | [] -> return Error "empty command"
        | exe :: args ->
            let psi = ProcessStartInfo(exe, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
            args |> List.iter psi.ArgumentList.Add
            cwd |> Option.iter (fun d -> psi.WorkingDirectory <- d)
            env |> List.iter (fun (k, v) -> psi.Environment[k] <- v)
            try
                use p =
                    match Process.Start psi with
                    | null -> failwith "process did not start"
                    | p -> p
                p.StandardInput.Close() // never wait on a prompt
                let out = p.StandardOutput.ReadToEndAsync()
                let err = p.StandardError.ReadToEndAsync()
                use cts = new CancellationTokenSource(timeout)
                try
                    do! p.WaitForExitAsync cts.Token
                    let! o = out
                    let! e = err
                    return Ok { Code = p.ExitCode; Out = o; Err = e }
                with :? OperationCanceledException ->
                    p.Kill true
                    return Error $"{exe} timed out after {timeout.TotalSeconds}s"
            with ex ->
                return Error $"{exe}: {ex.Message}"
    }
