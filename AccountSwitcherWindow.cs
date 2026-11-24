using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using LOrientation = Avalonia.Layout.Orientation;
using VAlign = Avalonia.Layout.VerticalAlignment;

namespace StarryLauncher;

internal sealed class AccountSwitcherWindow : Window
{
    private readonly StackPanel _listPanel = new() { Spacing = 8 };
    private bool _addNew;

    public AccountSwitcherWindow()
    {
        Title = S.Get("launcher.account.switch");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("SurfaceBrush", Colors.White);
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        LauncherService.RememberCurrentAccount();
        AppSettings.Shared.Save();

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };

        root.Children.Add(new TextBlock
        {
            Text = S.Get("launcher.account.switch"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res("OnSurfaceBrush", Colors.Black),
        });
        root.Children.Add(new TextBlock
        {
            Text = S.Get("launcher.account.hint"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 14),
            Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
        });

        var listScroll = new ScrollViewer
        {
            MaxHeight = 244,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        listScroll.Content = _listPanel;
        root.Children.Add(listScroll);
        RebuildList();

        var btnRow = new StackPanel { Orientation = LOrientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HAlign.Center };
        var addBtn = new Button
        {
            Classes = { "primary" },
            MinHeight = 42,
            MinWidth = 130,
            Content = S.Get("launcher.account.add"),
        };
        addBtn.Click += (_, _) => { _addNew = true; Close(); };
        var closeBtn = new Button
        {
            Classes = { "tonal" },
            MinHeight = 42,
            MinWidth = 130,
            Content = S.Get("btn.close"),
        };
        closeBtn.Click += (_, _) => Close();
        btnRow.Children.Add(addBtn);
        btnRow.Children.Add(closeBtn);
        root.Children.Add(btnRow);

        Content = root;
    }

    private void RebuildList()
    {
        _listPanel.Children.Clear();
        var accounts = AppSettings.Shared.McAccounts;
        if (accounts.Count == 0)
        {
            _listPanel.Children.Add(new TextBlock
            {
                Text = S.Get("launcher.account.empty"),
                FontSize = 12.5,
                HorizontalAlignment = HAlign.Center,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
            });
            return;
        }

        foreach (var acc in accounts.ToList())
        {
            bool isCurrent = AppSettings.Shared.McUserName == acc.Name
                && AppSettings.Shared.McLoginType == acc.Type;
            string label = isCurrent
                ? $"{acc.Name}  ·  {S.Get(MethodKey(acc.Type))}（{S.Get("launcher.account.current")}）"
                : $"{acc.Name}  ·  {S.Get(MethodKey(acc.Type))}";

            var head = new Image
            {
                Width = 32,
                Height = 32,
                VerticalAlignment = VAlign.Center,
            };
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(head, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            head.Source = SkinHeadService.SteveFace();
            _ = LoadHeadAsync(head, acc.Type, acc.Uuid);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,*,Auto") };
            row.Children.Add(head);

            var pick = new Button
            {
                Classes = { "tonal" },
                MinHeight = 42,
                HorizontalAlignment = HAlign.Stretch,
                HorizontalContentAlignment = HAlign.Left,
                Padding = new Thickness(14, 0, 10, 0),
                Content = label,
            };
            pick.Click += (_, _) =>
            {
                Picked = acc;
                Close();
            };
            Grid.SetColumn(pick, 2);
            row.Children.Add(pick);

            var del = new Button
            {
                Classes = { "tonal" },
                MinHeight = 42,
                MinWidth = 44,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                Content = new Image { Width = 18, Height = 18, Source = SvgIcon.Get("qk") },
            };
            ToolTip.SetTip(del, S.Get("launcher.account.delete"));
            del.Click += (_, _) =>
            {
                LauncherService.RemoveAccount(acc);
                RebuildList();
            };
            Grid.SetColumn(del, 3);
            row.Children.Add(del);

            _listPanel.Children.Add(row);
        }
    }

    private static async Task LoadHeadAsync(Image img, string type, string uuid)
    {
        try
        {
            var face = await SkinHeadService.LoadAsync(type, uuid);
            if (face != null) img.Source = face;
        }
        catch {   }
    }

    private static string MethodKey(string type) => "launcher.loginType.microsoft";

    public AppSettings.AccountEntry? Picked { get; private set; }

    public bool AddNewRequested => _addNew;

    private static IBrush Res(string key, Color fallback)
    {
        if (Application.Current?.TryGetResource(key, null, out var v) == true && v is IBrush b) return b;
        return new SolidColorBrush(fallback);
    }
}
