namespace StarryLauncher;

internal static class AppDirs
{

    public static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static readonly string Root = Path.Combine(ExeDir, "StarryLauncher");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Biaoqingbao => Path.Combine(Root, "biaoqingbao");

    public static string Tx => Path.Combine(Root, "tx");

    public static string Modpack => Path.Combine(Root, "modpack");


    private static int _done;

    public static void MigrateLegacy()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        MoveFile("settings.json");
        MoveDir("logs");
        MoveDir("biaoqingbao");

        try
        {
            Directory.CreateDirectory(Tx);
            foreach (var n in new[] { "avatar_user.png", "avatar_chat.png", "avatar_ai.png" })
            {
                string old = Path.Combine(Root, n);
                string neu = Path.Combine(Tx, n);
                if (File.Exists(old) && !File.Exists(neu)) File.Move(old, neu);
            }
        }
        catch {   }
    }

    private static void MoveFile(string name)
    {
        try
        {
            string old = Path.Combine(ExeDir, name);
            string neu = Path.Combine(Root, name);
            if (!File.Exists(old) || File.Exists(neu)) return;
            Directory.CreateDirectory(Root);
            File.Move(old, neu);
        }
        catch {   }
    }

    private static void MoveDir(string name)
    {
        try
        {
            string old = Path.Combine(ExeDir, name);
            string neu = Path.Combine(Root, name);
            if (!Directory.Exists(old) || Directory.Exists(neu)) return;
            Directory.CreateDirectory(Root);
            Directory.Move(old, neu);
        }
        catch {   }
    }
}
