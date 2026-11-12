/// The tray's two small windows, built in code (no XAML) in the harbor look.
module Harbor.Tray.Windows

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Harbor

let private brush (hex: string) = SolidColorBrush(Color.Parse hex)
let private page, fg, dim, accent, warn = brush "#111616", brush "#e9ede7", brush "#8f9c93", brush "#bed6a7", brush "#e0c084"
let private mono = FontFamily "JetBrains Mono,DejaVu Sans Mono,Consolas,monospace"

let private text (s: string) (size: float) (b: IBrush) =
    TextBlock(Text = s, FontSize = size, Foreground = b, TextWrapping = TextWrapping.Wrap)

let private label (s: string) =
    TextBlock(Text = s.ToUpperInvariant(), FontSize = 10., Foreground = dim, FontFamily = mono, LetterSpacing = 1.5)

let private window (title: string) (content: Control) =
    Window(
        Title = title,
        Width = 440.,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        Background = page,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Icon = Icons.icon Icons.Green,
        Content = Border(Padding = Thickness 24., Background = page, Child = content))

let private stack (children: Control list) =
    let s = StackPanel(Spacing = 12.)
    for c in children do s.Children.Add c
    s

/// Hub: a fresh one-time code, the command for the new machine, and "Paired: <name>" once used.
let pairDevice () : Window =
    let code = text "…" 40. fg
    code.FontFamily <- mono
    code.HorizontalAlignment <- HorizontalAlignment.Center
    code.LetterSpacing <- 4.
    let ttl = text "" 11. dim
    ttl.HorizontalAlignment <- HorizontalAlignment.Center
    let command = TextBox(IsReadOnly = true, FontFamily = mono, FontSize = 12., Foreground = fg, Background = brush "#0e1412", Text = "")
    let note = text "The machine is approved the moment its agent presents the code. It is still identified by its Tailscale node." 12. dim
    let again = Button(Content = "New code", HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false)
    let body =
        stack [ label "pair a device"; code; ttl; text "On the new machine, with harbor-agent installed:" 13. fg; command
                text "then start or restart the agent. On a desktop, the tray's \"Pair with hub…\" does the same." 12. dim
                note; again ]
    let w = window "harbor: pair a device" body
    let mutable current: Hub.Code option = None
    let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 1.)
    let mutable polls = 0
    let fresh () =
        again.IsVisible <- false
        code.Text <- "…"
        code.Foreground <- fg
        task {
            let! url = Hub.agentUrl ()
            match! Hub.createCode () with
            | Ok c ->
                current <- Some c
                code.Text <- c.Code
                command.Text <- $"""harbor-agent pair {c.Code} --hub {defaultArg url "http://<hub>:7420"}"""
                timer.Start()
            | Error e ->
                code.Text <- "—"
                ttl.Text <- $"no hub on this machine answered: {e}"
        }
        |> ignore
    timer.Tick.Add(fun _ ->
        match current with
        | None -> ()
        | Some c ->
            let left = c.ExpiresAt - DateTimeOffset.UtcNow
            if left <= TimeSpan.Zero then
                timer.Stop()
                ttl.Text <- "expired"
                code.Foreground <- dim
                again.IsVisible <- true
            else
                ttl.Text <- $"one use · expires in {int left.TotalMinutes}:{left.Seconds:D2}"
                polls <- polls + 1
                if polls % 2 = 0 then
                    task {
                        match! Hub.usedBy c.Code with
                        | Some name ->
                            timer.Stop()
                            code.Text <- "paired"
                            code.Foreground <- accent
                            ttl.Text <- $"{name} is approved and reporting"
                            again.Content <- "Pair another"
                            again.IsVisible <- true
                        | None -> ()
                    }
                    |> ignore)
    again.Click.Add(fun _ -> fresh ())
    w.Closed.Add(fun _ -> timer.Stop())
    fresh ()
    w

/// Agent: enter the code (and the hub, on a first install); the running agent sends it.
let pairWithHub (machine: string) (hub: string option) : Window =
    let codeBox = TextBox(Watermark = "7KQ4-M2XP", FontFamily = mono, FontSize = 22., MaxLength = 12)
    let hubBox = TextBox(Watermark = "http://<hub>:7420", FontFamily = mono, FontSize = 12., Text = defaultArg hub "")
    let result = text "" 12. dim
    let pair = Button(Content = "Pair", HorizontalAlignment = HorizontalAlignment.Right)
    let body =
        stack [ label "pair with hub"
                text $"This machine: {machine}. Get a code from the hub's tray (\"Pair a device…\") or its web UI (Pending → pair a device)." 13. fg
                codeBox
                label "hub"
                hubBox
                result
                pair ]
    let w = window "harbor: pair with hub" body
    pair.Click.Add(fun _ ->
        let code = AgentStatus.normalizeCode (defaultArg (Option.ofObj codeBox.Text) "")
        if code.Length <> 8 then
            result.Text <- "That isn't an 8-character code."
            result.Foreground <- warn
        else
            AgentStatus.savePairCode machine code
            let typedHub = (defaultArg (Option.ofObj hubBox.Text) "").Trim()
            let hubNote =
                match hub with
                | Some h when typedHub <> "" && typedHub.TrimEnd '/' <> h -> $" Note: the agent's config points at {h}; change agent.hub there to {typedHub}."
                | None when typedHub <> "" -> $" Set \"agent\": {{ \"hub\": \"{typedHub}\" }} in the agent's harbor.json, then start it."
                | _ -> ""
            result.Text <- $"Saved. The agent sends the code with its next push (within a minute); this icon turns green when the hub accepts it.{hubNote}"
            result.Foreground <- accent
            pair.IsEnabled <- false)
    w

/// Debug aid: render a window's content to a PNG without touching the screen.
let snapshot (w: Window) (path: string) =
    match w.Content with
    | :? Control as c ->
        c.Measure(Size(w.Width, Double.PositiveInfinity))
        c.Arrange(Rect(Size(w.Width, c.DesiredSize.Height)))
        let size = PixelSize(int (max 1. c.Bounds.Width), int (max 1. c.Bounds.Height))
        use bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size)
        bmp.Render c
        bmp.Save path
    | _ -> ()
