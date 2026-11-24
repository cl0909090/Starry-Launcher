using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarryLauncher;

public sealed class CustomServer
{
    public string Name { get; set; } = "";
    public string Addr { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public static class ProfileSync
{

    public static event Action? Changed;
    public static void Raise() => Changed?.Invoke();

    public static event Action? BackgroundChanged;
    public static void RaiseBackground() => BackgroundChanged?.Invoke();
}

public sealed class AppSettings
{
    private static AppSettings? _shared;
    public static AppSettings Shared => _shared ??= Load();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public bool TrayMinimize { get; set; }

    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public string Language { get; set; } = "zh-CN";

    public string UserName { get; set; } = "";

    public bool UserNameCustom { get; set; }

    public string UserNameMachine { get; set; } = "";

    [JsonIgnore]
    public string DisplayName =>
        UserNameCustom && !string.IsNullOrWhiteSpace(UserName) ? UserName : Environment.UserName;

    public string UserAvatarPath { get; set; } = "";


    public string McLoginType { get; set; } = "microsoft";

    public string McUserName { get; set; } = "";

    public string McUuid { get; set; } = "";

    public string McAccessToken { get; set; } = "";

    public string McRefreshToken { get; set; } = "";

    public long McTokenExpiresAt { get; set; }

    public List<AccountEntry> McAccounts { get; set; } = [];

    public sealed class AccountEntry
    {
        public string Type { get; set; } = "microsoft";
        public string Name { get; set; } = "";
        public string Uuid { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public long ExpiresAt { get; set; }
        public string Key => $"{Type}|{Uuid}";
    }

    public string McGameDir { get; set; } = "";

    public List<string> McGamePaths { get; set; } = [];

    public List<string> McGamePathsExcluded { get; set; } = [];

    public List<string>? McGamePathsEnabled { get; set; }

    public Dictionary<string, string> McGamePathAliases { get; set; } = [];

    public string McDownloadSource { get; set; } = "bmclapi";

    public Dictionary<string, string> ResTargetMemory { get; set; } = [];

    public string McJavaPath { get; set; } = "";

    public string McSelectedInstance { get; set; } = "";

    public string PageBgPath { get; set; } = "";

    public double PageBgBlur { get; set; }

    public double PageBgVisibility { get; set; } = 100;

    public double ComponentOpacity { get; set; } = 100;

    public string ChatBgPath { get; set; } = "";

    public double ChatBgBlur { get; set; }

    public double ChatBgVisibility { get; set; } = 100;

    public string ThemeColor { get; set; } = "";

    public string ThemeGradient { get; set; } = "";

    public string UiFont { get; set; } = "";

    public string ThemeMode { get; set; } = "light";

    private static readonly object _saveLock = new();

    public static string FilePath => AppDirs.SettingsFile;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s != null)
                {

                    if (!s.UserNameCustom) s.UserName = "";

                    if (s.UserNameCustom && !string.Equals(s.UserNameMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    {
                        s.UserNameCustom = false;
                        s.UserName = "";
                    }

                    s.McGamePaths ??= [];
                    s.McGamePathsExcluded ??= [];
                    if (!string.IsNullOrWhiteSpace(s.McGameDir) &&
                        !s.McGamePaths.Any(p => string.Equals(p, s.McGameDir, StringComparison.OrdinalIgnoreCase)))
                        s.McGamePaths.Add(s.McGameDir);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 settings.json 失败，使用默认配置: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {

        lock (_saveLock)
        {
            try
            {

                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"写入 settings.json 失败: {ex.Message}");
            }
        }
    }
}
