/// Runs harbor-agent as a hidden child of the tray (Windows: a console program started at login
/// would leave a terminal window open; idle time also needs the user's session). Restarts it if
/// it exits; stops it when the tray quits. On Linux prefer the systemd unit instead.
module Harbor.Tray.Supervisor

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Harbor

type Agent(exe: string, config: string option) =
    let stop = new CancellationTokenSource()
    let mutable current: Process option = None
    let log = Path.Combine(AgentStatus.directory (), "agent.log")

    let start () =
        let psi = ProcessStartInfo(exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true)
        psi.ArgumentList.Add "run"
        config |> Option.iter (fun c -> psi.ArgumentList.Add "--config"; psi.ArgumentList.Add c)
        Directory.CreateDirectory(Path.GetDirectoryName log |> nonNull) |> ignore
        let p = Process.Start psi |> nonNull
        // keep the last run's output for troubleshooting (overwritten on each start)
        let writer = new StreamWriter(log, false, AutoFlush = true)
        p.OutputDataReceived.Add(fun e -> match e.Data with null -> () | l -> lock writer (fun () -> writer.WriteLine l))
        p.ErrorDataReceived.Add(fun e -> match e.Data with null -> () | l -> lock writer (fun () -> writer.WriteLine l))
        p.BeginOutputReadLine()
        p.BeginErrorReadLine()
        p.Exited.Add(fun _ -> writer.Dispose())
        p.EnableRaisingEvents <- true
        current <- Some p
        p

    member _.Log = log

    /// Keep the agent running until Stop; back off 10 s between restarts (1 s after a config change).
    member _.Run() : Task =
        task {
            while not stop.IsCancellationRequested do
                let mutable wait = 10.
                try
                    let p = start ()
                    do! p.WaitForExitAsync stop.Token
                    // 75: the agent saw its config change and wants a quick restart
                    if p.ExitCode = 75 then wait <- 1.
                    eprintfn $"harbor-agent exited ({p.ExitCode}); restarting in {wait} s"
                with
                | :? OperationCanceledException -> ()
                | ex -> eprintfn $"harbor-agent: {ex.Message}"
                try
                    do! Task.Delay(TimeSpan.FromSeconds wait, stop.Token)
                with :? OperationCanceledException ->
                    ()
        }

    member _.Stop() =
        stop.Cancel()
        current |> Option.iter (fun p -> try (if not p.HasExited then p.Kill true) with _ -> ())
