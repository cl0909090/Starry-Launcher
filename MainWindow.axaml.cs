using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;

namespace StarryLauncher;

public class MildBackEase : Avalonia.Animation.Easings.Easing
{
    public override double Ease(double t)
    {
        const double s = 0.7;
        t -= 1.0;
        return t * t * ((s + 1) * t + s) + 1.0;
    }
}

public partial class MainWindow : Window
{
    private Control _modpackPage = null!;
    private Control _settingsPage = null!;
    private Control _launcherPage = null!;
    private string _currentNavKey = "launcher";

    private Button? _selectedNavBtn = null!;
    private Button? _hoverRaisedBtn;

    public MainWindow()
    {
        InitializeComponent();

        _modpackPage = new Views.ModpackView();
        _settingsPage = new Views.SettingsView();
        _launcherPage = new Views.LauncherView();
        _selectedNavBtn = NavLauncher;
        BindAcrylic(NavAcrylic);

        NavIndicator.PointerEntered += (_, _) =>
        {
            if (_selectedNavBtn is not { } b) return;
            b.Classes.Add("raised");
            _hoverRaisedBtn = b;
        };
        NavIndicator.PointerExited += (_, _) =>
        {
            if (_hoverRaisedBtn is not { } b) return;
            b.Classes.Remove("raised");
            _hoverRaisedBtn = null;
        };

        S.Changed += OnLanguageChanged;

        var st = AppSettings.Shared;
        if (st.WindowWidth >= MinWidth && st.WindowHeight >= MinHeight)
        {
            Width = st.WindowWidth;
            Height = st.WindowHeight;
        }

        NavLauncher.Content = new Image { Width = 28, Height = 28, Source = SvgIcon.Get("pcl") };
        NavModpack.Content = new Image { Width = 28, Height = 28, Source = SvgIcon.Get("kj") };
        NavSettings.Content = new Image { Width = 28, Height = 28, Source = SvgIcon.Get("szdh") };
        SelectNav("launcher");
        _instance = this;
        ApplyAppearance();
        ApplyThemeColor();
        ApplyUiFont(this);

        Application.Current!.ActualThemeVariantChanged += (_, _) =>
        {
            if (AppSettings.Shared.ThemeMode == "system")
            {
                ApplyThemeColor(animate: true);
            }
        };
        UpdateTopBarIdentity();
        ProfileSync.Changed += OnProfileChangedTopBar;

        _greetTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background,
            (_, _) => UpdateTopBarIdentity());
        _greetTimer.Start();

        Closing += OnWindowClosing;
        _trayIcon = new TrayIcon
        {
            Icon = Icon,
            ToolTipText = "Starry Launcher",
            IsVisible = false,
            Menu = BuildTrayMenu(),
        };
        _trayIcon.Clicked += (_, _) => RestoreFromTray();
        var app = Application.Current;
        if (app != null) TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });

        McInstaller.ProgressChanged += OnMcInstallProgress;
        McInstaller.Completed += OnMcInstallDone;
        McInstaller.Failed += OnMcInstallFail;
        McInstaller.Cancelled += OnMcInstallCancelled;

        if (Program.ShowSignal is { } sig)
        {
            _ = Task.Run(() =>
            {
                while (sig.WaitOne())
                    Dispatcher.UIThread.Post(() => RestoreFromTray());
            });
        }
    }

    private TrayIcon? _trayIcon;
    private bool _forceExit;
    private static MainWindow? _instance;
    private DispatcherTimer? _greetTimer;

    private Border? _dlBanner;
    private TextBlock? _dlTitle, _dlPct;
    private ProgressBar? _dlBar;
    private Button? _dlCancelBtn;
    private StackPanel? _dlTaskHost;
    private StackPanel? _dlDetail;
    private ScrollViewer? _dlTaskScroll;
    private bool _dlExpanded;
    private string? _dlLastTaskId;
    private const double DlCollapsedW = 320, DlExpandedW = 460, DlCollapsedH = 58, DlExpandedMaxH = 560;
    private const double DlTop = 14;

    private void CenterDlBanner()
    {
        if (_dlBanner == null) return;
        Canvas.SetLeft(_dlBanner, Math.Max(0, (Bounds.Width - _dlBanner.Width) / 2));
    }

    private void OnDlWindowSizeChanged(object? sender, SizeChangedEventArgs e) => CenterDlBanner();

    private void OnMcInstallProgress(McInstallProgress p) => Dispatcher.UIThread.Post(() =>
    {
        EnsureDlBanner();
        if (_dlBanner == null || _dlTitle == null) return;
        _dlLastTaskId = p.TaskId;

        if (_dlTitle.Foreground is Avalonia.Media.SolidColorBrush b && b.Color == Avalonia.Media.Color.Parse("#C0392B"))
            _dlTitle.Foreground = (IBrush)Application.Current!.Resources["OnSurfaceBrush"]!;

        var active = McInstaller.ActiveTasks;
        if (active.Count > 1)
        {
            _dlTitle.Text = string.Format(S.Get("dl.task.summary"), active.Count, active.Average(t => t.Percent), active.Sum(t => t.SpeedMb));
            _dlPct.Text = $"{active.Average(t => t.Percent):F0}%";
            if (_dlBar != null) _dlBar.Value = active.Average(t => t.Percent);
        }
        else
        {
            _dlTitle.Text = string.Format(S.Get("dl.installing"), p.Title);
            _dlPct.Text = $"{p.Percent:F0}%";
            if (_dlBar != null) _dlBar.Value = p.Percent;
        }
        if (_dlExpanded) RefreshDlDetail();
    });

    private void OnMcInstallDone(string versionId) => Dispatcher.UIThread.Post(() =>
    {
        if (_dlBanner == null || _dlTitle == null) return;
        _dlTitle.Text = string.Format(S.Get("dl.installing"), versionId) + " — " + S.Get("dl.done");
        _dlPct.Text = "100%";
        if (_dlBar != null) _dlBar.Value = 100;
        RefreshDlDetail();
        if (McInstaller.ActiveTasks.Count > 0) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) => { t.Stop(); RemoveDlBanner(); };
        t.Start();
    });

    private void OnMcInstallFail(string versionId, string error) => Dispatcher.UIThread.Post(() =>
    {
        if (_dlBanner == null || _dlTitle == null) return;
        _dlTitle.Text = S.Get("dl.failed") + " · " + versionId;
        _dlPct.Text = "";
        if (_dlBar != null) _dlBar.Value = 0;
        _dlTitle.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C0392B"));
        RefreshDlDetail();
        if (_dlExpanded) ApplyDlHeight();
    });

    private void OnMcInstallCancelled(string versionId) => Dispatcher.UIThread.Post(() =>
    {
        if (_dlBanner == null || _dlTitle == null) return;
        if (McInstaller.ActiveTasks.Count > 0) { RefreshDlDetail(); return; }
        _dlTitle.Text = string.Format(S.Get("dl.installing"), versionId) + " — " + S.Get("dl.cancelled");
        _dlPct.Text = "";
        RefreshDlDetail();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) => { t.Stop(); if (McInstaller.ActiveTasks.Count == 0) RemoveDlBanner(); };
        t.Start();
    });

    private void EnsureDlBanner()
    {
        if (_dlBanner != null) return;
        var layer = OverlayLayer.GetOverlayLayer(this);
        if (layer == null) return;
        var app = Application.Current!;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var icon = new Image { Source = SvgIcon.Get("xz"), Width = 22, Height = 22, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(icon, 0);
        _dlTitle = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = (IBrush)app.Resources["OnSurfaceBrush"]! };
        Grid.SetColumn(_dlTitle, 1);
        _dlPct = new TextBlock { FontSize = 12.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), Foreground = (IBrush)app.Resources["OnSurfaceBrush"]! };
        Grid.SetColumn(_dlPct, 2);
        grid.Children.Add(icon);
        grid.Children.Add(_dlTitle);
        grid.Children.Add(_dlPct);

        _dlBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = 0, CornerRadius = new CornerRadius(3) };
        _dlBar.Foreground = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#4F7CFF"), 0),
                new GradientStop(Color.Parse("#8A5CF6"), 1),
            },
        };
        _dlCancelBtn = new Button
        {
            Classes = { "outlined" },
            Content = S.Get("dl.cancel"),
            MinHeight = 34,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _dlCancelBtn.Click += (_, e) => { e.Handled = true; McInstaller.CancelAll(); };

        _dlTaskHost = new StackPanel();
        _dlTaskScroll = new ScrollViewer { Content = _dlTaskHost, Margin = new Thickness(0, 10, 0, 0) };

        var detail = new StackPanel { Margin = new Thickness(2, 0, 2, 12) };
        _dlDetail = detail;
        detail.Children.Add(_dlBar);
        detail.Children.Add(_dlTaskScroll);
        detail.Children.Add(_dlCancelBtn);

        var root = new StackPanel();
        var header = new Border { Height = 58, Padding = new Thickness(14, 0), Child = grid };
        root.Children.Add(header);
        root.Children.Add(detail);

        _dlBanner = new Border
        {

            Width = DlCollapsedW * 0.46,
            Height = 20,
            ClipToBounds = true,
            CornerRadius = new CornerRadius(12),
            Background = (IBrush)app.Resources["SurfaceBrush"]!,
            BoxShadow = BoxShadows.Parse("0 6 20 0 #33000000"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = root,
        };

        _dlBanner.Transitions = new Transitions
        {
            new DoubleTransition { Property = Avalonia.Layout.Layoutable.HeightProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            new DoubleTransition { Property = Avalonia.Layout.Layoutable.WidthProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
        };
        _dlBanner.Tapped += (_, e) =>
        {

            for (var v = ((RoutedEventArgs)e).Source as Avalonia.Visual; v != null && !ReferenceEquals(v, _dlBanner);
                 v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                if (v is Button) return;
            _dlExpanded = !_dlExpanded;
            if (_dlExpanded) { RefreshDlDetail(); ApplyDlHeight(); }
            else if (_dlBanner != null) { _dlBanner.Height = DlCollapsedH; _dlBanner.Width = DlCollapsedW; }
        };

        _dlBanner.PropertyChanged += OnDlBannerPropertyChanged;
        SizeChanged += OnDlWindowSizeChanged;
        layer.Children.Add(_dlBanner);
        Canvas.SetTop(_dlBanner, DlTop);
        CenterDlBanner();

        var bannerRef = _dlBanner;
        Dispatcher.UIThread.Post(() =>
        {
            if (bannerRef != _dlBanner) return;
            bannerRef.Width = DlCollapsedW;
            bannerRef.Height = DlCollapsedH;
        });
    }

    private void OnDlBannerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Avalonia.Layout.Layoutable.WidthProperty) CenterDlBanner();
    }

    private void RefreshDlDetail()
    {
        if (_dlBanner == null || _dlTaskHost == null) return;
        _dlTaskHost.Children.Clear();
        foreach (var t in McInstaller.Tasks) _dlTaskHost.Children.Add(BuildDlTaskRow(t));
        if (_dlExpanded) ApplyDlHeight();
    }

    private void ApplyDlHeight()
    {
        if (_dlBanner == null || _dlDetail == null) return;
        _dlBanner.Width = DlExpandedW;
        _dlDetail.Measure(new Avalonia.Size(DlExpandedW - 24, double.PositiveInfinity));
        double content = _dlDetail.DesiredSize.Height;
        _dlBanner.Height = Math.Min(DlExpandedMaxH, DlCollapsedH + Math.Max(content, 40));
    }

    private Avalonia.Controls.Control BuildDlTaskRow(McInstallTask t)
    {
        var app = Application.Current!;
        var onVar = (IBrush)app.Resources["OnSurfaceVarBrush"]!;
        var onSur = (IBrush)app.Resources["OnSurfaceBrush"]!;
        var red = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C0392B"));
        var root = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var titleLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var statusText = t.Finished
            ? t.Success ? "✓ " + S.Get("dl.done") : t.Cancelled ? S.Get("dl.cancelled") : S.Get("dl.failed")
            : $"{t.Percent:F0}%";
        var title = new TextBlock
        {
            Text = t.Title, FontSize = 13, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = onSur,
        };
        var status = new TextBlock
        {
            Text = statusText, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = t.Finished && !t.Success ? red : t.Finished ? onVar : onVar,
        };
        Grid.SetColumn(title, 0); Grid.SetColumn(status, 1);
        titleLine.Children.Add(title); titleLine.Children.Add(status);
        root.Children.Add(titleLine);

        foreach (var s in t.Stages)
        {
            string mark = s.Done >= s.Total && s.Total > 0 ? "✓" : s.Done > 0 ? $"{s.Percent:F0}%" : "…";
            root.Children.Add(new TextBlock
            {
                Text = $"{mark}  {S.Get(s.Key)}  ({s.Done / 1024.0 / 1024.0:F1} / {s.Total / 1024.0 / 1024.0:F1} MB)",
                FontSize = 11.5, Margin = new Thickness(2, 3, 0, 0),
                Foreground = mark == "✓" ? onVar : onSur,
            });
        }

        if (!t.Finished)
        {
            root.Children.Add(new TextBlock
            {
                Text = (t.SpeedMb > 0.05 ? $"{t.SpeedMb:F1} MB/s" : "") +
                       (t.CurrentFile.Length > 0 ? (t.SpeedMb > 0.05 ? " · " : "") + t.CurrentFile : ""),
                FontSize = 11.5, Margin = new Thickness(2, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = onVar,
            });
            var cancelRow = new Button
            {
                Classes = { "outlined" }, Content = S.Get("dl.cancel"), MinHeight = 26, MinWidth = 70,
                Padding = new Thickness(8, 1), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0), FontSize = 12,
            };
            var tid = t.TaskId;
            cancelRow.Click += (_, e) => { e.Handled = true; McInstaller.Cancel(tid); };
            root.Children.Add(cancelRow);
        }
        else
        {

            if (!t.Success && !t.Cancelled && t.Error is { Length: > 0 })
            {
                root.Children.Add(new TextBlock
                {
                    Text = t.Error, FontSize = 11.5, Margin = new Thickness(2, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap, Foreground = red,
                });
                var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                var detailBtn = new Button
                {
                    Classes = { "outlined" }, Content = S.Get("dl.detail"), MinHeight = 26, MinWidth = 84,
                    Padding = new Thickness(8, 1), FontSize = 12,
                };
                var detailText = new TextBlock
                {
                    Text = t.ErrorDetail, FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
                    Foreground = onVar, Margin = new Thickness(0, 6, 0, 0), IsVisible = false,
                    MaxHeight = 150,
                };
                detailBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    detailText.IsVisible = !detailText.IsVisible;
                    detailBtn.Content = detailText.IsVisible ? S.Get("dl.detail.hide") : S.Get("dl.detail");
                    ApplyDlHeight();
                };
                var removeBtn = new Button
                {
                    Classes = { "outlined" }, Content = S.Get("dl.task.remove"), MinHeight = 26, MinWidth = 70,
                    Padding = new Thickness(8, 1), FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
                };
                removeBtn.Click += (_, e) => { e.Handled = true; McInstaller.RemoveTask(t.TaskId); RefreshDlDetail(); };
                actions.Children.Add(detailBtn);
                actions.Children.Add(removeBtn);
                root.Children.Add(actions);
                root.Children.Add(detailText);
            }
            else if (t.Success)
            {
                var clearBtn = new Button
                {
                    Classes = { "outlined" }, Content = S.Get("dl.task.remove"), MinHeight = 26, MinWidth = 70,
                    Padding = new Thickness(8, 1), FontSize = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                clearBtn.Click += (_, e) => { e.Handled = true; McInstaller.RemoveTask(t.TaskId); RefreshDlDetail(); };
                root.Children.Add(clearBtn);
            }
        }
        return root;
    }

    private void RemoveDlBanner()
    {
        if (_dlBanner == null) return;
        _dlBanner.PropertyChanged -= OnDlBannerPropertyChanged;
        SizeChanged -= OnDlWindowSizeChanged;
        if (OverlayLayer.GetOverlayLayer(this) is { } layer) layer.Children.Remove(_dlBanner);
        _dlBanner = null;
        _dlExpanded = false;
        _dlTaskHost = null;
        _dlTaskScroll = null;
        _dlDetail = null;
        _dlBar = null;
        _dlCancelBtn = null;
    }

    private void OnProfileChangedTopBar() => Dispatcher.UIThread.Post(UpdateTopBarIdentity);

    private void UpdateTopBarIdentity()
    {
        var st = AppSettings.Shared;
        TopBarAvatar.Source = LoadLocalBitmap(st.UserAvatarPath) ?? DefaultUserAvatar;
        var name = string.IsNullOrWhiteSpace(st.UserName) ? Environment.UserName : st.UserName.Trim();
        var h = DateTime.Now.Hour;
        var key = h switch
        {
            >= 5 and < 11 => "greet.morning",
            >= 11 and < 13 => "greet.forenoon",
            >= 13 and < 18 => "greet.afternoon",
            _ => "greet.evening",
        };
        GreetingText.Text = $"{S.Get(key)} {name}";
    }

    private static Color Darken(Color c, double f) =>
        Color.FromRgb((byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));

    private static Color Tint(Color c, double w) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * w), (byte)(c.G + (255 - c.G) * w), (byte)(c.B + (255 - c.B) * w));

    private static Color NormalizeVivid(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        var min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        var v = max;
        var s = max <= 0 ? 0 : (max - min) / max;
        if (s < 0.02)
        {

            if (v > 0.62)
            {
                var g = (byte)(0.62 * 255);
                return Color.FromRgb(g, g, g);
            }
            return c;
        }
        if (s >= 0.35 && v <= 0.85) return c;

        double h;
        var d = max - min;
        if (max == c.R / 255.0) h = 60 * (((c.G - c.B) / 255.0 / d) % 6);
        else if (max == c.G / 255.0) h = 60 * ((c.B - c.R) / 255.0 / d + 2);
        else h = 60 * ((c.R - c.G) / 255.0 / d + 4);
        if (h < 0) h += 360;
        var (r2, g2, b2) = HsvToRgbTuple(h, 0.35, Math.Min(v, 0.85));
        return Color.FromRgb((byte)Math.Round(r2 * 255), (byte)Math.Round(g2 * 255), (byte)Math.Round(b2 * 255));
    }

    private static (double, double, double) HsvToRgbTuple(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        return h switch
        {
            < 60 => (c + m, x + m, m),
            < 120 => (x + m, c + m, m),
            < 180 => (m, c + m, x + m),
            < 240 => (m, x + m, c + m),
            < 300 => (x + m, m, c + m),
            _ => (c + m, m, x + m),
        };
    }

    private static (double h, double s, double l) RgbToHsl(Color c)
    {
        var r = c.R / 255.0; var g = c.G / 255.0; var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min <= 0) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        if (s <= 0) { var g = (byte)Math.Round(l * 255); return Color.FromRgb(g, g, g); }
        h = ((h % 360) + 360) % 360;
        double Q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double P = 2 * l - Q;
        double Chan(double t)
        {
            t = ((t % 1) + 1) % 1;
            if (t < 1.0 / 6) return P + (Q - P) * 6 * t;
            if (t < 0.5) return Q;
            if (t < 2.0 / 3) return P + (Q - P) * (2.0 / 3 - t) * 6;
            return P;
        }
        return Color.FromRgb((byte)Math.Round(Chan(h / 360 + 1.0 / 3) * 255),
                             (byte)Math.Round(Chan(h / 360) * 255),
                             (byte)Math.Round(Chan(h / 360 - 1.0 / 3) * 255));
    }

    private static Color DeriveLikeDefault(Color c, double s, double l)
    {
        var (h, cs, _) = RgbToHsl(c);
        if (cs < 0.02) return HslToRgb(0, 0, l);
        return HslToRgb(h, s, l);
    }

    internal static bool TryParseHex(string? hex, out Color c) =>
        Color.TryParse(string.IsNullOrEmpty(hex) || hex.StartsWith('#') ? hex : "#" + hex, out c);

    internal static readonly Dictionary<string, (Color A, Color B)> ThemeGradients = new()
    {
        ["blueviolet"] = (Color.FromRgb(0x4F, 0x7C, 0xFF), Color.FromRgb(0x8A, 0x5C, 0xF6)),
        ["violet"] = (Color.FromRgb(0x7B, 0x2F, 0xF0), Color.FromRgb(0xC8, 0x7E, 0xFF)),
        ["blue"]   = (Color.FromRgb(0x15, 0x65, 0xC0), Color.FromRgb(0x53, 0xC4, 0xF7)),
        ["teal"]   = (Color.FromRgb(0x00, 0x89, 0x7B), Color.FromRgb(0x4D, 0xD0, 0xC9)),
        ["green"]  = (Color.FromRgb(0x2E, 0x7D, 0x32), Color.FromRgb(0x7C, 0xD9, 0x6E)),
        ["gold"]   = (Color.FromRgb(0xF5, 0x7C, 0x00), Color.FromRgb(0xFF, 0xD5, 0x4F)),
        ["red"]    = (Color.FromRgb(0xC6, 0x28, 0x28), Color.FromRgb(0xFF, 0x6E, 0x6E)),
    };

    internal static Color GradientMid(Color a, Color b) => Color.FromRgb(
        (byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));

    private static void ApplyPrimaryStops(IResourceDictionary res, bool gradient, Color a, Color b)
    {
        if (res["PrimaryBrush"] is not LinearGradientBrush brush || brush.GradientStops.Count < 2) return;
        brush.GradientStops[0].Color = a;
        brush.GradientStops[1].Color = b;
    }

    internal static void ApplyThemeColor(string? overrideHex = null, bool animate = false)
    {
        if (animate && overrideHex == null)
        {
            ScheduleThemeFx();
            return;
        }
        ApplyThemeColorNow(overrideHex);
    }

    private static DispatcherTimer? _themeFxTimer;

    private static void ScheduleThemeFx()
    {
        var fx = _instance?.FindControl<Border>("ThemeFx");
        if (fx == null) { ApplyThemeColorNow(null); ApplyAppearance(); return; }

        if (_themeFxTimer != null) { _themeFxTimer.Stop(); _themeFxTimer = null; fx.Opacity = 0; }

        bool toDark = AppSettings.Shared.ThemeMode switch
        {
            "dark" => true,
            "light" => false,
            _ => Application.Current!.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark,
        };
        fx.Background = new SolidColorBrush(toDark
            ? Color.FromRgb(0x1C, 0x1B, 0x1F)
            : Color.FromRgb(0xFF, 0xFB, 0xFE));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const double fadeIn = 150, fadeOut = 180;
        bool switched = false;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _themeFxTimer = timer;
        timer.Tick += (_, _) =>
        {
            double t = clock.Elapsed.TotalMilliseconds;
            if (t < fadeIn)
            {
                fx.Opacity = t / fadeIn;
            }
            else if (!switched)
            {
                switched = true;
                ApplyThemeColorNow(null);
                ApplyAppearance();
            }
            else if (t < fadeIn + fadeOut)
            {
                fx.Opacity = 1 - (t - fadeIn) / fadeOut;
            }
            else
            {
                fx.Opacity = 0;
                _themeFxTimer = null;
                timer.Stop();
            }
        };
        timer.Start();
    }

    private static void ApplyThemeColorNow(string? overrideHex)
    {
        var app = Application.Current!;
        ResolveThemeVariant();
        var res = app.Resources;
        var st = AppSettings.Shared;
        var hex = overrideHex ?? st.ThemeColor;

        var gradId = overrideHex == null ? st.ThemeGradient : "";
        (Color A, Color B) grad = default;
        var hasGrad = !string.IsNullOrEmpty(gradId) && ThemeGradients.TryGetValue(gradId, out grad);

        var isDefault = !TryParseHex(hex, out var c0) || c0 == default;
        var c = isDefault ? Color.FromRgb(0x67, 0x50, 0xA4) : c0;
        if (hasGrad) c = GradientMid(grad.A, grad.B);
        c = NormalizeVivid(c);

        if (isDefault && !hasGrad)
        {

            SetColor(res, "PrimaryDarkBrush", Color.FromRgb(0x4F, 0x3B, 0x82));
            SetColor(res, "PrimaryLightBrush", Color.FromRgb(0xEA, 0xDD, 0xFF));
            SetColor(res, "PrimaryDarkTextBrush", Color.FromRgb(0x21, 0x00, 0x5D));
            SetColor(res, "SecondaryLightBrush", Color.FromRgb(0xE8, 0xDE, 0xF8));
            SetColor(res, "SecondaryHoverBrush", Color.FromRgb(0xDA, 0xCE, 0xEE));
            SetColor(res, "NavBallBrush", Color.FromArgb(0x33, 0x67, 0x50, 0xA4));
            SetColor(res, "TintSurfaceBrush", Color.FromArgb(0xE6, 0xF3, 0xED, 0xFB));
            SetColor(res, "TintHoverBrush", Color.FromArgb(0x2A, 0xE8, 0xDE, 0xF8));
        }
        else
        {

            SetColor(res, "PrimaryDarkBrush", DeriveLikeDefault(c, 0.377, 0.370));
            SetColor(res, "PrimaryLightBrush", DeriveLikeDefault(c, 1.0, 0.933));
            SetColor(res, "PrimaryDarkTextBrush", DeriveLikeDefault(c, 1.0, 0.182));
            var secondaryLight = DeriveLikeDefault(c, 0.654, 0.922);
            SetColor(res, "SecondaryLightBrush", secondaryLight);
            SetColor(res, "SecondaryHoverBrush", DeriveLikeDefault(c, 0.513, 0.863));
            SetColor(res, "NavBallBrush", Color.FromArgb(0x33, c.R, c.G, c.B));
            var tintSurface = DeriveLikeDefault(c, 0.64, 0.957);
            SetColor(res, "TintSurfaceBrush", Color.FromArgb(0xE6, tintSurface.R, tintSurface.G, tintSurface.B));
            SetColor(res, "TintHoverBrush", Color.FromArgb(0x2A, secondaryLight.R, secondaryLight.G, secondaryLight.B));
        }

        ApplyFluentAccent(res, c);

        if (hasGrad) ApplyPrimaryStops(res, true, grad.A, grad.B);
        else ApplyPrimaryStops(res, false, c, c);

        ApplyThemePalette(res, c);
        if (_instance is { } mw)
            mw.TransparencyBackgroundFallback = new SolidColorBrush(IsDark
                ? Color.FromArgb(0xF2, 0x1C, 0x1B, 0x1F)
                : Color.FromArgb(0xF2, 0xFF, 0xFB, 0xFE));
        RefreshAcrylicTints();
        ApplyComponentOpacity(st.ComponentOpacity);

        ThemePaletteChanged?.Invoke();
    }

    internal static event Action? ThemePaletteChanged;

    private static void ApplyFluentAccent(IResourceDictionary res, Color c)
    {
        string[] keys =
        {
            "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
            "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3"
        };
        Color d1 = Darken(c, 0.12), d2 = Darken(c, 0.25), d3 = Darken(c, 0.42);
        Color l1 = Tint(c, 0.16), l2 = Tint(c, 0.32), l3 = Tint(c, 0.5);

        Color MapVariant(string key) => key switch
        {
            "SystemAccentColorDark1" => d1, "SystemAccentColorDark2" => d2, "SystemAccentColorDark3" => d3,
            "SystemAccentColorLight1" => l1, "SystemAccentColorLight2" => l2, "SystemAccentColorLight3" => l3,
            _ => c
        };

        var dicts = AllDictionaries(res).ToList();

        var map = new Dictionary<Color, Color>();
        foreach (var d in dicts)
            foreach (var k in keys)
                if (d.TryGetValue(k, out var v) && v is Color col && !map.ContainsKey(col))
                    map[col] = MapVariant(k);
        if (map.Count == 0) return;

        foreach (var d in dicts)
            foreach (var k in keys)
                if (d.TryGetValue(k, out var v) && v is Color col && map.TryGetValue(col, out var nc))
                    d[k] = nc;

        foreach (var d in dicts)
        {
            var entries = d.ToArray();
            foreach (var (k, v) in entries)
                if (v is SolidColorBrush b && map.TryGetValue(b.Color, out var nc2))
                    b.Color = nc2;
        }

        var themeDicts = dicts.Where(d => keys.Any(k => d.ContainsKey(k))).ToList();
        if (themeDicts.Count == 0) return;
        double newHue = Hsv(c).H;
        var oldHues = map.Keys.Select(cc => Hsv(cc).H).Where(h => h >= 0).Distinct().ToList();
        foreach (var d in themeDicts)
        {
            var entries = d.ToArray();
            foreach (var (k, v) in entries)
            {
                if (v is not ISolidColorBrush sb) continue;
                var col = sb.Color;
                var (h, s, vv) = Hsv(col);
                if (s < 0.06) continue;
                if (!oldHues.Any(oh => HueDist(oh, h) <= 16)) continue;
                var nc = HsvToColor(newHue, s, vv, col.A / 255.0);
                if (v is SolidColorBrush mb) mb.Color = nc;
                else d[k] = new SolidColorBrush(nc);
            }
        }
    }

    private static (double H, double S, double V) Hsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        if (d <= 0) return (-1, 0, max);
        double h;
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max <= 0 ? 0 : d / max, max);
    }

    private static Color HsvToColor(double h, double s, double v, double a)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromArgb(
            (byte)Math.Round(a * 255),
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static double HueDist(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static IEnumerable<IResourceDictionary> AllDictionaries(IResourceDictionary root)
    {
        yield return root;
        if (root.MergedDictionaries != null)
            foreach (var m in root.MergedDictionaries)
                if (m is IResourceDictionary mrd)
                    foreach (var x in AllDictionaries(mrd))
                        yield return x;
        if (root is ResourceDictionary rd && rd.ThemeDictionaries != null)
            foreach (var td in rd.ThemeDictionaries.Values)
                if (td is ResourceDictionary tdr)
                    foreach (var x in AllDictionaries(tdr))
                        yield return x;
    }

    private static void SetColor(IResourceDictionary res, string key, Color c)
    {
        if (res[key] is SolidColorBrush b) b.Color = c;
    }

    internal static bool IsDark { get; private set; }

    private static void ResolveThemeVariant()
    {
        var app = Application.Current!;
        app.RequestedThemeVariant = AppSettings.Shared.ThemeMode switch
        {
            "dark" => Avalonia.Styling.ThemeVariant.Dark,
            "system" => Avalonia.Styling.ThemeVariant.Default,
            _ => Avalonia.Styling.ThemeVariant.Light,
        };
        IsDark = app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
    }

    private static void ApplyThemePalette(IResourceDictionary res, Color c)
    {
        if (IsDark)
        {
            SetColor(res, "SurfaceBrush", Color.FromRgb(0x1C, 0x1B, 0x1F));
            SetColor(res, "OnSurfaceBrush", Color.FromRgb(0xE6, 0xE1, 0xE5));
            SetColor(res, "OnSurfaceVarBrush", Color.FromRgb(0xC7, 0xC1, 0xCD));
            SetColor(res, "OutlineBrush", Color.FromRgb(0x93, 0x8F, 0x99));
            SetColor(res, "OutlineVarBrush", Color.FromRgb(0x4A, 0x44, 0x58));
            SetColor(res, "LogSurfaceBrush", Color.FromRgb(0x26, 0x26, 0x2B));
            SetColor(res, "LogTextBrush", Color.FromRgb(0xB4, 0xB8, 0xBC));
            SetColor(res, "WinBgBrush", Color.FromArgb(0x99, 0x1C, 0x1B, 0x1F));
            SetColor(res, "PadBrush", Color.FromArgb(0xE6, 0x1C, 0x1B, 0x1F));
            SetColor(res, "PadSoftBrush", Color.FromArgb(0x66, 0x1C, 0x1B, 0x1F));
            SetColor(res, "PadCardBrush", Color.FromArgb(0xD9, 0x22, 0x21, 0x26));
            SetColor(res, "PadStrongBrush", Color.FromArgb(0xF2, 0x1C, 0x1B, 0x1F));
            SetColor(res, "PadBorderBrush", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            SetColor(res, "DangerBgBrush", Color.FromRgb(0x45, 0x2B, 0x2B));
            SetColor(res, "DangerHoverBrush", Color.FromRgb(0x52, 0x33, 0x33));
            SetColor(res, "OutlinedBgBrush", Color.FromRgb(0x2C, 0x29, 0x31));

            SetColor(res, "PrimaryLightBrush", DeriveLikeDefault(c, 0.654, 0.30));
            SetColor(res, "PrimaryDarkTextBrush", DeriveLikeDefault(c, 1.0, 0.933));
            SetColor(res, "SecondaryLightBrush", DeriveLikeDefault(c, 0.654, 0.28));
            SetColor(res, "SecondaryHoverBrush", DeriveLikeDefault(c, 0.513, 0.34));
            var tint = DeriveLikeDefault(c, 0.64, 0.26);
            SetColor(res, "TintSurfaceBrush", Color.FromArgb(0xE6, tint.R, tint.G, tint.B));
            var th = DeriveLikeDefault(c, 1.0, 0.86);
            SetColor(res, "TintHoverBrush", Color.FromArgb(0x2A, th.R, th.G, th.B));
        }
        else
        {

            SetColor(res, "SurfaceBrush", Color.FromRgb(0xFF, 0xFB, 0xFE));
            SetColor(res, "OnSurfaceBrush", Color.FromRgb(0x1C, 0x1B, 0x1F));
            SetColor(res, "OnSurfaceVarBrush", Color.FromRgb(0x49, 0x45, 0x4F));
            SetColor(res, "OutlineBrush", Color.FromRgb(0x79, 0x74, 0x7E));
            SetColor(res, "OutlineVarBrush", Color.FromRgb(0xCA, 0xC4, 0xD0));
            SetColor(res, "LogSurfaceBrush", Color.FromRgb(0xD3, 0xEF, 0xE8));
            SetColor(res, "LogTextBrush", Color.FromRgb(0x5C, 0x6B, 0x70));
            SetColor(res, "WinBgBrush", Color.FromArgb(0x99, 0xFF, 0xFB, 0xFE));
            SetColor(res, "PadBrush", Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            SetColor(res, "PadSoftBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            SetColor(res, "PadCardBrush", Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
            SetColor(res, "PadStrongBrush", Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
            SetColor(res, "PadBorderBrush", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            SetColor(res, "DangerBgBrush", Color.FromRgb(0xFC, 0xE8, 0xE8));
            SetColor(res, "DangerHoverBrush", Color.FromRgb(0xF8, 0xD7, 0xD7));
            SetColor(res, "OutlinedBgBrush", Color.FromRgb(0xF4, 0xF1, 0xF7));
        }
    }

    private static void ApplyAcrylicLook(ExperimentalAcrylicBorder b)
    {
        if (b.Material is ExperimentalAcrylicMaterial m)
        {
            m.TintColor = IsDark
                ? Color.FromArgb(0xF2, 0x1C, 0x1B, 0x1F)
                : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
            var idx = _acrylics.FindIndex(r => r.Acrylic.TryGetTarget(out var t) && ReferenceEquals(t, b));
            if (IsDark)
            {
                m.TintOpacity = 1.0;
                m.MaterialOpacity = 0.0;
            }
            else if (idx >= 0)
            {
                m.TintOpacity = _acrylics[idx].TintOp;
                m.MaterialOpacity = _acrylics[idx].MatOp;
            }
        }
    }

    private static void RefreshAcrylicTints()
    {
        _acrylics.RemoveAll(r => !r.Acrylic.TryGetTarget(out _));
        foreach (var r in _acrylics)
            if (r.Acrylic.TryGetTarget(out var b))
                ApplyAcrylicLook(b);
    }

    private static IImage? _defaultUserAvatar;
    private static IImage DefaultUserAvatar => _defaultUserAvatar ??= SvgIcon.Get("avatar", "#9E9E9E");

    private static readonly List<(WeakReference<ExperimentalAcrylicBorder> Acrylic, WeakReference<Border>? Fallback,
        double TintOp, double MatOp)> _acrylics = new();
    private static bool _pageBgActive;
    private static Bitmap? _pageBgBmp; private static string? _pageBgBmpPath;

    internal static void BindAcrylic(ExperimentalAcrylicBorder b, Border? fallback = null)
    {
        var m0 = b.Material as ExperimentalAcrylicMaterial;
        _acrylics.Add((new WeakReference<ExperimentalAcrylicBorder>(b),
            fallback != null ? new WeakReference<Border>(fallback) : null,
            m0?.TintOpacity ?? 0.85, m0?.MaterialOpacity ?? 0.5));
        b.IsVisible = !_pageBgActive;
        ApplyAcrylicLook(b);
        ApplyFallback(fallback != null ? new WeakReference<Border>(fallback) : null);
    }

    private static void RefreshAcrylics()
    {
        _acrylics.RemoveAll(r => !r.Acrylic.TryGetTarget(out _));
        foreach (var r in _acrylics)
        {
            if (r.Acrylic.TryGetTarget(out var b))
                b.IsVisible = !_pageBgActive;
            ApplyFallback(r.Fallback);
        }
    }

    private static void ApplyFallback(WeakReference<Border>? fallback)
    {
        if (fallback?.TryGetTarget(out var fb) == true)
            fb.IsVisible = _pageBgActive;
    }

    private static readonly FontFamily DefaultUiFont = new("Microsoft YaHei UI");

    internal static FontFamily CurrentUiFont { get; private set; } = DefaultUiFont;

    internal static void ApplyUiFont(Window? extra = null)
    {
        var name = AppSettings.Shared.UiFont;
        FontFamily ff = DefaultUiFont;
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { ff = new FontFamily(name); }
            catch {   }
        }
        CurrentUiFont = ff;
        if (extra is not null) extra.FontFamily = ff;
        if (Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var w in desktop.Windows)
                w.FontFamily = ff;
        }
    }

    internal static void ApplyAppearance()
    {
        var w = _instance;
        if (w == null) return;
        var st = AppSettings.Shared;

        bool bgOn = false;
        try
        {
            if (!string.IsNullOrEmpty(st.PageBgPath) && File.Exists(st.PageBgPath))
            {
                if (_pageBgBmp == null || _pageBgBmpPath != st.PageBgPath)
                {
                    _pageBgBmp?.Dispose();
                    _pageBgBmp = new Bitmap(st.PageBgPath);
                    _pageBgBmpPath = st.PageBgPath;
                }
                w.PageBgBack.Source = _pageBgBmp;
                w.PageBgImage.Source = _pageBgBmp;
                bool blurOn = st.PageBgBlur > 0.5;
                w.PageBgImage.Effect = blurOn ? new BlurEffect { Radius = st.PageBgBlur } : null;
                w.PageBgBack.IsVisible = blurOn;
                w.PageBgImage.RenderTransform = null;
                w.PageBgImage.Opacity = Math.Clamp(st.PageBgVisibility, 0, 100) / 100.0;
                w.PageBgLayer.IsVisible = true;
                bgOn = true;
            }
            else
            {
                _pageBgBmp?.Dispose(); _pageBgBmp = null; _pageBgBmpPath = null;
                w.PageBgBack.Source = null; w.PageBgImage.Source = null;
                w.PageBgLayer.IsVisible = false;
            }
        }
        catch (Exception ex) { AppLog.Warn($"页面背景加载失败（跳过）: {ex.Message}"); w.PageBgLayer.IsVisible = false; }

        if (_pageBgActive != bgOn)
        {
            _pageBgActive = bgOn;
            RefreshAcrylics();
        }

        ApplyComponentOpacity(st.ComponentOpacity);
    }

    internal static void ApplyComponentOpacity(double percent)
    {
        double p = Math.Clamp(percent, 0, 100) / 100.0;
        var res = Application.Current!.Resources;
        if (res["CardBrush"] is SolidColorBrush card)
        {

            double full = 0xE6 / 255.0;
            var a = (byte)Math.Round(255 * (0.08 + (full - 0.08) * p));
            card.Color = IsDark
                ? Color.FromArgb(a, 0x1C, 0x1B, 0x1F)
                : Color.FromArgb(a, 0xFF, 0xFF, 0xFF);
        }
        if (res["FieldSurfaceBrush"] is SolidColorBrush field)
        {
            var a2 = (byte)Math.Round(255 * (0.08 + 0.92 * p));
            field.Color = IsDark
                ? Color.FromArgb(a2, 0x36, 0x34, 0x3B)
                : Color.FromArgb(a2, 0xF4, 0xF1, 0xF7);
        }
    }

    internal static Bitmap? LoadLocalBitmap(string? path)
    {
        try { return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : new Bitmap(path); }
        catch { return null; }
    }

    private NativeMenu BuildTrayMenu()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem(S.Get("tray.open"));
        open.Click += (_, _) => RestoreFromTray();
        var exit = new NativeMenuItem(S.Get("tray.exit"));
        exit.Click += (_, _) => { _forceExit = true; Close(); };
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);
        return menu;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {

        var st = AppSettings.Shared;
        if (WindowState != WindowState.Maximized && Width > 0 && Height > 0)
        {
            st.WindowWidth = (int)Width;
            st.WindowHeight = (int)Height;
        }
        st.Save();

        if (_forceExit || !AppSettings.Shared.TrayMinimize)
        {
            return;
        }
        e.Cancel = true;
        Hide();
        if (_trayIcon != null) _trayIcon.IsVisible = true;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon != null) _trayIcon.IsVisible = false;
    }

    private void OnLanguageChanged() =>
        Dispatcher.UIThread.Post(RebuildCurrentPage, DispatcherPriority.Background);

    private void RebuildCurrentPage()
    {
        _modpackPage = new Views.ModpackView();
        _settingsPage = new Views.SettingsView();
        _launcherPage = new Views.LauncherView();

        ToolTip.SetTip(NavLauncher, S.Get("nav.launcherTip"));
        ToolTip.SetTip(NavModpack, S.Get("nav.modpackTip"));
        ToolTip.SetTip(NavSettings, S.Get("nav.settings"));

        if (_trayIcon != null) _trayIcon.Menu = BuildTrayMenu();
        UpdateTopBarIdentity();
        SelectNav(_currentNavKey);
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_SYSCOMMAND = 0x0112;
    private const IntPtr SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120, SC_CLOSE = 0xF060;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        HideDrawnTitleBar();
    }

    private void HideDrawnTitleBar()
    {
        var found = FindDrawnDecorations();
        if (found != null)
        {
            SetHasTitleBar(found, false);
            return;
        }

        var retries = 0;
        DispatcherTimer.Run(() =>
        {
            var wdd = FindDrawnDecorations();
            if (wdd == null) return ++retries < 20;
            SetHasTitleBar(wdd, false);
            return false;
        }, TimeSpan.FromMilliseconds(100));
    }

    private static void SetHasTitleBar(WindowDrawnDecorations wdd, bool value)
    {
        var prop = typeof(WindowDrawnDecorations).GetProperty(nameof(WindowDrawnDecorations.HasTitleBar));
        prop?.GetSetMethod(true)?.Invoke(wdd, new object[] { value });
    }

    private WindowDrawnDecorations FindDrawnDecorations()
    {

        foreach (var child in this.GetLogicalDescendants())
            if (child is WindowDrawnDecorations wdd)
                return wdd;
        return null;
    }

    private void SendSysCommand(IntPtr sc)
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && PostMessage(hwnd, WM_SYSCOMMAND, sc, IntPtr.Zero)) return;

        if (sc == SC_MINIMIZE) WindowState = WindowState.Minimized;
        else if (sc == SC_MAXIMIZE) WindowState = WindowState.Maximized;
        else if (sc == SC_RESTORE) WindowState = WindowState.Normal;
        else Close();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => SendSysCommand(SC_MINIMIZE);

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        SendSysCommand(WindowState == WindowState.Maximized ? SC_RESTORE : SC_MAXIMIZE);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => SendSysCommand(SC_CLOSE);

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {

        if (FindButtonAncestor(e.Source as Control) != null) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FindButtonAncestor(e.Source as Control) != null) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private static Button? FindButtonAncestor(Control? source)
    {
        while (source != null)
        {
            if (source is Button b) return b;
            source = source.Parent as Control;
        }
        return null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == WindowStateProperty && CapMax != null!)
            CapMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private static readonly double[] NavSlotY = { 0, 62, 124 };
    private const double NavSlotStep = 62.0;
    private static readonly string[] NavKeys = { "launcher", "modpack", "settings" };
    private bool _navDragging;
    private bool _navConsumeClick;
    private DispatcherTimer? _navPressTimer;
    private Point _navLastPoint;
    private IPointer? _navPointer;

    private int SelectedNavIndex()
    {
        if (NavModpack.Classes.Contains("selected")) return 1;
        if (NavSettings.Classes.Contains("selected")) return 2;
        return 0;
    }

    private void MoveNavIndicator(int index)
    {
        if (index < 0) return;
        SetNavIndicator(NavSlotY[index], 1);
    }

    private void SetNavIndicator(double y, double scale)
    {
        NavIndicator.RenderTransform = TransformOperations.Parse($"translateY({y:0.#}px)");
        NavIndicatorScale.RenderTransform = TransformOperations.Parse($"scale({scale:0.##})");
    }

    private int NearestNavSlot(Point p)
    {
        var y = Math.Clamp(p.Y - 10 - 28, 0, NavSlotY[^1] + NavSlotStep / 2);
        return Math.Clamp((int)Math.Round(y / NavSlotStep), 0, NavSlotY.Length - 1);
    }

    private void OnNavPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _navLastPoint = e.GetPosition(NavPillPanel);
        _navPointer = e.Pointer;
        _navConsumeClick = false;
        _navPressTimer?.Stop();
        _navPressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Normal, Dispatcher, (_, _) =>
        {
            _navPressTimer!.Stop();
            _navDragging = true;
            _navConsumeClick = true;
            _navPointer?.Capture(NavPillPanel);
            SetNavIndicator(NavSlotY[NearestNavSlot(_navLastPoint)], 0.92);
        });
        _navPressTimer.Start();
    }

    private void OnNavMoved(object? sender, PointerEventArgs e)
    {
        _navLastPoint = e.GetPosition(NavPillPanel);
        if (_navDragging) SetNavIndicator(NavSlotY[NearestNavSlot(_navLastPoint)], 0.92);
    }

    private void OnNavReleased(object? sender, PointerReleasedEventArgs e) => EndNavDrag();

    private void OnNavCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndNavDrag();

    private void EndNavDrag()
    {
        _navPressTimer?.Stop();
        if (!_navDragging) return;
        _navDragging = false;
        _navPointer?.Capture(null);
        var idx = NearestNavSlot(_navLastPoint);
        SelectNav(NavKeys[idx]);
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (_navConsumeClick) { _navConsumeClick = false; return; }
        if (sender is Button b && b.Tag is string key)
            SelectNav(key);
    }

    private void SelectNav(string key)
    {
        _currentNavKey = key;
        NavLauncher.Classes.Set("selected", key == "launcher");
        NavModpack.Classes.Set("selected", key == "modpack");
        NavSettings.Classes.Set("selected", key == "settings");

        NavLauncher.Classes.Remove("raised");
        NavModpack.Classes.Remove("raised"); NavSettings.Classes.Remove("raised");
        _selectedNavBtn = key switch
        {
            "modpack" => NavModpack,
            "settings" => NavSettings,
            _ => NavLauncher,
        };
        MoveNavIndicator(SelectedNavIndex());
        var page = key switch
        {
            "modpack" => _modpackPage,
            "settings" => _settingsPage,
            _ => _launcherPage,
        };

        Dispatcher.UIThread.Post(() => { PageHost.Content = page; FadeIn(page); }, DispatcherPriority.Background);
    }

    internal static void FadeIn(Control? c, int ms = 600)
    {
        if (c == null) return;
        c.Transitions = null;
        c.Opacity = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        int elapsed = 0;
        timer.Tick += (_, _) =>
        {
            elapsed += 16;
            double p = Math.Min(1.0, elapsed / (double)ms);
            c.Opacity = 1 - Math.Pow(1 - p, 3);
            if (p >= 1) { timer.Stop(); c.Opacity = 1; }
        };
        timer.Start();
    }

    public static void RefreshLauncherInstances() =>
        (_instance?._launcherPage as Views.LauncherView)?.ExternalRefresh();

}
