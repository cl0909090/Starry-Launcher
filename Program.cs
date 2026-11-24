using Avalonia;
using System;
using System.Threading;
using Avalonia.Fonts.Inter;

namespace StarryLauncher;

class Program
{

    private const string SingleInstanceMutexName = @"Global\StarryLauncher.SingleInstance";

    public const string ShowSignalName = @"Global\StarryLauncher.ShowWindow";

    private static Mutex? _instanceMutex;

    public static EventWaitHandle? ShowSignal { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {

        if (!TryAcquireSingleInstance()) return;

        AppDirs.MigrateLegacy();
        try
        {
            var boot = AppSettings.Load();
            S.Init(S.Parse(boot.Language));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"启动初始化失败（沿用默认语言）: {ex.Message}");
        }

        try
        {
            var exePath = Environment.ProcessPath ?? "";
            AppLog.Info($"Starry Launcher 启动：{exePath} 构建时间={(exePath.Length > 0 && File.Exists(exePath) ? File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd HH:mm:ss") : "?")} 进程={Environment.ProcessId}");
        }
        catch { }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("未处理异常（进程即将退出）", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "null"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("任务未观察异常（已吞掉不影响运行）", e.Exception);
            e.SetObserved();
        };

        try
        {

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Error("主循环致命异常退出", ex);
            throw;
        }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
    }

    private static bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (createdNew)
            {

                try { ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName, out _); }
                catch { }
                return true;
            }

            AppLog.Info("检测到已有一个 Starry Launcher 在运行，激活它并退出本进程");

            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch { }

            foreach (var p in System.Diagnostics.Process.GetProcessesByName("StarryLauncher"))
            {
                try
                {
                    if (p.Id == Environment.ProcessId) continue;
                    var h = p.MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        ShowWindow(h, SW_RESTORE);
                        SetForegroundWindow(h);
                        break;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            try { _instanceMutex.Dispose(); } catch { }
            _instanceMutex = null;
            return false;
        }
        catch (Exception ex)
        {

            AppLog.Warn($"单实例检查失败: {ex.Message}");
            return true;
        }
    }

    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
