/// Which build this is: the git commit the SDK stamps into the informational version ("1.0.0+<sha>").
module Harbor.BuildInfo

open System.Reflection

let private informational () =
    let asm = Assembly.GetEntryAssembly() |> Option.ofObj |> Option.defaultValue (Assembly.GetExecutingAssembly())
    asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>() |> Option.ofObj |> Option.map _.InformationalVersion

/// Full commit sha of this build, when it was built from a git checkout.
let commit: string option =
    informational ()
    |> Option.bind (fun v ->
        match v.IndexOf '+' with
        | i when i >= 0 && v.Length - i > 7 -> Some(v.Substring(i + 1))
        | _ -> None)

let short = commit |> Option.map (fun c -> c.Substring(0, min 7 c.Length)) |> Option.defaultValue "unknown"
