using System.Text;

namespace StarryLauncher;

public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static int _writes;
    private const long MaxLogBytes = 5 * 1024 * 1024;

    public static string Folder
    {
        get
        {
            _dir ??= AppDirs.Logs;
            return _dir;
        }
    }

    public static string CurrentFile => Path.Combine(Folder, $"starry-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Folder);

                if (_writes == 0) TrimOldLogs();
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
                if (++_writes % 200 == 0) TrimOldLogs();
                if (new FileInfo(CurrentFile).Length > MaxLogBytes) RollCurrentFile();
            }
        }
        catch
        {

        }
    }

    private static void TrimOldLogs()
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles("starry-*.log");
            if (files.Length <= 7) return;
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            for (int i = 0; i < files.Length - 7; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }

    private static void RollCurrentFile()
    {
        try
        {
            var file = CurrentFile;
            if (!File.Exists(file)) return;
            var rolled = Path.Combine(Folder, $"starry-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            if (!File.Exists(rolled)) File.Move(file, rolled);
            TrimOldLogs();
        }
        catch { }
    }
}

public sealed class LogBuffer
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private readonly int _maxLines;
    private string? _cached;
    private string _builder = "";

    public LogBuffer(int maxLines = 2000) => _maxLines = maxLines;

    public void Append(string message)
    {
        lock (_gate)
        {
            _lines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_lines.Count > _maxLines) _lines.RemoveRange(0, _lines.Count - _maxLines);
            _cached = null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _cached = null;
        }
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                if (_cached != null) return _cached;

                _builder = string.Join('\n', _lines);
                return _cached = _builder;
            }
        }
    }

    public override string ToString() => Text;
}
