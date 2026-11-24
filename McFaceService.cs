using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace StarryLauncher;

internal static class McFaceService
{

    private static readonly Dictionary<string, IImage> Cache = new();

    public static string Dir => Path.Combine(AppDirs.Root, "mcfaces");

    private static string SafeName(string nick)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in nick)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        var s = sb.ToString();
        if (s.Length == 0) s = "_";
        return s.Length > 40 ? s[..40] : s;
    }

    public static Bitmap? TryLoadCached(string nick)
    {
        try
        {
            if (Cache.TryGetValue(nick, out var hit)) return hit as Bitmap;
            var path = Path.Combine(Dir, SafeName(nick) + ".png");
            if (!File.Exists(path)) return null;
            var bmp = new Bitmap(path);
            Cache[nick] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    public static async Task<IImage?> FetchAsync(string nick, Window? owner)
    {
        if (nick.Length == 0) return null;
        var cached = TryLoadCached(nick);
        if (cached != null) return cached;

        var face = await FromBytesAsync(await GetBytes($"https://mc-heads.net/avatar/{Uri.EscapeDataString(nick)}/64"));
        if (face == null)
        {

            var uid = await UuidFromMojangAsync(nick);
            if (!string.IsNullOrEmpty(uid))
                face = await FromBytesAsync(await GetBytes($"https://crafatar.com/avatars/{uid}?overlay&size=64"));
        }
        if (face == null)
            face = await PickSkinAndCropAsync(nick, owner);
        return face;
    }

    private static async Task<byte[]?> GetBytes(string url)
    {
        try { return await LauncherService.Http.GetByteArrayAsync(url); }
        catch { return null; }
    }

    private static async Task<IImage?> FromBytesAsync(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            await using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            if (bmp.PixelSize.Width < 8 || bmp.PixelSize.Height < 8) return null;
            return bmp;
        }
        catch { return null; }
    }

    private static async Task<string?> UuidFromMojangAsync(string nick)
    {
        try
        {
            var json = await LauncherService.Http.GetStringAsync(
                $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(nick)}");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task<IImage?> PickSkinAndCropAsync(string nick, Window? owner)
    {
        if (owner?.StorageProvider == null) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"导入 {nick} 的皮肤 PNG（64x64）",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("皮肤图片") { Patterns = ["*.png"] }],
        });
        if (files.Count == 0) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(files[0].Path.LocalPath);
            return CompositeAndSave(nick, bytes);
        }
        catch { return null; }
    }

    private static IImage? CompositeAndSave(string nick, byte[] pngBytes)
    {
        try
        {
            using var skb = SKBitmap.Decode(pngBytes);
            if (skb == null || skb.Width < 8 || skb.Height < 8) return null;
            int u = Math.Max(1, skb.Width / 64);
            using var outBmp = new SKBitmap(64, 64);
            using (var canvas = new SKCanvas(outBmp))
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.None })
            {
                using var face = new SKBitmap();
                if (!skb.ExtractSubset(face, new SKRectI(8 * u, 8 * u, 16 * u, 16 * u))) return null;
                canvas.DrawBitmap(face, SKRect.Create(64, 64), paint);
                using var hat = new SKBitmap();
                if (skb.ExtractSubset(hat, new SKRectI(40 * u, 8 * u, 48 * u, 16 * u)))
                    canvas.DrawBitmap(hat, SKRect.Create(64, 64), paint);
            }
            using var img = SKImage.FromBitmap(outBmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            var av = new Bitmap(new MemoryStream(data.ToArray()));
            Directory.CreateDirectory(Dir);
            av.Save(Path.Combine(Dir, SafeName(nick) + ".png"));
            Cache[nick] = av;
            return av;
        }
        catch { return null; }
    }
}
