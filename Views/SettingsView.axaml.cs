using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace StarryLauncher.Views;

public partial class SettingsView : UserControl
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private bool _syncing;

    private readonly Control?[] _pages = new Control?[3];
    private int _tab;
    private static int _lastTab;

    public SettingsView()
    {
        InitializeComponent();
        _pages[0] = PageAppearance;
        _pages[1] = PageGeneral;
        _pages[2] = PageStorageLoc;

        TabPanel.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() => MoveTabIndicator(_tab), DispatcherPriority.Background);

        SyncFromSettings();

        TabBtn0.Content = SvgIcon.IconText("wg", S.Get("set.tab.appearance"));

        TabBtn1.Content = SvgIcon.IconText("cg", S.Get("set.tab.general"));
        TabBtnSt.Content = SvgIcon.IconText("xqwj", S.Get("set.tab.storage"));

        IcBgPage.Source = SvgIcon.Get("bj");
        IcBgChat.Source = SvgIcon.Get("bj");

        StCardIcon.Source = SvgIcon.Get("xqwj");
        BtnMcPathIcon.Source = SvgIcon.Get("xqwj");
        SyncMcPathField();
        AccentTitleIcon.Source = SvgIcon.Get("ys");
        TrayCardIcon.Source = SvgIcon.Get("tzkb");
        LangCardIcon.Source = SvgIcon.Get("fy");
        FontCardIcon.Source = SvgIcon.Get("zt");
        ThemeCardIcon.Source = SvgIcon.Get("tsys");

        TrayCardTitle.FontSize = 17; TrayCardTitle.FontWeight = Avalonia.Media.FontWeight.Bold;
        LangCardTitle.FontSize = 17; LangCardTitle.FontWeight = Avalonia.Media.FontWeight.Bold;
        FontCardTitle.FontSize = 17; FontCardTitle.FontWeight = Avalonia.Media.FontWeight.Bold;
        ThemeCardTitle.FontSize = 17; ThemeCardTitle.FontWeight = Avalonia.Media.FontWeight.Bold;

        BtnPageBgClear.Content = new Image { Width = 18, Height = 18, Source = SvgIcon.Get("qk") };
        BtnPageBgClear.MinWidth = 44;
        BtnChatBgClear.Content = new Image { Width = 18, Height = 18, Source = SvgIcon.Get("qk") };
        BtnChatBgClear.MinWidth = 44;

        foreach (var (id, (a, b)) in MainWindow.ThemeGradients)
        {
            var g = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(new Point(0, 0), RelativeUnit.Relative),
                EndPoint = new RelativePoint(new Point(1, 1), RelativeUnit.Relative),
            };
            g.GradientStops.Add(new GradientStop(a, 0));
            g.GradientStops.Add(new GradientStop(b, 1));
            GradientSwatches.Children.Add(new Button
            {
                Classes = { "swatch" },
                Tag = "grad:" + id,
                Background = g,
            });
            ((Button)GradientSwatches.Children[^1]).Click += OnGradientPick;
        }

        BtnAvatar.Click += async (_, _) => await PickAvatarAsync();
        BtnIdentityName.Click += async (_, _) => await EditNicknameAsync();
        BtnMcFace.Click += async (_, _) => await ApplyMcFaceAsync();

        BtnPageBgPick.Click += async (_, _) => await PickPageBgAsync();
        BtnPageBgClear.Click += (_, _) =>
        {
            _settings.PageBgPath = "";
            _settings.Save();
            MainWindow.ApplyAppearance();
        };
        SldPageBlur.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.PageBgBlur = SldPageBlur.Value;
            ValPageBlur.Text = $"{SldPageBlur.Value:0}";
            _settings.Save();
            MainWindow.ApplyAppearance();
        };
        SldPageVis.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.PageBgVisibility = SldPageVis.Value;
            ValPageVis.Text = $"{SldPageVis.Value:0}%";
            _settings.Save();
            MainWindow.ApplyAppearance();
        };
        SldCompOpacity.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.ComponentOpacity = SldCompOpacity.Value;
            ValCompOpacity.Text = $"{SldCompOpacity.Value:0}%";
            _settings.Save();
            MainWindow.ApplyComponentOpacity(SldCompOpacity.Value);
        };
        BtnChatBgPick.Click += async (_, _) => await PickChatBgAsync();
        BtnChatBgClear.Click += (_, _) =>
        {
            _settings.ChatBgPath = "";
            _settings.Save();
            ProfileSync.RaiseBackground();
        };
        SldChatBlur.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.ChatBgBlur = SldChatBlur.Value;
            ValChatBlur.Text = $"{SldChatBlur.Value:0}";
            _settings.Save();
            ProfileSync.RaiseBackground();
        };
        SldChatVis.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.ChatBgVisibility = SldChatVis.Value;
            ValChatVis.Text = $"{SldChatVis.Value:0}%";
            _settings.Save();
            ProfileSync.RaiseBackground();
        };

        TrayToggle.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.TrayMinimize = TrayToggle.IsChecked == true;
            _settings.Save();
        };
        LangBox.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var lang = LangBox.SelectedIndex switch
            {
                1 => UiLanguage.ZhTW,
                2 => UiLanguage.En,
                _ => UiLanguage.ZhCN,
            };
            S.Set(lang);
            _settings.Language = S.ToSettingValue(lang);
            _settings.Save();
        };

        RefreshFontBox();
        FontBox.DropDownOpened += (_, _) => RefreshFontBox();
        FontBox.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.UiFont = FontBox.SelectedIndex > 0
                ? (FontBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ""
                : "";
            _settings.Save();
            MainWindow.ApplyUiFont();
        };

        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _settings.ThemeMode = ThemeBox.SelectedIndex switch
            {
                0 => "dark",
                2 => "system",
                _ => "light",
            };
            _settings.Save();
            MainWindow.ApplyThemeColor(animate: true);
        };

    }

    private void SelectTab(int idx)
    {
        _tab = idx;
        _lastTab = idx;
        var buttons = new[] { TabBtn0, TabBtn1, TabBtnSt };
        for (int i = 0; i < buttons.Length; i++)
            buttons[i].Classes.Set("selected", i == idx);

        Dispatcher.UIThread.Post(() => MoveTabIndicator(_tab), DispatcherPriority.Background);
        var page = _pages[idx];
        if (page == null) return;

        PageStorage.Children.Remove(page);
        TabHost.Content = page;
        MainWindow.FadeIn(page);
    }

    private bool _tabDragging;
    private bool _tabConsumeClick;
    private DispatcherTimer? _tabPressTimer;
    private Point _tabLastPos;
    private IPointer? _tabPointer;

    private void OnTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_tabConsumeClick) { _tabConsumeClick = false; return; }
        if (sender is Button b && b.Tag is string s && int.TryParse(s, out var idx))
            SelectTab(idx);
    }

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabPanel).Properties.IsLeftButtonPressed) return;
        _tabLastPos = e.GetPosition(TabList);
        _tabPointer = e.Pointer;
        _tabConsumeClick = false;
        _tabPressTimer?.Stop();
        _tabPressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Normal, (_, _) =>
        {
            _tabPressTimer!.Stop();
            _tabDragging = true;
            _tabConsumeClick = true;
            _tabPointer?.Capture(TabPanel);
            MoveTabIndicator(NearestTabIndex(_tabLastPos));
        });
        _tabPressTimer.Start();
    }

    private void OnTabMoved(object? sender, PointerEventArgs e)
    {
        _tabLastPos = e.GetPosition(TabList);
        if (_tabDragging) MoveTabIndicator(NearestTabIndex(_tabLastPos));
    }

    private void OnTabReleased(object? sender, PointerReleasedEventArgs e) => EndTabDrag();

    private void OnTabCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndTabDrag();

    private void EndTabDrag()
    {
        _tabPressTimer?.Stop();
        if (!_tabDragging) return;
        _tabDragging = false;
        _tabPointer?.Capture(null);
        SelectTab(NearestTabIndex(_tabLastPos));
    }

    private int NearestTabIndex(Point p)
    {
        var buttons = new[] { TabBtn0, TabBtn1, TabBtnSt };
        double best = double.MaxValue;
        int idx = _tab;
        for (int i = 0; i < buttons.Length; i++)
        {
            var c = buttons[i].TranslatePoint(new Point(0, buttons[i].Bounds.Height / 2), TabPanel);
            if (c == null) continue;
            double d = Math.Abs(c.Value.Y - p.Y);
            if (d < best) { best = d; idx = i; }
        }
        return idx;
    }

    private void MoveTabIndicator(int index)
    {
        if (index < 0) return;
        var buttons = new[] { TabBtn0, TabBtn1, TabBtnSt };
        var b = buttons[index];
        var top = b.TranslatePoint(new Point(0, 0), TabPanel);
        if (top == null || b.Bounds.Height <= 0) return;
        TabIndicator.Height = b.Bounds.Height;
        TabIndicator.IsVisible = true;
        TabIndicator.RenderTransform = TransformOperations.Parse($"translateY({top.Value.Y:0.#}px)");
    }

    private void OnAccentPick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hex) return;
        _settings.ThemeColor = hex;
        _settings.ThemeGradient = "";
        _settings.Save();
        MainWindow.ApplyThemeColor();
        RefreshSwatchRings(hex);
    }

    private void OnGradientPick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag || !tag.StartsWith("grad:")) return;
        _settings.ThemeGradient = tag[5..];
        _settings.Save();
        MainWindow.ApplyThemeColor();
        RefreshSwatchRings(_settings.ThemeColor);
    }

    private Color _customColor = Color.FromRgb(0x67, 0x50, 0xA4);

    private void OnCustomColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (!MainWindow.TryParseHex(_settings.ThemeColor, out var opened) || opened == default)
            opened = Color.FromRgb(0x67, 0x50, 0xA4);
        _customColor = opened;
        var (hue0, sat0, val0) = RgbToHsv(opened);
        double hue = hue0, sat = sat0, val = val0;
        const double SvW = 210, SvH = 150, HueW = 16, HueH = 150;

        var svBase = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true };
        var svOverlay = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true };
        var svMarker = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 14, Height = 14, StrokeThickness = 2, Stroke = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        var svArea = new Panel { Width = SvW, Height = SvH, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross) };
        svArea.Children.Add(svBase);
        svArea.Children.Add(svOverlay);
        svArea.Children.Add(svMarker);

        var hueThumb = new Border
        {
            Width = HueW + 8, Height = 6, CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var hueBar = new Border
        {
            Width = HueW, Height = HueH, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        {
            var g = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(new Point(0, 0), RelativeUnit.Relative),
                EndPoint = new RelativePoint(new Point(0, 1), RelativeUnit.Relative),
            };
            for (var i = 0; i <= 6; i++)
                g.GradientStops.Add(new GradientStop(HsvToRgb(i * 60 % 360, 1, 1), i / 6.0));
            hueBar.Background = g;
        }
        var hueWrap = new Panel { Width = HueW, Height = HueH };
        hueWrap.Children.Add(hueBar);
        hueWrap.Children.Add(hueThumb);

        var previewNew = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(opened) };
        var previewCur = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(opened) };
        var previewStack = new StackPanel { Spacing = 4 };
        previewStack.Children.Add(new TextBlock
        {
            Text = S.Get("set.accent.new"), FontSize = 11, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Foreground = Application.Current!.Resources["OnSurfaceVarBrush"] as IBrush,
        });
        previewStack.Children.Add(previewNew);
        previewStack.Children.Add(new TextBlock
        {
            Text = S.Get("set.accent.current"), FontSize = 11, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Foreground = Application.Current!.Resources["OnSurfaceVarBrush"] as IBrush,
        });
        previewStack.Children.Add(previewCur);

        var hexBox = new TextBox { Text = Hex(opened), MinWidth = 96, FontSize = 13 };

        var topRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        topRow.Children.Add(svArea);
        topRow.Children.Add(hueWrap);
        var bottomRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 10, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        bottomRow.Children.Add(previewStack);
        var rightCol = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Spacing = 6 };
        rightCol.Children.Add(new TextBlock
        {
            Text = "HEX", FontSize = 11, Foreground = Application.Current!.Resources["OnSurfaceVarBrush"] as IBrush,
        });
        rightCol.Children.Add(hexBox);
        bottomRow.Children.Add(rightCol);

        var panel = new StackPanel { Margin = new Thickness(12, 10) };
        panel.Children.Add(topRow);
        panel.Children.Add(bottomRow);

        void RefreshSvBase()
        {

            var g = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(new Point(0, 0), RelativeUnit.Relative),
                EndPoint = new RelativePoint(new Point(1, 0), RelativeUnit.Relative),
            };
            g.GradientStops.Add(new GradientStop(Colors.White, 0));
            g.GradientStops.Add(new GradientStop(HsvToRgb(hue, 1, 1), 1));
            svBase.Background = g;

            var g2 = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(new Point(0, 0), RelativeUnit.Relative),
                EndPoint = new RelativePoint(new Point(0, 1), RelativeUnit.Relative),
            };
            g2.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            g2.GradientStops.Add(new GradientStop(Colors.Black, 1));
            svOverlay.Background = g2;
        }

        void Update(bool refreshHex = true)
        {
            _customColor = HsvToRgb(hue, sat, val);
            svMarker.Margin = new Thickness(sat * SvW - 7, (1 - val) * SvH - 7, 0, 0);
            hueThumb.Margin = new Thickness(-4, hue / 360 * HueH - 3, 0, 0);
            previewNew.Background = new SolidColorBrush(_customColor);

            MainWindow.ApplyThemeColor(Hex(_customColor));
            if (refreshHex) hexBox.Text = Hex(_customColor);
        }

        bool svDragging = false;
        void SvUpdate(Avalonia.Input.PointerEventArgs e)
        {
            var p = e.GetPosition(svArea);
            sat = Math.Clamp(p.X / SvW, 0, 1);
            val = Math.Clamp(1 - p.Y / SvH, 0, 1);
            Update();
        }
        svArea.PointerPressed += (_, e) => { svDragging = true; e.Pointer.Capture(svArea); SvUpdate(e); };
        svArea.PointerMoved += (_, e) => { if (svDragging) SvUpdate(e); };
        svArea.PointerReleased += (_, e) => { svDragging = false; };

        bool hueDragging = false;
        void HueUpdate(Avalonia.Input.PointerEventArgs e)
        {
            var p = e.GetPosition(hueWrap);
            hue = Math.Clamp(p.Y / HueH, 0, 0.999) * 360;
            RefreshSvBase();
            Update();
        }
        hueBar.PointerPressed += (_, e) => { hueDragging = true; e.Pointer.Capture(hueBar); HueUpdate(e); };
        hueBar.PointerMoved += (_, e) => { if (hueDragging) HueUpdate(e); };
        hueBar.PointerReleased += (_, e) => { hueDragging = false; };

        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            if (MainWindow.TryParseHex(hexBox.Text?.Trim(), out var c) && c != default)
            {
                (hue, sat, val) = RgbToHsv(c);
                RefreshSvBase();
                Update(refreshHex: false);
            }
            else Update();
        };

        RefreshSvBase();
        Update(refreshHex: false);

        var flyout = new Flyout { Content = panel };
        flyout.Closed += (_, _) =>
        {

            _settings.ThemeColor = Hex(_customColor);
            _settings.ThemeGradient = "";
            _settings.Save();
            MainWindow.ApplyThemeColor();
            RefreshSwatchRings(_settings.ThemeColor);
        };
        btn.Flyout = flyout;
        flyout.ShowAt(btn);
    }

    private void RefreshSwatchRings(string? currentHex)
    {
        foreach (var child in AccentSwatches.Children.OfType<Button>())
            SetRing(child, string.IsNullOrEmpty(_settings.ThemeGradient) &&
                           !string.IsNullOrEmpty(currentHex) &&
                           string.Equals(child.Tag as string, currentHex, StringComparison.OrdinalIgnoreCase));
        foreach (var child in GradientSwatches.Children.OfType<Button>())
            SetRing(child, child.Tag is string t && t.StartsWith("grad:") &&
                           string.Equals(t[5..], _settings.ThemeGradient, StringComparison.Ordinal));
    }

    private static void SetRing(Button b, bool selected)
    {
        b.BorderThickness = selected ? new Thickness(2.5) : new Thickness(0);
        b.BorderBrush = selected
            ? Application.Current!.Resources["OnSurfaceBrush"] as IBrush
            : null;
    }

    private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    private static (double h, double s, double v) RgbToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        double r = 0, g = 0, b = 0;
        switch (h)
        {
            case < 60: (r, g, b) = (c, x, 0); break;
            case < 120: (r, g, b) = (x, c, 0); break;
            case < 180: (r, g, b) = (0, c, x); break;
            case < 240: (r, g, b) = (0, x, c); break;
            case < 300: (r, g, b) = (x, 0, c); break;
            default: (r, g, b) = (c, 0, x); break;
        }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static SettingsView? _live;

    public static void SyncFromAgent()
    {
        var v = _live;
        if (v == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { v.SyncFromSettings(); }
            catch {   }
        }, DispatcherPriority.Background);
    }

    private void RefreshFontBox()
    {
        List<string> all;
        try
        {
            all = Avalonia.Media.FontManager.Current.SystemFonts.Select(f => f.Name).ToList();
        }
        catch { all = []; }

        static bool HasChinese(string name)
        {
            try
            {
                if (!Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(name), out var gt))
                    return false;
                return gt.CanShapeScript(Avalonia.Media.TextFormatting.Unicode.Script.Han);
            }
            catch { return false; }
        }

        var fonts = all
            .OrderByDescending(HasChinese)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool prev = _syncing;
        _syncing = true;
        try
        {
            var items = new List<ComboBoxItem> { new() { Content = S.Get("settings.fontDefault") } };
            foreach (var f in fonts)
            {
                items.Add(new ComboBoxItem
                {
                    Content = f,
                    Tag = f,
                    FontFamily = new Avalonia.Media.FontFamily(f),
                });
            }
            FontBox.ItemsSource = items;
            FontBox.SelectedIndex = string.IsNullOrWhiteSpace(_settings.UiFont)
                ? 0
                : fonts.IndexOf(_settings.UiFont) + 1;
        }
        finally { _syncing = prev; }
    }

    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {

            ImgIdentityAvatar.Source = MainWindow.LoadLocalBitmap(_settings.UserAvatarPath) ?? UserDefaultAvatar;
            BtnIdentityName.Content = string.IsNullOrWhiteSpace(_settings.DisplayName) ? "玩家" : _settings.DisplayName;

            SldPageBlur.Value = _settings.PageBgBlur;
            SldPageVis.Value = _settings.PageBgVisibility;
            SldCompOpacity.Value = _settings.ComponentOpacity;
            SldChatBlur.Value = _settings.ChatBgBlur;
            SldChatVis.Value = _settings.ChatBgVisibility;
            ValPageBlur.Text = $"{_settings.PageBgBlur:0}";
            ValPageVis.Text = $"{_settings.PageBgVisibility:0}%";
            ValCompOpacity.Text = $"{_settings.ComponentOpacity:0}%";
            ValChatBlur.Text = $"{_settings.ChatBgBlur:0}";
            ValChatVis.Text = $"{_settings.ChatBgVisibility:0}%";

            foreach (var child in AccentSwatches.Children.OfType<Button>())
            {
                if (child.Tag is string hex && MainWindow.TryParseHex(hex, out var c))
                    child.Background = new SolidColorBrush(c);
            }
            RefreshSwatchRings(_settings.ThemeColor);

            TrayToggle.IsChecked = _settings.TrayMinimize;
            LangBox.SelectedIndex = S.Parse(_settings.Language) switch
            {
                UiLanguage.ZhTW => 1,
                UiLanguage.En => 2,
                _ => 0,
            };
            RefreshFontBox();
            ThemeBox.SelectedIndex = _settings.ThemeMode switch
            {
                "dark" => 0,
                "system" => 2,
                _ => 1,
            };
        }
        finally { _syncing = false; }

        if (_pages[0] != null && TabHost.Content == null) SelectTab(_lastTab);
    }

    private static IImage? _userDefaultAvatar;
    private static IImage UserDefaultAvatar => _userDefaultAvatar ??= SvgIcon.Get("avatar", "#9E9E9E");

    private async Task PickAvatarAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = S.Get("ai.pickAvatarUser"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(S.Get("ai.fileTypeImage")) { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.ico", "*.webp"] }],
        });
        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath;

        string official = Path.Combine(AppDirs.Tx, "avatar_user.png");
        string temp = official + ".tmp" + Path.GetExtension(path);
        try { File.Copy(path, temp, overwrite: true); }
        catch (Exception ex) { AppLog.Warn($"头像副本复制失败: {ex.Message}"); return; }

        var win = new AvatarCropWindow(temp, official);
        if (top is Window owner) await win.ShowDialog(owner);
        else win.Show();
        try { File.Delete(temp); } catch { }
        if (win.ResultPath == null) return;

        _settings.UserAvatarPath = official;
        _settings.Save();
        ImgIdentityAvatar.Source = MainWindow.LoadLocalBitmap(_settings.UserAvatarPath) ?? UserDefaultAvatar;
        ProfileSync.Raise();
        ShowResult(S.Get("set.identity.saved"));
    }

    private async Task ApplyMcFaceAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var nick = _settings.McUserName?.Trim() ?? "";
        if (nick.Length == 0)
        {

            var win = new TextInputDialog(S.Get("set.identity.mcface"), "", "", maxLength: 16);
            if (top is Window owner) await win.ShowDialog(owner);
            else win.Show();
            if (!win.Confirmed || string.IsNullOrWhiteSpace(win.InputText)) return;
            nick = win.InputText.Trim();
        }
        var face = await McFaceService.FetchAsync(nick, top as Window);
        if (face is not Bitmap bmp)
        {
            ShowResult(S.Get("set.identity.mcfaceFail"));
            return;
        }
        string official = Path.Combine(AppDirs.Tx, "avatar_user.png");
        Directory.CreateDirectory(AppDirs.Tx);
        bmp.Save(official);
        _settings.UserAvatarPath = official;
        _settings.Save();
        ImgIdentityAvatar.Source = MainWindow.LoadLocalBitmap(_settings.UserAvatarPath) ?? UserDefaultAvatar;
        ProfileSync.Raise();
        ShowResult(S.Get("set.identity.saved"));
    }

    private async Task EditNicknameAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var win = new TextInputDialog(S.Get("set.name.title"), "",
            string.IsNullOrWhiteSpace(_settings.DisplayName) ? "玩家" : _settings.DisplayName, maxLength: 6);
        if (top is Window owner) await win.ShowDialog(owner);
        else win.Show();
        if (!win.Confirmed) return;

        _settings.UserName = win.InputText?.Trim() ?? "";
        _settings.UserNameCustom = !string.IsNullOrWhiteSpace(_settings.UserName);
        _settings.UserNameMachine = _settings.UserNameCustom ? Environment.MachineName : "";
        _settings.Save();
        BtnIdentityName.Content = _settings.UserName;
        ProfileSync.Raise();
    }

    private async Task PickPageBgAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = S.Get("dlg.pickPageBg"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(S.Get("ai.fileTypeImage")) { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"] }],
        });
        if (files.Count == 0) return;
        _settings.PageBgPath = files[0].Path.LocalPath;
        _settings.Save();
        MainWindow.ApplyAppearance();
    }

    private async Task PickChatBgAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = S.Get("dlg.pickChatBg"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(S.Get("ai.fileTypeImage")) { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"] }],
        });
        if (files.Count == 0) return;
        _settings.ChatBgPath = files[0].Path.LocalPath;
        _settings.Save();
        ProfileSync.RaiseBackground();
    }

    private void SyncMcPathField()
    {
        McPathCurrentLabel.Text = S.Get("set.path.current");
        BtnMcPathDetectText.Text = S.Get("set.path.autoDetect");
        McPathCurrentText.Text = S.Get("set.path.labelFixed");

        if (McPathOverlay.IsVisible) BuildMcPathList();
    }

    private void OnToggleMcPathDrop(object? sender, RoutedEventArgs e)
    {
        BuildMcPathList();
        MainWindow.BindAcrylic(McPathAcrylic, McPathWhiteBg);
        McPathDim.IsVisible = true;
        McPathOverlay.IsVisible = true;
    }

    private void OnMcPathDimTapped(object? sender, Avalonia.Input.TappedEventArgs e) => CloseMcPathOverlay();
    private void OnMcPathClose(object? sender, RoutedEventArgs e) => CloseMcPathOverlay();
    private void CloseMcPathOverlay()
    {
        McPathDim.IsVisible = false;
        McPathOverlay.IsVisible = false;
    }

    private void BuildMcPathList()
    {
        McPathPickerList.Children.Clear();
        var dirs = LauncherService.FindGameDirs();
        var def = LauncherService.NormalizeGameRoot(AppSettings.Shared.McGameDir);
        var onSurface = Application.Current?.Resources["OnSurfaceBrush"] as IBrush ?? Brushes.Black;
        var onVar = Application.Current?.Resources["OnSurfaceVarBrush"] as IBrush ?? Brushes.Gray;
        foreach (var dir in dirs)
        {
            var isDefault = string.Equals(dir, def, StringComparison.OrdinalIgnoreCase);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

            var textStack = new StackPanel
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 8, 0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            var alias = new TextBlock
            {
                Text = LauncherService.GetGamePathAlias(dir), FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = isDefault ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
                Foreground = isDefault ? (Application.Current?.Resources["PrimaryBrush"] as IBrush ?? Brushes.Black) : onSurface,
            };
            var pathLine = new TextBlock
            {
                Text = dir, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = onVar,
            };
            textStack.Children.Add(alias);
            textStack.Children.Add(pathLine);
            textStack.Tapped += (_, _) =>
            {
                LauncherService.SetDefaultGamePath(dir);
                SyncMcPathField();
                CloseMcPathOverlay();
                MainWindow.RefreshLauncherInstances();
            };
            row.Children.Add(textStack);

            var edit = new Button
            {
                Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 2, 2, 2),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Content = new TextBlock
                {
                    Text = "\uE70F", FontSize = 15,
                    FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets",
                },
            };
            ToolTip.SetTip(edit, S.Get("set.path.rename"));
            edit.Click += (_, _) => RenameMcPath(dir);
            Grid.SetColumn(edit, 1);
            row.Children.Add(edit);

            var del = new Button
            {
                Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 2, 2, 2),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Content = new Image { Width = 16, Height = 16, Source = SvgIcon.Get("qk") },
            };
            ToolTip.SetTip(del, S.Get("set.path.deleted"));
            del.Click += (_, _) =>
            {
                LauncherService.RemoveGamePath(dir);
                SyncMcPathField();
                MainWindow.RefreshLauncherInstances();

                BuildMcPathList();
            };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);
            McPathPickerList.Children.Add(row);
        }
        if (dirs.Count == 0)
            McPathPickerList.Children.Add(new TextBlock
            {
                Text = S.Get("set.path.noPaths"), FontSize = 12.5, Margin = new Thickness(6, 8),
                Foreground = Application.Current?.Resources["OnSurfaceVarBrush"] as IBrush ?? Brushes.Gray,
            });
    }

    private async void RenameMcPath(string dir)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is not Window owner) return;
        var win = new TextInputDialog(S.Get("set.path.renameTitle"), S.Get("set.path.renameHint"),
            LauncherService.GetGamePathAlias(dir), 40);
        await win.ShowDialog(owner);
        if (!win.Confirmed) return;
        LauncherService.SetGamePathAlias(dir, win.InputText);
        SyncMcPathField();
        BuildMcPathList();
        MainWindow.RefreshLauncherInstances();
    }

    private bool _mcScanning;
    private async void OnAutoDetectMcPaths(object? sender, RoutedEventArgs e)
    {
        if (_mcScanning) return;
        _mcScanning = true;
        BtnMcPathDetect.IsEnabled = false;
        BtnMcPathPick.IsEnabled = false;
        McScanProgressHost.IsVisible = true;
        McScanBar.Value = 0;
        McScanStatus.Text = S.Get("set.path.scanStarting");

        try
        {
            var progress = new Progress<LauncherService.GameFolderScanProgress>(p =>
            {
                McScanBar.Value = p.Total > 0 ? (double)p.Done * 100 / p.Total : 0;
                McScanStatus.Text = S.F("set.path.scanning", p.Done, p.Total);
            });
            var found = await LauncherService.DetectGameFoldersAsync(progress);
            int added = LauncherService.CommitDetectedPaths(found);
            McScanStatus.Text = S.F("set.path.scanDone", added);
            SyncMcPathField();
            BuildMcPathList();
            MainWindow.RefreshLauncherInstances();
        }
        catch (Exception ex)
        {
            McScanStatus.Text = S.Get("set.path.scanFail");
            AppLog.Info($"[设置] 自动探测失败: {ex.Message}");
        }
        finally
        {
            _mcScanning = false;
            BtnMcPathDetect.IsEnabled = true;
            BtnMcPathPick.IsEnabled = true;
        }
    }

    private async void OnPickMcPath(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        if (top is Window w0) { w0.Show(); w0.Activate(); }
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = S.Get("launcher.chooseDir"),
            AllowMultiple = false,
        });
        var dir = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(dir)) return;
        LauncherService.AddGamePath(dir);
        SyncMcPathField();
        MainWindow.RefreshLauncherInstances();
    }

    private void SetBusy(bool busy, params Button[] buttons)
    {
        foreach (var b in buttons) b.IsEnabled = !busy;
    }

    private void ShowResult(string text) => AppLog.Info($"[设置] {text}");
}

internal sealed class TextInputDialog : Window
{
    private readonly TextBox _box;

    public bool Confirmed { get; private set; }
    public string InputText => _box.Text ?? "";

    public TextInputDialog(string title, string hint, string initial, int maxLength)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = MainWindow.CurrentUiFont;
        Background = Avalonia.Media.Brushes.White;
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        _box = new TextBox { Classes = { "field" }, Text = initial, MaxLength = maxLength };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { Confirm(); }
        };

        var btnOk = new Button { Classes = { "primary" }, Content = S.Get("btn.ok"), MinWidth = 96, MinHeight = 38 };
        btnOk.Click += (_, _) => Confirm();
        var btnCancel = new Button { Classes = { "outlined" }, Content = S.Get("btn.cancel"), MinWidth = 96, MinHeight = 38 };
        btnCancel.Click += (_, _) => Close();

        var root = new StackPanel
        {
            Margin = new Avalonia.Thickness(26, 22),
            Spacing = 12,
            Children =
            {
                new Avalonia.Controls.TextBlock { Text = title, FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Avalonia.Media.Brushes.Black },
                _box,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { btnCancel, btnOk },
                },
            },
        };
        Content = root;
        if (!string.IsNullOrEmpty(hint))
            root.Children.Insert(1, new Avalonia.Controls.TextBlock { Text = hint, FontSize = 12, Foreground = Avalonia.Media.Brushes.Gray });
        AttachedToVisualTree += (_, _) => MainWindow.FadeIn(root);
        Opened += (_, _) => _box.Focus();
    }

    private void Confirm()
    {
        Confirmed = true;
        Close();
    }
}

internal sealed class ServerEditDialog : Window
{
    private readonly TextBox _boxName;
    private readonly TextBox _boxAddr;

    public bool Confirmed { get; private set; }
    public string ServerName => _boxName.Text ?? "";
    public string ServerAddr => _boxAddr.Text ?? "";

    public ServerEditDialog(string title, string name = "", string addr = "")
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = MainWindow.CurrentUiFont;
        Background = Avalonia.Media.Brushes.White;
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        _boxName = new TextBox { Classes = { "field" }, Text = name, PlaceholderText = S.Get("set.et.serverName") };
        _boxAddr = new TextBox
        {
            Classes = { "field" },
            Text = addr,
            PlaceholderText = S.Get("set.et.serverAddrHint"),
        };
        _boxAddr.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Confirm(); };

        var btnOk = new Button { Classes = { "primary" }, Content = S.Get("btn.ok"), MinWidth = 96, MinHeight = 38 };
        btnOk.Click += (_, _) => Confirm();
        var btnCancel = new Button { Classes = { "outlined" }, Content = S.Get("btn.cancel"), MinWidth = 96, MinHeight = 38 };
        btnCancel.Click += (_, _) => Close();

        var root = new StackPanel
        {
            Margin = new Avalonia.Thickness(26, 22),
            Spacing = 12,
            Children =
            {
                new Avalonia.Controls.TextBlock { Text = title, FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Avalonia.Media.Brushes.Black },
                _boxName,
                _boxAddr,
                new Avalonia.Controls.TextBlock { Text = S.Get("set.et.serverAddr"), FontSize = 11.5, Foreground = Avalonia.Media.Brushes.Gray },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { btnCancel, btnOk },
                },
            },
        };
        Content = root;
        AttachedToVisualTree += (_, _) => MainWindow.FadeIn(root);
        Opened += (_, _) => _boxName.Focus();
    }

    private void Confirm() { Confirmed = true; Close(); }
}

public partial class SettingsView : UserControl
{
    private bool _fwBusy;
    private bool _fwSyncing;
    private bool _pingBusy;

    private const string SpeedDlUrl = "https://dldir1.qq.com/weixin/Windows/WeChatSetup.exe";
    private const string SpeedUpUrl = "https://speed.cloudflare.com/__up";

    private static double MeasureBandwidth(bool download, int threads, int windowMs)
    {
        long total = 0;
        using var cts = new System.Threading.CancellationTokenSource(windowMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new Task[threads];
        for (int i = 0; i < threads; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.Timeout = TimeSpan.FromMilliseconds(windowMs + 3000);
                    if (download)
                    {
                        using var resp = await http.GetAsync(SpeedDlUrl,
                            System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        resp.EnsureSuccessStatusCode();
                        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                        var buf = new byte[64 * 1024];
                        while (!cts.IsCancellationRequested)
                        {
                            int n = await stream.ReadAsync(buf, cts.Token);
                            if (n <= 0) break;
                            Interlocked.Add(ref total, n);
                        }
                    }
                    else
                    {
                        var data = new byte[4 * 1024 * 1024];
                        new Random().NextBytes(data);
                        while (!cts.IsCancellationRequested)
                        {
                            using var content = new System.Net.Http.ByteArrayContent(data);
                            using var resp = await http.PostAsync(SpeedUpUrl, content, cts.Token);
                            Interlocked.Add(ref total, data.Length);
                        }
                    }
                }
                catch { }
            });
        }
        Task.WaitAll(tasks);
        sw.Stop();
        double sec = Math.Max(sw.ElapsedMilliseconds, 1) / 1000.0;
        var mbps = total * 8 / sec / 1e6;
        return mbps > 0 ? mbps : -1;
    }

    private string FormatSpeedLine(double down, double up)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (down <= 0 && up <= 0) return S.Get("set.adv.speedFail");
        if (up <= 0) return string.Format(inv, S.Get("set.adv.speedDlOnly"), down.ToString("0.#", inv));
        return string.Format(inv, S.Get("set.adv.dlUp"),
            down.ToString("0.#", inv), up.ToString("0.#", inv));
    }

    private static Avalonia.Controls.TextBlock MakeResultLine(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Foreground = (Avalonia.Media.IBrush)Avalonia.Application.Current!.Resources["OnSurfaceBrush"]!,
    };
}

internal sealed class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialog(string title, string message, bool infoOnly = false)
    {
        Title = title;
        Width = 430;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = MainWindow.CurrentUiFont;
        Background = Avalonia.Media.Brushes.White;
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        var btnOk = new Button { Classes = { "primary" }, Content = S.Get("btn.ok"), MinWidth = 96, MinHeight = 38 };
        btnOk.Click += (_, _) => { Confirmed = true; Close(); };
        var btnCancel = new Button { Classes = { "outlined" }, Content = S.Get("btn.cancel"), MinWidth = 96, MinHeight = 38 };
        btnCancel.Click += (_, _) => Close();

        btnCancel.IsVisible = !infoOnly;

        var root = new StackPanel
        {
            Margin = new Avalonia.Thickness(26, 22),
            Spacing = 16,
            Children =
            {
                new Avalonia.Controls.TextBlock { Text = title, FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Avalonia.Media.Brushes.Black },
                new Avalonia.Controls.TextBlock
                {
                    Text = message,
                    FontSize = 13.5,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x44, 0x40, 0x4A)),
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { btnCancel, btnOk },
                },
            },
        };
        Content = root;
        AttachedToVisualTree += (_, _) => MainWindow.FadeIn(root);
    }
}
