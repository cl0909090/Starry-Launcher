using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using LOrientation = Avalonia.Layout.Orientation;
using VAlign = Avalonia.Layout.VerticalAlignment;

namespace StarryLauncher;

internal sealed class TaskManagerWindow : Window
{
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBlock _summary = new() { FontSize = 12.5, Foreground = Res("OnSurfaceVarBrush", Colors.Gray) };
    private readonly DispatcherTimer _timer;
    private static TaskManagerWindow? _open;

    public static void Toggle(Window owner)
    {
        if (_open is { IsVisible: true }) { _open.Activate(); return; }
        _open = new TaskManagerWindow { Owner = owner };
        _open.Show(owner);
    }

    public TaskManagerWindow()
    {
        Title = S.Get("dl.task.mgr");
        Width = 700;
        Height = 560;
        MinWidth = 560;
        MinHeight = 420;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("SurfaceBrush", Colors.White);
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        var rootGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(22, 18, 22, 16) };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        titleRow.Children.Add(new TextBlock
        {
            Text = S.Get("dl.task.mgr"), FontSize = 18, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VAlign.Center, Foreground = Res("OnSurfaceBrush", Colors.Black),
        });
        var closeBtn = new Button
        {
            Classes = { "outlined" },
            Content = S.Get("btn.close"),
            MinHeight = 34,
            MinWidth = 88,
            HorizontalAlignment = HAlign.Right,
        };
        closeBtn.Click += (_, _) => Close();
        titleRow.Children.Add(closeBtn);
        rootGrid.Children.Add(titleRow);

        _summary.Margin = new Thickness(0, 0, 0, 10);
        rootGrid.Children.Add(_summary);
        Grid.SetRow(_summary, 1);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        scroll.Content = _list;
        Grid.SetRow(scroll, 2);
        rootGrid.Children.Add(scroll);

        rootGrid.Children.Add(new TextBlock
        {
            Text = S.Get("dl.task.hint"), FontSize = 11.5, Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
        });
        Grid.SetRow(rootGrid.Children[3], 3);

        Content = rootGrid;

        McInstaller.ProgressChanged += OnEvent;
        McInstaller.Completed += OnDone;
        McInstaller.Failed += OnFail;
        McInstaller.Cancelled += OnCancelled;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Rebuild();
        _timer.Start();
        Closed += (_, _) =>
        {
            McInstaller.ProgressChanged -= OnEvent;
            McInstaller.Completed -= OnDone;
            McInstaller.Failed -= OnFail;
            McInstaller.Cancelled -= OnCancelled;
            _timer.Stop();
            _open = null;
        };
        Rebuild();
    }

    private void OnEvent(McInstallProgress p) => Dispatcher.UIThread.Post(Rebuild);
    private void OnDone(string v) => Dispatcher.UIThread.Post(Rebuild);
    private void OnFail(string v, string e) => Dispatcher.UIThread.Post(Rebuild);

    private void OnCancelled(string v) => Dispatcher.UIThread.Post(() =>
    {
        Rebuild();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (McInstaller.ActiveTasks.Count == 0 && !_openDisposed) Close();
        };
        t.Start();
    });

    private bool _openDisposed;

    private void Rebuild()
    {
        var tasks = McInstaller.Tasks;
        var active = tasks.Where(t => !t.Finished).ToList();
        _summary.Text = active.Count == 0
            ? S.Get("dl.task.none")
            : string.Format(S.Get("dl.task.summary"), active.Count,
                tasks.Where(t => !t.Finished).Average(t => t.Percent),
                active.Sum(t => t.SpeedMb));

        _list.Children.Clear();
        if (tasks.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = S.Get("dl.task.empty"), FontSize = 13, HorizontalAlignment = HAlign.Center,
                Margin = new Thickness(0, 30, 0, 0), Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
            });
            return;
        }
        foreach (var t in tasks) _list.Children.Add(BuildCard(t));
    }

    private Border BuildCard(McInstallTask t)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12),
            Background = Res("PadSoftBrush", Colors.Transparent),
        };
        var root = new StackPanel { Spacing = 6 };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var title = new TextBlock
        {
            Text = string.Format(S.Get("dl.installing"), t.Title) + (t.Finished
                ? t.Success ? " — " + S.Get("dl.done")
                    : t.Cancelled ? " — " + S.Get("dl.cancelled")
                    : " — " + S.Get("dl.failed")
                : ""),
            FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VAlign.Center,
            Foreground = Res("OnSurfaceBrush", Colors.Black),
        };
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        if (!t.Finished)
        {
            var cancelBtn = new Button
            {
                Classes = { "outlined" }, Content = S.Get("dl.cancel"), MinHeight = 30, MinWidth = 76,
                HorizontalAlignment = HAlign.Right,
            };
            cancelBtn.Click += (_, _) => { McInstaller.Cancel(t.TaskId); Rebuild(); };
            Grid.SetColumn(cancelBtn, 2);
            head.Children.Add(cancelBtn);
        }
        else
        {
            var status = new TextBlock
            {
                Text = t.Success ? "✓" : t.Cancelled ? S.Get("dl.cancelled") : t.Error ?? "",
                FontSize = 12, VerticalAlignment = VAlign.Center,
                Foreground = t.Success ? Brushes.SeaGreen
                    : t.Cancelled ? Res("OnSurfaceVarBrush", Colors.Gray)
                    : new SolidColorBrush(Color.Parse("#C0392B")),
            };
            Grid.SetColumn(status, 1);
            head.Children.Add(status);
        }
        root.Children.Add(head);

        if (t.Finished && !t.Success && !t.Cancelled && !string.IsNullOrEmpty(t.Error))
        {
            var err = new TextBlock
            {
                Text = t.Error, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 0, 0), Foreground = new SolidColorBrush(Color.Parse("#C0392B")),
            };
            root.Children.Add(err);

            var detail = new TextBlock
            {
                Text = t.ErrorDetail, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 0, 0), Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
                IsVisible = false,
            };
            var detailScroll = new ScrollViewer
            {
                Content = detail, MaxHeight = 170, Margin = new Thickness(0, 4, 0, 0),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                IsVisible = false,
            };
            var detailBtn = new Button
            {
                Classes = { "outlined" }, Content = S.Get("dl.detail"), MinHeight = 26, MinWidth = 84,
                Padding = new Thickness(10, 2), HorizontalAlignment = HAlign.Left, Margin = new Thickness(2, 6, 0, 0),
            };
            detailBtn.Click += (_, _) =>
            {
                bool show = !detail.IsVisible;
                detail.IsVisible = show;
                detailScroll.IsVisible = show;
                detailBtn.Content = show ? S.Get("dl.detail.hide") : S.Get("dl.detail");
            };

            var delRow = new StackPanel { Orientation = LOrientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 6, 0, 0) };
            delRow.Children.Add(detailBtn);
            var delBtn = new Button
            {
                Classes = { "outlined" }, Content = S.Get("dl.task.remove"), MinHeight = 26, MinWidth = 84,
                Padding = new Thickness(10, 2), HorizontalAlignment = HAlign.Left,
            };
            delBtn.Click += (_, _) => { McInstaller.RemoveTask(t.TaskId); Rebuild(); };
            delRow.Children.Add(delBtn);
            root.Children.Add(delRow);
            root.Children.Add(detailScroll);
        }

        var stages = t.Stages.Where(s => s.Total > 0 || s.Done > 0).ToList();
        foreach (var s in stages)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(2, 0) };
            var mark = new TextBlock
            {
                FontSize = 12, VerticalAlignment = VAlign.Center, MinWidth = 22,
                Foreground = s.Done >= s.Total && s.Total > 0 ? Brushes.SeaGreen : Res("OnSurfaceVarBrush", Colors.Gray),
                Text = s.Total > 0 && s.Done >= s.Total ? "✓" : !t.Finished && s.Key == t.CurrentStageKey ? "●" : "…",
            };
            Grid.SetColumn(mark, 0);
            row.Children.Add(mark);
            var name = new TextBlock
            {
                Text = S.Get(s.Key), FontSize = 12.5, VerticalAlignment = VAlign.Center,
                Foreground = Res("OnSurfaceBrush", Colors.Black),
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var pct = new TextBlock
            {
                FontSize = 11.5, VerticalAlignment = VAlign.Center,
                Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
                Text = s.Total > 0 ? $"{s.Percent:F0}%" : "",
            };
            Grid.SetColumn(pct, 2);
            row.Children.Add(pct);
            var done = new TextBlock
            {
                FontSize = 11, VerticalAlignment = VAlign.Center, Margin = new Thickness(8, 0, 0, 0),
                Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
                Text = s.Total > 0 ? $"{s.Done / 1024.0 / 1024:F1}/{s.Total / 1024.0 / 1024:F1} MB" : "",
            };
            Grid.SetColumn(done, 3);
            row.Children.Add(done);
            root.Children.Add(row);
        }

        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = t.Percent, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 0) };
        bar.Foreground = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#4F7CFF"), 0), new GradientStop(Color.Parse("#8A5CF6"), 1) },
        };
        root.Children.Add(bar);
        var foot = new TextBlock
        {
            FontSize = 11.5, Margin = new Thickness(2, 2, 0, 0),
            Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
            Text = $"{t.Percent:F1}% · {t.SpeedMb:F1} MB/s" +
                   (t.CurrentFile.Length > 0 && !t.Finished ? " · " + t.CurrentFile : ""),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        root.Children.Add(foot);

        card.Child = root;
        return card;
    }

    private static IBrush Res(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is IBrush b) return b;
        return new SolidColorBrush(fallback);
    }
}
