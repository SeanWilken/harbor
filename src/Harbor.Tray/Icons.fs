/// Tray icons drawn at runtime: an anchor on a disc whose colour is the tether state.
module Harbor.Tray.Icons

open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Media.Imaging

type Shade =
    | Green
    | Amber
    | Red
    | Grey

let private colour =
    function
    | Green -> Color.Parse "#bed6a7"
    | Amber -> Color.Parse "#e0c084"
    | Red -> Color.Parse "#e48a7a"
    | Grey -> Color.Parse "#5f6d63"

/// The "commit anchor": the shank is a commit line, the ring is HEAD and the fluke tips are commits
/// (docs/mockups/icons.html, concept A). Drawn as shapes, not a font glyph, so it stays crisp when the
/// tray scales it down to 16-24 px. Transparent background: the colour is the tether state.
let bitmap (shade: Shade) : Bitmap =
    let size = 64
    let bmp = new RenderTargetBitmap(PixelSize(size, size), Vector(96., 96.))
    use ctx = bmp.CreateDrawingContext()
    let b = SolidColorBrush(colour shade)
    let pen = Pen(b, 6., lineCap = PenLineCap.Round, lineJoin = PenLineJoin.Round)
    for d in [ "M32 19 V54"; "M14 38 Q14 55 32 55 Q50 55 50 38"; "M21 28 H43" ] do
        ctx.DrawGeometry(null, pen, Geometry.Parse d)
    ctx.DrawEllipse(null, Pen(b, 5.5), Point(32., 11.), 6.5, 6.5)
    ctx.DrawEllipse(b, null, Point(14., 36.), 5.5, 5.5)
    ctx.DrawEllipse(b, null, Point(50., 36.), 5.5, 5.5)
    bmp :> Bitmap

let private cache = System.Collections.Generic.Dictionary<Shade, WindowIcon>()

let icon (shade: Shade) =
    match cache.TryGetValue shade with
    | true, i -> i
    | _ ->
        let i = WindowIcon(bitmap shade)
        cache[shade] <- i
        i
