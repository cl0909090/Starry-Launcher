using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace StarryLauncher;

internal static class SkinHeadService
{
    private static CroppedBitmap? _steveFace;

    private static readonly Dictionary<string, IImage> Cache = new();

    public static IImage? SteveFace()
    {
        if (_steveFace != null) return _steveFace;
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://StarryLauncher/Assets/steve.png"));
            _steveFace = new CroppedBitmap(new Bitmap(s), new PixelRect(8, 8, 8, 8));
        }
        catch {   }
        return _steveFace;
    }

    public static async Task<IImage?> LoadAsync(string type, string uuid)
    {
        var cacheKey = $"{type}|{uuid}";
        if (Cache.TryGetValue(cacheKey, out var hit)) return hit;

        try
        {
            if (type == "microsoft" && !string.IsNullOrEmpty(uuid))
            {

                try
                {
                    var img = BitmapFrom(await LauncherService.Http.GetByteArrayAsync(
                        $"https://mc-heads.net/avatar/{uuid}"));
                    if (img != null) return Cache[cacheKey] = img;
                }
                catch
                {
                    var img = BitmapFrom(await LauncherService.Http.GetByteArrayAsync(
                        $"https://crafatar.com/avatars/{uuid}?overlay"));
                    if (img != null) return Cache[cacheKey] = img;
                }
            }
        }
        catch {   }
        return SteveFace();
    }

    private static IImage? BitmapFrom(byte[] bytes)
    {
        try
        {
            var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch { return null; }
    }
}
