using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Threading;

namespace StarryLauncher.Views;

public partial class LauncherView : UserControl
{

    private readonly List<LauncherService.McInstance> _instances = [];
    private LauncherService.McInstance? _selectedInst;
    private CancellationTokenSource? _msaCts;

    private EventHandler<Avalonia.Controls.SizeChangedEventArgs>? _sizeHandler;

    public LauncherView()
    {
        InitializeComponent();

        var stLogin = AppSettings.Shared;
        if (!string.Equals(stLogin.McLoginType, "microsoft", StringComparison.OrdinalIgnoreCase))
        {
            stLogin.McLoginType = "microsoft";
            stLogin.McUserName = "";
            stLogin.McAccessToken = "";
            stLogin.McRefreshToken = "";
            stLogin.McUuid = "";
            stLogin.Save();
        }

        InstTitleIcon.Source = SvgIcon.Get("kj");
        PathPickBtnIcon.Source = SvgIcon.Get("xqwj");
        GameLogBtnIcon.Source = SvgIcon.Get("yxrz");
        LogDrawerIcon.Source = SvgIcon.Get("yxrz");
        MainWindow.BindAcrylic(LogDrawerAcrylic, LogDrawerWhiteBg);

        RefreshInstances();
        RefreshAccountUi();
        LoadAvatarAsync();

        AttachedToVisualTree += OnPageAttach;
        DetachedFromVisualTree += OnPageDetach;
        UpdateButtons();
    }

    private void OnPageAttach(object? sender, VisualTreeAttachmentEventArgs e)
    {
        LauncherService.GameExited += OnGameExited;
        LauncherService.GameWindowFocused += OnGameWindowFocused;
        McInstaller.Completed += OnMcInstallDone;

        RefreshInstances();

        LauncherService.ReattachRunningGame();
        UpdateButtons();
    }

    private void OnPageDetach(object? sender, VisualTreeAttachmentEventArgs e)
    {
        LauncherService.GameExited -= OnGameExited;
        LauncherService.GameWindowFocused -= OnGameWindowFocused;
        McInstaller.Completed -= OnMcInstallDone;
    }

    private void OnMcInstallDone(string versionId) => ExternalRefresh();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _sizeHandler ??= (_, _) => SyncHeight();
        PageScroll.SizeChanged -= _sizeHandler;
        PageScroll.SizeChanged += _sizeHandler;
        SyncHeight();
    }

    private void SyncHeight()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var vp = PageScroll.Viewport.Height;
            if (vp > 0) Root.Height = vp;
        }, DispatcherPriority.Background);
    }

    private DispatcherTimer? _statusTimer;

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer?.Stop();
        _statusTimer = null;
        if (string.IsNullOrEmpty(text)) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (StatusText.Text == text) StatusText.Text = "";
        };
        t.Start();
        _statusTimer = t;
    }

    private void RefreshAccountUi()
    {
        var st = AppSettings.Shared;

        LauncherService.RememberCurrentAccount();

        bool logged = !string.IsNullOrWhiteSpace(st.McUserName);
        if (logged)
        {
            NameText.Text = st.McUserName;
            MethodHeader.Text = S.Get("launcher.loginType.microsoft");
            LoginPanel.IsVisible = false;
            LoginBtn.IsVisible = false;
        }
        else
        {
            NameText.Text = S.Get("launcher.status.notlogin");
            MethodHeader.Text = S.Get("launcher.status.notlogin");
            LoginPanel.IsVisible = true;
            LoginBtn.IsVisible = true;
        }
        SwitchBtn.IsVisible = true;
    }

    private void LoadAvatarAsync()
    {
        var st = AppSettings.Shared;

        SetHeadFromSkin(LoadSteveSkin());

        if (st.McLoginType == "microsoft" && !string.IsNullOrEmpty(st.McUuid))
        {
            var uuid = st.McUuid;
            _ = Task.Run(async () =>
            {
                try
                {

                    byte[] bytes;
                    try
                    {
                        bytes = await LauncherService.Http.GetByteArrayAsync($"https://mc-heads.net/skin/{uuid}");
                    }
                    catch
                    {
                        bytes = await LauncherService.Http.GetByteArrayAsync($"https://crafatar.com/skins/{uuid}");
                    }
                    var skin = SkinFromBytes(bytes);
                    if (skin != null) SetHeadFromSkin(skin);
                }
                catch {   }
            });
        }
    }

    private static Bitmap? _steveSkin;

    private static Bitmap? LoadSteveSkin()
    {
        if (_steveSkin != null) return _steveSkin;
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://StarryLauncher/Assets/steve.png"));
            _steveSkin = new Bitmap(s);
        }
        catch { return null; }
        return _steveSkin;
    }

    private static Bitmap? SkinFromBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch { return null; }
    }

    private void SetHeadFromSkin(Bitmap? skin)
    {
        if (skin == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {

                int u = Math.Max(1, skin.PixelSize.Width / 64);
                AvatarFace.Source = new CroppedBitmap(skin, new PixelRect(8 * u, 8 * u, 8 * u, 8 * u));
                AvatarHat.Source = new CroppedBitmap(skin, new PixelRect(40 * u, 8 * u, 8 * u, 8 * u));
            }
            catch {   }
        });
    }

    private async void OnLogin(object? sender, RoutedEventArgs e)
    {
        SetStatus("");
        try
        {
            SetBusy(true);
            var session = LauncherService.BeginMicrosoftLogin();
            var top = TopLevel.GetTopLevel(this);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = session.AuthorizeUrl,
                    UseShellExecute = true,
                });
            }
            catch { }

            _msaCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var win = new MsaLoginWindow(session.AuthorizeUrl, _msaCts);
            if (top is Window owner) win.Show(owner); else win.Show();
            try
            {
                await LauncherService.CompleteMicrosoftLoginAsync(session, _msaCts.Token);
                win.SetStatus(S.Get("launcher.login.ok") + " · " + AppSettings.Shared.McUserName, true);
                await Task.Delay(700);
            }
            catch (OperationCanceledException)
            {
                SetStatus(S.Get("launcher.ms.canceled"));
                return;
            }
            finally { win.Close(); }

            RefreshAccountUi();
            LoadAvatarAsync();
            SetStatus(S.Get("launcher.login.ok"));
        }
        catch (Exception ex)
        {

            var msg = ex.Message.Contains(' ') ? ex.Message : S.Get(ex.Message);
            SetStatus(S.Get("launcher.login.failed") + " · " + msg);
        }
        finally { SetBusy(false); }
    }

    private void OnSwitchAccount(object? sender, RoutedEventArgs e)
    {
        var win = new AccountSwitcherWindow();
        var top = TopLevel.GetTopLevel(this);
        if (top is Window owner) win.Show(owner); else win.Show();
        win.Closed += (_, _) =>
        {
            if (win.Picked != null)
                LauncherService.SwitchToAccount(win.Picked);
            else if (win.AddNewRequested)
                LauncherService.Logout(keepLoginType: true);
            else
                return;
            RefreshAccountUi();
            LoadAvatarAsync();
        };
    }

    private void SetBusy(bool busy)
    {
        LoginBtn.IsEnabled = !busy;
        if (!busy) UpdateButtons();
    }

    private static readonly Dictionary<string, Bitmap> _loaderIcons = [];

    private static Bitmap LoaderIcon(string loader)
    {
        if (_loaderIcons.TryGetValue(loader, out var cached)) return cached;
        var file = loader switch
        {
            "forge" => "forge.png",
            "neoforge" => "NeoForge.png",
            "fabric" => "Fabric.png",
            "optifine" => "OptiFine.png",
            _ => "zsb.png",
        };
        Bitmap bmp;
        try { using var s = AssetLoader.Open(new Uri($"avares://StarryLauncher/Assets/{file}")); bmp = new Bitmap(s); }
        catch { using var s = AssetLoader.Open(new Uri("avares://StarryLauncher/Assets/zsb.png")); bmp = new Bitmap(s); }
        _loaderIcons[loader] = bmp;
        return bmp;
    }

    private void RefreshInstances()
    {
        _instances.Clear();
        InstList.Children.Clear();

        var all = LauncherService.VisibleGameDirs()
            .SelectMany(root => LauncherService.ScanInstances(root))
            .OrderByDescending(i => i.LastWrite);
        foreach (var inst in all)
        {
            _instances.Add(inst);
            InstList.Children.Add(BuildInstanceCard(inst));
        }

        _selectedInst = _instances.FirstOrDefault(i => i.Name == AppSettings.Shared.McSelectedInstance);
        ApplySelectionVisual();
        UpdateGameName();
    }

    private void UpdateGameName()
    {
        GameNameText.Text = _selectedInst is not null
            ? _selectedInst.Name
            : S.Get("launcher.noinstance");
    }

    private Border BuildInstanceCard(LauncherService.McInstance inst)
    {
        var onSurface = Application.Current?.Resources["OnSurfaceBrush"] as IBrush
            ?? new SolidColorBrush(Color.FromRgb(0x1C, 0x1B, 0x1F));
        var onVar = Application.Current?.Resources["OnSurfaceVarBrush"] as IBrush
            ?? new SolidColorBrush(Color.FromRgb(0x49, 0x45, 0x4F));

        var card = new Border
        {
            Classes = { "card", "instCard" },
            Padding = new Thickness(14, 10),
            Tag = inst.Name,
        };

        var grid = new Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("Auto,*") };

        var iconHost = new Border
        {
            Width = 36, Height = 36,
            CornerRadius = new CornerRadius(9),
            ClipToBounds = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new Image { Width = 36, Height = 36, Source = LoaderIcon(inst.Loader) },
        };
        grid.Children.Add(iconHost);

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = inst.Name, FontSize = 14, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = onSurface,
        });
        text.Children.Add(new TextBlock
        {
            Text = inst.PlayedText, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
            Foreground = onVar,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        card.Child = grid;

        card.Tapped += (_, _) =>
        {
            _selectedInst = inst;
            var st = AppSettings.Shared;
            st.McSelectedInstance = inst.Name;
            st.Save();
            ApplySelectionVisual();
            UpdateGameName();
        };
        return card;
    }

    private void ApplySelectionVisual()
    {
        foreach (var child in InstList.Children)
        {
            if (child is not Border b) continue;
            bool sel = _selectedInst is not null && (string?)b.Tag == _selectedInst.Name;
            if (sel && !b.Classes.Contains("sel")) b.Classes.Add("sel");
            else if (!sel && b.Classes.Contains("sel")) b.Classes.Remove("sel");
        }
    }

    private readonly List<string> _logLines = new();
    private CancellationTokenSource? _logDrawerCts;

    private async void OnOpenGameLog(object? sender, RoutedEventArgs e)
    {
        LogDim.IsVisible = true;
        LogDrawer.IsVisible = true;
        LogDrawer.RenderTransform = null;
        MainWindow.FadeIn(LogDrawer);

        LogDrawerBox.Text = _logLines.Count > 0 ? string.Join(Environment.NewLine, _logLines) : "";
        LogDrawerBox.CaretIndex = LogDrawerBox.Text?.Length ?? 0;
        LauncherService.Log += OnLauncherLogLine;
        OnLauncherLogLine(LauncherService.BacklogSnapshot());

        _logDrawerCts?.Cancel();
        _logDrawerCts = new CancellationTokenSource();
        try { await Task.Delay(600, _logDrawerCts.Token); } catch (OperationCanceledException) { }
    }

    private void OnLogDrawerCloseClick(object? sender, RoutedEventArgs e) => CloseLogDrawer();
    private void OnLogDimTapped(object? sender, Avalonia.Input.TappedEventArgs e) => CloseLogDrawer();

    private void CloseLogDrawer()
    {
        if (!LogDrawer.IsVisible) return;
        _logDrawerCts?.Cancel();
        LauncherService.Log -= OnLauncherLogLine;
        LogDrawer.IsVisible = false;
        LogDim.IsVisible = false;
        LogDrawer.Opacity = 1;
    }

    private void OnLauncherLogLine(string text)
    {
        if (text.Length == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            lock (_logLines)
            {
                foreach (var line in text.Split('\n'))
                {
                    _logLines.Add(line.TrimEnd('\r'));
                    if (_logLines.Count > 1000) _logLines.RemoveRange(0, _logLines.Count - 1000);
                }
            }
            if (!LogDrawer.IsVisible) return;
            LogDrawerBox.Text = string.Join(Environment.NewLine, _logLines);
            LogDrawerBox.CaretIndex = LogDrawerBox.Text?.Length ?? 0;
        });
    }

    private async void OnLogCopyClick(object? sender, RoutedEventArgs e)
    {
        var text = LogDrawerBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip is not null) await clip.SetTextAsync(text);
        CopyFeedback.Flash(BtnLogCopy);
    }

    private void OnOpenPathPick(object? sender, RoutedEventArgs e)
    {
        PathPickDim.IsVisible = true;
        PathPickPanel.IsVisible = true;
        MainWindow.BindAcrylic(PathPickAcrylic, PathPickWhiteBg);
        BuildPathPickList();
    }

    private void OnPathPickDimTapped(object? sender, Avalonia.Input.TappedEventArgs e) => ClosePathPick();
    private void OnPathPickClose(object? sender, RoutedEventArgs e) => ClosePathPick();

    private void ClosePathPick()
    {
        if (!PathPickPanel.IsVisible) return;
        PathPickPanel.IsVisible = false;
        PathPickDim.IsVisible = false;
    }

    private void BuildPathPickList()
    {
        PathPickList.Children.Clear();
        var dirs = LauncherService.FindGameDirs();
        if (dirs.Count == 0)
        {
            PathPickList.Children.Add(new TextBlock
            {
                Text = S.Get("launcher.pathPickEmpty"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x94, 0xA3)),
            });
            return;
        }
        var onVar = Application.Current?.Resources["OnSurfaceVarBrush"] as IBrush
            ?? new SolidColorBrush(Color.FromRgb(0x49, 0x45, 0x4F));

        foreach (var dir in dirs)
        {

            var label = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical };
            label.Children.Add(new TextBlock
            {
                Text = LauncherService.GetGamePathAlias(dir), FontSize = 12.5, Foreground = onVar,
            });
            label.Children.Add(new TextBlock
            {
                Text = dir, FontSize = 10, Foreground = onVar, Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var box = new CheckBox
            {
                Content = label, IsChecked = LauncherService.IsGamePathEnabled(dir),
                FontSize = 12.5, Foreground = onVar,
                Padding = new Thickness(10, 5, 10, 5),
            };
            box.Click += (_, _) =>
            {

                var st = AppSettings.Shared;
                var set = st.McGamePathsEnabled is null
                    ? new List<string>(LauncherService.FindGameDirs())
                    : new List<string>(st.McGamePathsEnabled);
                set.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
                if (box.IsChecked == true) set.Add(dir);
                LauncherService.SetEnabledGamePaths(set);
                RefreshInstances();
            };
            PathPickList.Children.Add(box);
        }
    }

    internal void ExternalRefresh() => RefreshInstances();

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {

        if (!LauncherService.HasAccount)
        {
            SetStatus(S.Get("launcher.game.noaccount"));
            return;
        }
        if (_selectedInst is not LauncherService.McInstance inst)
        {
            SetStatus(S.Get("launcher.game.noinstance"));
            return;
        }
        SetStatus(S.Get("launcher.status.launching"));
        PlayBtn.IsEnabled = false;
        if (AppSettings.Shared.McLoginType == "microsoft")
        {
            SetStatus(S.Get("launcher.ms.refreshing"));
            bool refreshed = await LauncherService.TryRefreshMicrosoftAsync();
            var st0 = AppSettings.Shared;

            if (!refreshed && st0.McTokenExpiresAt > 0 &&
                st0.McTokenExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                SetStatus(S.Get("launcher.err.msaExpired"));
                UpdateButtons();
                return;
            }
        }
        var err = await LauncherService.LaunchAsync(inst, onStarted: () => Dispatcher.UIThread.Post(UpdateButtons));
        if (err != null)
        {
            SetStatus(S.Get(err));
            UpdateButtons();
        }
        else
        {
            SetStatus(S.Get("launcher.game.started"));
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        LauncherService.StopGame();
    }

    private bool _hiddenForGame;

    private void OnGameWindowFocused()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            if (w.WindowState == WindowState.Minimized) return;
            _hiddenForGame = true;
            w.WindowState = WindowState.Minimized;
        });
    }

    private void OnGameExited(int code, bool byUser)
    {
        Dispatcher.UIThread.Post(() =>
        {

            SetStatus(byUser ? S.Get("launcher.game.closedByUser")
                     : S.Get(code == 0 ? "launcher.game.exited" : "launcher.game.exitedCrash") + $" ({code})");
            UpdateButtons();

            if (_hiddenForGame && !LauncherService.IsGameRunning)
            {
                _hiddenForGame = false;
                if (TopLevel.GetTopLevel(this) is Window w)
                {
                    w.WindowState = WindowState.Normal;
                    w.Activate();
                }
            }
        });
    }

    private void UpdateButtons()
    {

        PlayBtn.IsEnabled = true;
        StopBtn.IsEnabled = LauncherService.IsGameRunning;
    }
}
