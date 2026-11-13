module Harbor.Git

open System
open System.IO
open Harbor.Domain

// No optional locks: a status scan must never block or race the user's own git commands.
// SSH_ASKPASS_REQUIRE=never: with no terminal, ssh would otherwise pop a passphrase dialog on the desktop.
let private env =
    [ "GIT_OPTIONAL_LOCKS", "0"; "GIT_TERMINAL_PROMPT", "0"; "SSH_ASKPASS_REQUIRE", "never"; "LC_ALL", "C" ]
let private timeout = TimeSpan.FromSeconds 20.

let private gitWith (timeout: TimeSpan) repo args =
    task {
        match! Proc.run timeout (Some repo) env ("git" :: args) with
        | Ok o when o.Code = 0 -> return Ok o.Out
        | Ok o -> return Error(o.Err.Trim())
        | Error e -> return Error e
    }

let private git repo args = gitWith timeout repo args

let private mtime (path: string) =
    try
        if Directory.Exists path then Some(DateTimeOffset(Directory.GetLastWriteTimeUtc path))
        elif File.Exists path then Some(DateTimeOffset(File.GetLastWriteTimeUtc path))
        else None
    with _ ->
        None

let private maxStat = 200

/// Newest of: last commit, the index (staging, checkouts), and any changed file.
let lastModified (repo: string) (commit: Commit option) (changes: Changes option) =
    let gitDir = Path.Combine(repo, ".git")
    let changed =
        changes
        |> Option.map (fun c -> c.Paths |> Array.truncate maxStat |> Array.choose (fun p -> mtime (Path.Combine(repo, p))))
        |> Option.defaultValue [||]
    [| yield! Option.toArray (commit |> Option.map _.Date)
       yield! Option.toArray (mtime (Path.Combine(gitDir, "index")))
       yield! changed |]
    |> Array.sortDescending
    |> Array.tryHead

let scan (repo: string) : Threading.Tasks.Task<Repo> =
    task {
        let status = git repo [ "status"; "-z"; "--porcelain=v2"; "--branch" ]
        let remotes = git repo [ "remote"; "-v" ]
        let ident = git repo [ "config"; "--show-scope"; "--get-regexp"; @"^user\.(name|email)$" ]
        let log = git repo [ "log"; "-1"; GitParse.lastCommitFormat ]
        let common = git repo [ "rev-parse"; "--path-format=absolute"; "--git-common-dir" ]
        let branches = git repo [ "for-each-ref"; GitParse.branchesFormat; "refs/heads" ]
        let! status = status
        let! remotes = remotes
        let! ident = ident
        let! log = log
        let! common = common
        let! branches = branches
        let parsed = status |> Result.map GitParse.status |> Result.toOption
        let! behindSince =
            match parsed |> Option.map fst with
            | Some b when b.Behind > 0 && b.Upstream.IsSome ->
                task {
                    match! git repo [ "log"; "--format=%cI"; "HEAD..@{upstream}" ] with
                    | Ok out ->
                        return
                            out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            |> Array.tryLast
                            |> Option.bind (fun d -> match DateTimeOffset.TryParse(d.Trim()) with | true, t -> Some t | _ -> None)
                    | Error _ -> return None
                }
            | _ -> Threading.Tasks.Task.FromResult None
        let changes = parsed |> Option.map snd
        let commit = log |> Result.toOption |> Option.bind GitParse.lastCommit
        return
            { Path = repo
              Name = DirectoryInfo(repo).Name
              Remotes = remotes |> Result.map GitParse.remotes |> Result.defaultValue [||]
              // exit code 1 just means no user.* is set anywhere
              Identity = ident |> Result.defaultValue "" |> GitParse.identity
              Branch = parsed |> Option.map fst
              Changes = changes |> Option.map (fun c -> { c with Paths = Array.truncate 50 c.Paths })
              LastCommit = commit
              LastModified = lastModified repo commit changes
              WorktreeOf = common |> Result.toOption |> Option.bind (GitParse.worktreeOf repo)
              LastFetch = common |> Result.toOption |> Option.bind (fun d -> mtime (Path.Combine(d.Trim(), "FETCH_HEAD")))
              FetchError = None
              Error = match status with Error e -> Some e | Ok _ -> None
              Branches = branches |> Result.toOption |> Option.map GitParse.branches
              BehindSince = behindSince }
    }

/// Commits from the last 30 days (at most 100), newest first; empty for a repo with no commits.
let recentCommits (repo: string) : Threading.Tasks.Task<LoggedCommit[]> =
    task {
        match! git repo [ "log"; "-z"; "--since=30.days"; "-n"; "100"; GitParse.logFormat ] with
        | Ok out -> return GitParse.log out
        | Error _ -> return [||]
    }

/// `git merge --ff-only @{upstream}`: moves the checked-out branch forward to its upstream, or
/// fails without touching anything when that isn't a fast-forward (or would overwrite files).
let fastForward (repo: string) : Threading.Tasks.Task<Result<unit, string>> =
    task {
        match! gitWith (TimeSpan.FromSeconds 60.) repo [ "merge"; "--ff-only"; "--quiet"; "@{upstream}" ] with
        | Ok _ -> return Ok()
        | Error e -> return Error(if e = "" then "git merge --ff-only failed" else e)
    }

/// `git fetch --all --prune`: updates remote-tracking refs only, never the working tree or branches.
/// stdin is closed and prompts are off, so a remote needing credentials fails instead of hanging.
let fetch (repo: string) : Threading.Tasks.Task<Result<unit, string>> =
    task {
        match! gitWith (TimeSpan.FromSeconds 120.) repo [ "fetch"; "--all"; "--prune"; "--quiet" ] with
        | Ok _ -> return Ok()
        | Error e -> return Error(if e = "" then "git fetch failed" else e)
    }
