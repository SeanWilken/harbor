/// Find git working trees under the configured roots without following symlinks.
module Harbor.Discovery

open System.IO

let private isRepo (dir: string) =
    let marker = Path.Combine(dir, ".git")
    Directory.Exists marker || File.Exists marker

let private children (dir: string) =
    try
        DirectoryInfo(dir).EnumerateDirectories()
        |> Seq.filter (fun d -> not (d.Attributes.HasFlag FileAttributes.ReparsePoint))
        |> Seq.toArray
    with
    | :? System.UnauthorizedAccessException
    | :? IOException -> [||]

/// Dot-folders below a root (~/.oh-my-zsh, ~/.codex/...) are tool state, not projects. A root
/// itself is always scanned, so a dotfiles repo can still be listed by naming it as a root.
let private skip (ignore: Set<string>) (skipHidden: bool) (name: string) =
    ignore.Contains name || (skipHidden && name.StartsWith ".")

/// Repos are leaves: nested repos (submodules, vendored clones) are not descended into.
let rec private walk (skipped: string -> bool) depth (dir: string) : string seq =
    seq {
        if isRepo dir then
            yield dir
        elif depth > 0 then
            for child in children dir do
                if not (skipped child.Name) then
                    yield! walk skipped (depth - 1) child.FullName
    }

let find (ignore: string[]) (skipHidden: bool) (maxDepth: int) (roots: string[]) : string[] =
    let skipped = skip (Set.ofArray ignore) skipHidden
    roots
    |> Seq.filter Directory.Exists
    |> Seq.collect (walk skipped maxDepth)
    |> Seq.distinct
    |> Seq.sort
    |> Seq.toArray
