/// Pure parsers for git's machine-readable output; Git.fs runs the commands.
module Harbor.GitParse

open System
open Harbor.Domain

let private splitN (sep: char) (n: int) (s: string) = s.Split([| sep |], n)
let private lines (s: string) = s.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)

let private emptyBranch =
    { Head = None; Oid = None; Upstream = None; Ahead = 0; Behind = 0 }

let private emptyChanges =
    { Staged = 0; Unstaged = 0; Untracked = 0; Conflicted = 0; Paths = [||] }

let private branchHeader (b: Branch) (line: string) =
    match splitN ' ' 3 line with
    | [| _; "branch.oid"; "(initial)" |] -> b
    | [| _; "branch.oid"; oid |] -> { b with Oid = Some oid }
    | [| _; "branch.head"; "(detached)" |] -> b
    | [| _; "branch.head"; head |] -> { b with Head = Some head }
    | [| _; "branch.upstream"; up |] -> { b with Upstream = Some up }
    | [| _; "branch.ab"; ab |] ->
        match ab.Split ' ' with
        | [| a; c |] -> { b with Ahead = int (a.TrimStart '+'); Behind = int (c.TrimStart '-') }
        | _ -> b
    | _ -> b

/// Path is the last field; `fields` is how many space-separated fields the record has.
let private pathOf fields (record: string) =
    let parts = splitN ' ' fields record
    if parts.Length = fields then Some parts[fields - 1] else None

let private tracked (c: Changes) (xy: string) path =
    { c with
        Staged = c.Staged + (if xy[0] <> '.' then 1 else 0)
        Unstaged = c.Unstaged + (if xy[1] <> '.' then 1 else 0)
        Paths = Array.append c.Paths (Option.toArray path) }

/// `git status -z --porcelain=v2 --branch`
let status (out: string) : Branch * Changes =
    let tokens = out.Split('\000', StringSplitOptions.RemoveEmptyEntries)
    let rec go i (b: Branch) (c: Changes) =
        if i >= tokens.Length then
            b, c
        else
            let t = tokens[i]
            match t[0] with
            | '#' -> go (i + 1) (branchHeader b t) c
            | '1' -> go (i + 1) b (tracked c (t.Substring(2, 2)) (pathOf 9 t))
            | '2' -> go (i + 2) b (tracked c (t.Substring(2, 2)) (pathOf 10 t)) // next token is the original path
            | 'u' ->
                go (i + 1) b { c with Conflicted = c.Conflicted + 1; Paths = Array.append c.Paths (Option.toArray (pathOf 11 t)) }
            | '?' -> go (i + 1) b { c with Untracked = c.Untracked + 1; Paths = Array.append c.Paths [| t.Substring 2 |] }
            | _ -> go (i + 1) b c
    go 0 emptyBranch emptyChanges

/// Credentials never leave the machine: `https://user:token@host/x` and `https://token@host/x`
/// become `https://host/x`; `ssh://git:pw@host` keeps its user. scp-style `git@host:x` has none.
let scrubUrl (url: string) =
    let m = Text.RegularExpressions.Regex.Match(url, @"^([a-zA-Z][\w+.-]*://)([^/@]*)@(.*)$")
    if not m.Success then url
    else
        let scheme, userinfo, rest = m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value
        if scheme.StartsWith "http" then scheme + rest
        else scheme + userinfo.Split(':')[0] + "@" + rest

/// `git remote -v`: fetch URLs only, credentials removed.
let remotes (out: string) : Remote[] =
    lines out
    |> Array.choose (fun line ->
        match line.Split('\t') with
        | [| name; rest |] when rest.EndsWith " (fetch)" -> Some { Name = name; Url = scrubUrl rest[.. rest.Length - 9] }
        | _ -> None)

/// `git config --show-scope --get-regexp ^user\.(name|email)$`: later lines override earlier ones.
let identity (out: string) : Identity =
    lines out
    |> Array.fold
        (fun (id: Identity) line ->
            match splitN '\t' 2 line with
            | [| scope; kv |] ->
                match splitN ' ' 2 kv with
                | [| "user.name"; v |] -> { id with Name = Some v }
                | [| "user.email"; v |] -> { id with Email = Some v; Scope = Some scope }
                | _ -> id
            | _ -> id)
        { Name = None; Email = None; Scope = None }

let lastCommitFormat = "--format=%cI%x00%an%x00%s"

/// `git log -1` with lastCommitFormat.
let lastCommit (out: string) : Commit option =
    match out.TrimEnd('\n').Split('\000') with
    | [| date; author; subject |] ->
        match DateTimeOffset.TryParse date with
        | true, d -> Some { Date = d; Author = author; Subject = subject }
        | _ -> None
    | _ -> None

let logFormat = "--format=%H%x1f%aI%x1f%an%x1f%s"

/// `git log -z` with logFormat: NUL between commits, US between fields.
let log (out: string) : LoggedCommit[] =
    out.Split('\000', StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun record ->
        match record.TrimStart('\n').Split('\031') with
        | [| sha; date; author; subject |] ->
            match DateTimeOffset.TryParse date with
            | true, d -> Some { Sha = sha; Date = d; Author = author; Subject = subject }
            | _ -> None
        | _ -> None)

/// Host + path of a remote URL, so clones of one repo match across machines:
/// `git@github.com:Me/X.git`, `https://me@github.com/me/x/` and `ssh://git@github.com:22/me/x`
/// all give `github.com/me/x`. Local-path remotes give None: they're machine-specific.
let remoteKey (url: string) : string option =
    let u = url.Trim()
    let hostPath =
        if u.Contains "://" then
            match Uri.TryCreate(u, UriKind.Absolute) with
            | true, NonNull uri when not uri.IsFile && uri.Host <> "" -> Some(uri.Host, Uri.UnescapeDataString uri.AbsolutePath)
            | _ -> None
        else
            // scp-like [user@]host:path; i > 1 skips Windows drive letters (C:\...)
            match u.IndexOf ':' with
            | i when i > 1 && not (u.Substring(0, i).Contains '/') ->
                let host = u.Substring(0, i)
                Some(host.Substring(host.IndexOf '@' + 1), u.Substring(i + 1))
            | _ -> None
    hostPath
    |> Option.map (fun (host, path) ->
        let path = path.Trim('/')
        let path = if path.EndsWith ".git" then path[.. path.Length - 5] else path
        $"{host}/{path}".ToLowerInvariant())

/// origin if there is one, else the first remote.
let primaryRemote (remotes: Remote[]) =
    remotes |> Array.tryFind (fun r -> r.Name = "origin") |> Option.orElse (Array.tryHead remotes)

/// Main working tree of a linked worktree, from `git rev-parse --path-format=absolute --git-common-dir`.
/// A normal repo's common dir is its own `.git`; a submodule's is under `.git/modules`, so neither
/// counts.
let worktreeOf (repo: string) (commonDir: string) : string option =
    let commonDir = commonDir.Trim().TrimEnd('/', '\\')
    let name = IO.Path.GetFileName commonDir
    match IO.Path.GetDirectoryName commonDir with
    | null -> None
    | main when name = ".git" && main.TrimEnd('/', '\\') <> repo.TrimEnd('/', '\\') -> Some main
    | _ -> None

let branchesFormat = "--format=%(refname:short)%1f%(upstream:short)%1f%(upstream:track,nobracket)%1f%(committerdate:iso-strict)"

/// `ahead 3, behind 2` | `ahead 1` | `gone` | empty, from %(upstream:track,nobracket).
let private track (s: string) =
    let count (word: string) =
        s.Split(',', StringSplitOptions.TrimEntries)
        |> Array.tryPick (fun p -> if p.StartsWith(word + " ") then Some(int (p.Substring(word.Length + 1))) else None)
        |> Option.defaultValue 0
    count "ahead", count "behind", s.Trim() = "gone"

/// `git for-each-ref refs/heads` with branchesFormat: one branch per line, unit-separated fields.
let branches (out: string) : LocalBranch[] =
    lines out
    |> Array.choose (fun line ->
        match line.Split('\031') with
        | [| name; upstream; tr; date |] ->
            let ahead, behind, gone = track tr
            Some
                { Name = name
                  Upstream = if upstream = "" then None else Some upstream
                  Ahead = ahead
                  Behind = behind
                  Gone = gone
                  Date =
                    match DateTimeOffset.TryParse date with
                    | true, d -> Some d
                    | _ -> None }
        | _ -> None)

let unpushedFormat = "--format=%H%x1f%aI%x1f%S%x1f%s"

/// `git log -z --source --branches --not --remotes` with unpushedFormat.
let unpushed (out: string) : UnpushedCommit[] =
    out.Split('\000', StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun record ->
        match record.TrimStart('\n').Split('\031') with
        | [| sha; date; source; subject |] ->
            match DateTimeOffset.TryParse date with
            | true, d ->
                let branch = if source.StartsWith "refs/heads/" then source.Substring 11 else source
                Some { Sha = sha; Date = d; Subject = subject; Branch = branch }
            | _ -> None
        | _ -> None)

/// A commit id a job may name: 7–40 lowercase hex, nothing git could read as an option or range.
let isSha (s: string) = Text.RegularExpressions.Regex.IsMatch(s, "^[0-9a-f]{7,40}$")
