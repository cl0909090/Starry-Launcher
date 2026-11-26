using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace StarryLauncher;

internal static class SvgIcon
{

    private static readonly Dictionary<string, string> Files = new()
    {
        ["fz"] = "fz",
        ["fk"] = "fk",
        ["sz"] = "sz",
        ["fuz"] = "fuz",
        ["lj"] = "lj",
        ["xz"] = "xz",
        ["fswjr"] = "fswjr",
        ["avatar"] = "tx",
        ["xx"] = "xx",
        ["fs"] = "fs",
        ["emoji"] = "emoji",
        ["dk"] = "dk",
        ["gb"] = "gb",
        ["wg"] = "wg",
        ["ys"] = "ys",
        ["zl"] = "zl",
        ["ljzd"] = "ljzd",
        ["yxrz"] = "yxrz",
        ["fjkz"] = "fjkz",
        ["tz"] = "tz",
        ["szdh"] = "szdh",
        ["xqwj"] = "xqwj",
        ["sx"] = "sx",
        ["cg"] = "bx-bulb",
        ["et"] = "et",
        ["pcl"] = "pcl",
        ["bj"] = "bj",
        ["fwq"] = "fwq",
        ["gy"] = "gy",
        ["dy"] = "dy",
        ["github"] = "github",
        ["tj"] = "tj",
        ["hslj"] = "hslj",
        ["qk"] = "qk",
        ["gbfwq"] = "gbfwq",
        ["ss"] = "ss",
        ["kj"] = "kj",
        ["tzkb"] = "circle-arrow-out-down-left",
        ["fy"] = "translate",
        ["zt"] = "mdi-window-shutter-auto",
        ["tsys"] = "bx-adjust",
        ["tslb"] = "mdi-bell-ring-outline",
        ["fhq"] = "bx-shield-quarter",
        ["cs"] = "bx-tachometer",
        ["gj"] = "bx-wrench",
        ["yj"] = "eye",
        ["by"] = "eye-off",
    };

    private static readonly Dictionary<string, string> Paths = new()
    {


        ["fz"] = "M12 4a4 4 0 0 1 4 4a4 4 0 0 1-4 4a4 4 0 0 1-4-4a4 4 0 0 1 4-4m0 2a2 2 0 0 0-2 2a2 2 0 0 0 2 2a2 2 0 0 0 2-2a2 2 0 0 0-2-2m0 7c2.67 0 8 1.33 8 4v3H4v-3c0-2.67 5.33-4 8-4m0 1.9c-2.97 0-6.1 1.46-6.1 2.1v1.1h12.2V17c0-.64-3.13-2.1-6.1-2.1",

        ["fk"] = "M13 11a3 3 0 1 0-3-3a3 3 0 0 0 3 3m0-4a1 1 0 1 1-1 1a1 1 0 0 1 1-1m4.11 3.86a5 5 0 0 0 0-5.72A2.9 2.9 0 0 1 18 5a3 3 0 0 1 0 6a2.9 2.9 0 0 1-.89-.14M13 13c-6 0-6 4-6 4v2h12v-2s0-4-6-4m-4 4c0-.29.32-2 4-2c3.5 0 3.94 1.56 4 2m7 0v2h-3v-2a5.6 5.6 0 0 0-1.8-3.94C24 13.55 24 17 24 17M8 12H5v3H3v-3H0v-2h3V7h2v3h3Z",

        ["sz"] = "m19.588 15.492l-1.814-1.29a6.483 6.483 0 0 0-.005-3.421l1.82-1.274l-1.453-2.514l-2.024.926a6.484 6.484 0 0 0-2.966-1.706L12.953 4h-2.906l-.193 2.213A6.483 6.483 0 0 0 6.889 7.92l-2.025-.926l-1.452 2.514l1.82 1.274a6.483 6.483 0 0 0-.006 3.42l-1.814 1.29l1.452 2.502l2.025-.927a6.483 6.483 0 0 0 2.965 1.706l.193 2.213h2.906l.193-2.213a6.484 6.484 0 0 0 2.965-1.706l2.025.927l1.453-2.501ZM13.505 2.985a.5.5 0 0 1 .5.477l.178 2.035a7.45 7.45 0 0 1 2.043 1.178l1.85-.863a.5.5 0 0 1 .662.195l2.005 3.47a.5.5 0 0 1-.162.671l-1.674 1.172c.128.798.124 1.593.001 2.359l1.673 1.17a.5.5 0 0 1 .162.672l-2.005 3.457a.5.5 0 0 1-.662.195l-1.85-.863c-.602.49-1.288.89-2.043 1.179l-.178 2.035a.5.5 0 0 1-.5.476h-4.01a.5.5 0 0 1-.5-.476l-.178-2.035a7.453 7.453 0 0 1-2.043-1.179l-1.85.863a.5.5 0 0 1-.663-.194L2.257 15.52a.5.5 0 0 1 .162-.671l1.673-1.171a7.45 7.45 0 0 1 0-2.359L2.42 10.148a.5.5 0 0 1-.162-.67L4.26 6.007a.5.5 0 0 1 .663-.195l1.85.863a7.45 7.45 0 0 1 2.043-1.178l.178-2.035a.5.5 0 0 1 .5-.477h4.01ZM11.5 9a3.5 3.5 0 1 1 0 7a3.5 3.5 0 0 1 0-7Zm0 1a2.5 2.5 0 1 0 0 5a2.5 2.5 0 0 0 0-5Z",

        ["fuz"] = "M15 11a3 3 0 0 1-3-3V4H8a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2v-8zm-2-3a2 2 0 0 0 2 2h3.59L13 4.41zM8 3h5l7 7v9a3 3 0 0 1-3 3H8a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3m0 21a5 5 0 0 1-5-5V7h1v12a4 4 0 0 0 4 4h8v1z",

        ["lj"] = "M10.73 14.97c.27.11.36.41.24.66s-.41.37-.66.24h-.01c-.46-.21-.89-.51-1.27-.9a4.49 4.49 0 0 1 0-6.36l3.53-3.53a4.49 4.49 0 0 1 6.36 0a4.49 4.49 0 0 1 0 6.36l-1.63 1.63l-.15-1.26l1.08-1.08a3.513 3.513 0 0 0 0-4.95a3.513 3.513 0 0 0-4.95 0L9.73 9.32a3.513 3.513 0 0 0 0 4.95c.3.3.64.53 1 .7m-6.65 4.95a4.49 4.49 0 0 1 0-6.36l1.63-1.63l.15 1.26l-1.08 1.08a3.513 3.513 0 0 0 0 4.95a3.513 3.513 0 0 0 4.95 0l3.54-3.54a3.513 3.513 0 0 0 0-4.95c-.3-.3-.64-.53-1-.7v.01a.49.49 0 0 1-.24-.67c.12-.25.41-.37.66-.24h.01c.46.21.89.51 1.27.9a4.49 4.49 0 0 1 0 6.36l-3.53 3.53a4.49 4.49 0 0 1-6.36 0",

        ["xz"] = "M12 4v12.25L17.25 11l.75.66l-6.5 6.5l-6.5-6.5l.75-.66L11 16.25V4zM3 19h1v2h15v-2h1v3H3z",

        ["fswjr"] = "M5 5h4l3 3h6a3 3 0 0 1 3 3v6a3 3 0 0 1-3 3H5a3 3 0 0 1-3-3V8a3 3 0 0 1 3-3m0 1a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h13a2 2 0 0 0 2-2v-6a2 2 0 0 0-2-2h-6.41l-3-3z",

        ["avatar"] = "M12 2A10 10 0 0 0 2 12a10 10 0 0 0 10 10a10 10 0 0 0 10-10A10 10 0 0 0 12 2M7.07 18.28c.43-.9 3.05-1.78 4.93-1.78s4.5.88 4.93 1.78A7.9 7.9 0 0 1 12 20c-1.86 0-3.57-.64-4.93-1.72m11.29-1.45c-1.43-1.74-4.9-2.33-6.36-2.33s-4.93.59-6.36 2.33A7.93 7.93 0 0 1 4 12c0-4.41 3.59-8 8-8s8 3.59 8 8c0 1.82-.62 3.5-1.64 4.83M12 6c-1.94 0-3.5 1.56-3.5 3.5S10.06 13 12 13s3.5-1.56 3.5-3.5S13.94 6 12 6m0 5a1.5 1.5 0 0 1-1.5-1.5A1.5 1.5 0 0 1 12 8a1.5 1.5 0 0 1 1.5 1.5A1.5 1.5 0 0 1 12 11",

        ["tj"] = "M2 21h4V9H2v2.5h2V19H2v2m7 0h8.37c1.05 0 1.96-.74 2.19-1.76l1.39-6.08a2.25 2.25 0 0 0-2.2-2.74H14.5l.7-3.32c.18-.88-.15-1.79-.86-2.35L13.13 3.8c-.31-.25-.75-.22-1.03.06L7 9v12",

        ["zt"] = "M4 5h16v2h-6v13h-4V7H4z",
    };

    private static readonly Dictionary<string, IImage> Cache = new();
    private static bool _filesLoaded;

    private static readonly Dictionary<string, double> Strokes = new();

    private static readonly Dictionary<string, (List<string> Stroke, List<string> Fill)> Mixed = new();

    private static void LoadFromResources()
    {
        if (_filesLoaded) return;
        _filesLoaded = true;
        foreach (var (key, file) in Files)
        {
            try
            {
                using var sr = new StreamReader(AssetLoader.Open(new Uri($"avares://StarryLauncher/tubiao/svg/{file}.svg")));
                var svg = sr.ReadToEnd();

                var strokeParts = new List<string>();
                var fillParts = new List<string>();
                double strokeWidth = double.TryParse(Regex.Match(svg, "stroke-width=\"([\\d.]+)\"").Groups[1].Value,
                    out var w0) ? w0 : 1.5;
                foreach (Match tag in Regex.Matches(svg, "<path\\b[^>]*>"))
                {
                    var d = Regex.Match(tag.Value, "d=\"([^\"]+)\"").Groups[1].Value;
                    if (string.IsNullOrEmpty(d)) continue;
                    bool isFill = tag.Value.Contains("fill=") && !tag.Value.Contains("fill=\"none\"");
                    (isFill ? fillParts : strokeParts).Add(d);
                }
                if (strokeParts.Count + fillParts.Count > 1)
                {

                    Mixed[key] = (strokeParts, fillParts);
                    if (strokeParts.Count > 0) Strokes[key] = strokeWidth;
                    continue;
                }
                Mixed.Remove(key);
                var all = strokeParts.Concat(fillParts).ToList();
                if (all.Count == 0) continue;

                Paths[key] = Regex.Replace(all[0], @"^\s*m", "M");
                if (strokeParts.Count > 0)
                    Strokes[key] = strokeWidth;
                else
                    Strokes.Remove(key);
            }
            catch {   }
        }
    }

    private static SolidColorBrush? _iconBrushOnVar;
    private static SolidColorBrush IconBrushOnVar =>
        _iconBrushOnVar ??= Avalonia.Application.Current?.Resources["OnSurfaceVarBrush"] as SolidColorBrush
            ?? new SolidColorBrush(Color.FromRgb(0x49, 0x45, 0x4F));
    private static SolidColorBrush? _iconBrushOn;
    private static SolidColorBrush IconBrushOn =>
        _iconBrushOn ??= Avalonia.Application.Current?.Resources["OnSurfaceBrush"] as SolidColorBrush
            ?? new SolidColorBrush(Color.FromRgb(0x1C, 0x1B, 0x1F));

    public static IImage Get(string key, string color = "")
    {
        LoadFromResources();
        IBrush brush;
        string cacheKey;
        if (string.IsNullOrEmpty(color) || color == "#49454F")
        { brush = IconBrushOnVar; cacheKey = key + "|@tv"; }
        else if (color == "#1C1B1F")
        { brush = IconBrushOn; cacheKey = key + "|@tf"; }
        else { brush = new SolidColorBrush(Color.Parse(color)); cacheKey = key + "|" + color; }
        if (Cache.TryGetValue(cacheKey, out var img)) return img;

        img = Mixed.TryGetValue(key, out var parts)
            ? BuildMixed(brush, key, parts)
            : new DrawingImage { Drawing = BuildDrawing(key, brush) };
        Cache[cacheKey] = img;
        return img;
    }

    private static GeometryDrawing BuildDrawing(string key, IBrush brush)
    {
        if (!Paths.TryGetValue(key, out var data)) return Transparent();
        try
        {

            if (Strokes.TryGetValue(key, out var w))
                return new GeometryDrawing
                {
                    Geometry = StreamGeometry.Parse(data),
                    Pen = new Pen(brush, w) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
                };
            return new GeometryDrawing
            {
                Geometry = StreamGeometry.Parse(data),
                Brush = brush,
            };
        }
        catch { return Transparent(); }
    }

    private static GeometryDrawing Transparent() =>
        new() { Geometry = StreamGeometry.Parse("M0 0"), Brush = new SolidColorBrush(Colors.Transparent) };

    private static IImage BuildMixed(IBrush brush, string key, (List<string> Stroke, List<string> Fill) parts)
    {
        try
        {
            double w = Strokes.TryGetValue(key, out var sw) ? sw : 1.5;
            var group = new DrawingGroup();
            foreach (var d in parts.Stroke)
            {
                try
                {
                    group.Children.Add(new GeometryDrawing
                    {
                        Geometry = StreamGeometry.Parse(d),
                        Pen = new Pen(brush, w) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
                    });
                }
                catch {   }
            }
            foreach (var d in parts.Fill)
            {
                try
                {

                    group.Children.Add(new GeometryDrawing
                    {
                        Geometry = StreamGeometry.Parse("F0 " + d),
                        Brush = brush,
                    });
                }
                catch {   }
            }
            if (group.Children.Count == 0) return new DrawingImage { Drawing = Transparent() };
            return new DrawingImage { Drawing = group };
        }
        catch { return new DrawingImage { Drawing = Transparent() }; }
    }

    public static StackPanel IconText(string key, string text, string color = "", double iconSize = 17)
    {
        var sp = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        sp.Children.Add(new Image { Width = iconSize, Height = iconSize, Source = Get(key, color) });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        return sp;
    }
}

public static class CopyFeedback
{
    private static readonly HashSet<Avalonia.Controls.ContentControl> Flashing = new();

    public static async void Flash(Avalonia.Controls.ContentControl btn)
    {
        if (!Flashing.Add(btn)) return;
        var prev = btn.Content;
        try
        {
            btn.Content = SvgIcon.IconText("fuz", S.Get("btn.copied"));
            await Task.Delay(2000);
            btn.Content = prev;
        }
        finally { Flashing.Remove(btn); }
    }
}
