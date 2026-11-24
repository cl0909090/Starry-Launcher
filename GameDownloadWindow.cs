using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using LOrientation = Avalonia.Layout.Orientation;
using VAlign = Avalonia.Layout.VerticalAlignment;

namespace StarryLauncher;

internal sealed class GameDownloadWindow : Window
{
    private readonly StackPanel _listPanel = new() { Spacing = 4 };
    private readonly TextBox _search = new() { MinHeight = 38 };
    private List<McVersionEntry> _versions = [];
    private readonly TextBlock _hint = new()
    {
        FontSize = 12.5,
        Margin = new Thickness(0, 10, 0, 0),
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HAlign.Center,
        Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
    };
    private static IBrush Res(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is IBrush b) return b;
        return new SolidColorBrush(fallback);
    }

    public GameDownloadWindow()
    {
        Title = S.Get("dl.pickTitle");
        Width = 480;
        Height = 600;
        MinWidth = 420;
        MinHeight = 420;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("SurfaceBrush", Colors.White);
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
        root.Children.Add(new TextBlock
        {
            Text = S.Get("dl.pickTitle"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res("OnSurfaceBrush", Colors.Black),
        });
        _search.TextChanged += (_, _) => RebuildList();
        _search.PlaceholderText = S.Get("dl.search");

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 14, 0, 10) };
        Grid.SetColumn(_search, 0);
        searchRow.Children.Add(_search);
        var dlSrcLabel = new TextBlock
        {
            Text = S.Get("modpack.f.dlsrc"), FontSize = 12.5, VerticalAlignment = VAlign.Center,
            Margin = new Thickness(12, 0, 8, 0), Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
        };
        Grid.SetColumn(dlSrcLabel, 1);
        searchRow.Children.Add(dlSrcLabel);
        var dlSrc = new ComboBox { MinHeight = 38, MinWidth = 120, VerticalContentAlignment = VAlign.Center };
        dlSrc.ItemsSource = new List<object>
        {
            new ComboBoxItem { Content = S.Get("dl.src.bmclapi"), Tag = "bmclapi" },
            new ComboBoxItem { Content = S.Get("dl.src.official"), Tag = "official" },
        };
        dlSrc.SelectedIndex = AppSettings.Shared.McDownloadSource == "official" ? 1 : 0;
        dlSrc.SelectionChanged += (_, _) =>
        {
            if (dlSrc.SelectedItem is not ComboBoxItem ci || ci.Tag is not string src) return;
            var st = AppSettings.Shared;
            if (st.McDownloadSource == src) return;
            st.McDownloadSource = src;
            st.Save();
            McInstaller.InvalidateManifestCache();
            _versions = [];
            _hint.Text = S.Get("dl.loading");
            _hint.IsVisible = true;
            _ = LoadListAsync();
        };
        Grid.SetColumn(dlSrc, 2);
        searchRow.Children.Add(dlSrc);
        root.Children.Add(searchRow);

        _hint.Text = S.Get("dl.loading");
        root.Children.Add(_hint);

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        scroll.Content = _listPanel;
        Grid.SetRow(scroll, 1);
        var outer = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        outer.Children.Add(root);
        outer.Children.Add(scroll);
        _ = LoadListAsync();
        Content = outer;
    }

    private async Task LoadListAsync()
    {
        try
        {
            var all = await McInstaller.FetchVersionListAsync();
            _versions = all.Where(v => v.Type == "release").ToList();
            RebuildList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("[mcinstall] 版本列表拉取失败：" + ex.Message);
            _hint.Text = S.Get("dl.listFail");
        }
    }

    private void RebuildList()
    {
        _listPanel.Children.Clear();
        string q = _search.Text?.Trim() ?? "";
        var hits = q.Length == 0
            ? _versions
            : _versions.FindAll(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        if (hits.Count == 0)
        {
            _hint.Text = _versions.Count == 0 ? _hint.Text : S.Get("dl.empty");
            _hint.IsVisible = true;
            return;
        }
        _hint.IsVisible = false;

        foreach (var v in hits)
        {
            string date = v.ReleaseTime.Length >= 10 ? v.ReleaseTime[..10] : v.ReleaseTime;
            var row = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10),
                Background = new SolidColorBrush(Colors.Transparent),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = v,
            };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = v.Id, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VAlign.Center, Foreground = Res("OnSurfaceBrush", Colors.Black) };
            var time = new TextBlock { Text = date, FontSize = 11.5, VerticalAlignment = VAlign.Center, Foreground = Res("OnSurfaceVarBrush", Colors.Gray) };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(time, 1);
            g.Children.Add(name);
            g.Children.Add(time);
            row.Child = g;

            row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(Color.Parse("#18000000"));
            row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Colors.Transparent);
            row.Tapped += OnVersionTapped;
            _listPanel.Children.Add(row);
        }
    }

    private void OnVersionTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not McVersionEntry v) return;
        if (McInstaller.IsVersionRunning(v.Id)) return;

        string dir = McInstaller.ResolveInstallDir();
        try { Directory.CreateDirectory(dir); } catch { }
        AppLog.Info($"[mcinstall] 用户选择版本 {v.Id}，目标目录 {dir}");
        McInstaller.InstallVanilla(v.Id, dir);
        Close();
    }
}
