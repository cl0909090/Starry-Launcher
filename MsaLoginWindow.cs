using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using LOrientation = Avalonia.Layout.Orientation;

namespace StarryLauncher;

internal sealed class MsaLoginWindow : Window
{
    private readonly TextBlock _status;
    private readonly string _authorizeUrl;
    private readonly CancellationTokenSource? _cts;

    public MsaLoginWindow(string authorizeUrl, CancellationTokenSource? cts)
    {
        _authorizeUrl = authorizeUrl;
        _cts = cts;

        Title = S.Get("launcher.ms.winTitle");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = MainWindow.CurrentUiFont;
        Background = Res("SurfaceBrush", Colors.White);
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        var root = new StackPanel { Margin = new Thickness(30, 26, 30, 20) };

        root.Children.Add(new TextBlock
        {
            Text = S.Get("launcher.ms.winTitle"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res("OnSurfaceBrush", Colors.Black),
        });

        root.Children.Add(new TextBlock
        {
            Text = S.Get("launcher.ms.oauthHint"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = Res("OnSurfaceBrush", Colors.Black),
        });

        _status = new TextBlock
        {
            Text = S.Get("launcher.ms.waiting"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 14, 0, 18),
            Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
        };
        root.Children.Add(_status);

        var row = new StackPanel { Orientation = LOrientation.Horizontal, Spacing = 10, HorizontalAlignment = HAlign.Center };
        var openBtn = new Button { Classes = { "tonal" }, MinHeight = 36, Content = S.Get("launcher.ms.openpage") };
        openBtn.Click += (_, _) => OpenBrowser();
        var cancelBtn = new Button { Classes = { "tonal" }, MinHeight = 36, Content = S.Get("launcher.ms.cancel") };
        cancelBtn.Click += (_, _) => { _cts?.Cancel(); Close(); };
        row.Children.Add(openBtn);
        row.Children.Add(cancelBtn);
        root.Children.Add(row);

        Content = root;
    }

    public void SetStatus(string text, bool success)
    {
        _status.Text = text;
        _status.Foreground = Res(success ? "SuccessBrush" : "DangerBrush",
            success ? Color.FromRgb(0x22, 0xB4, 0x70) : Color.FromRgb(0xE8, 0x52, 0x52));
    }

    private void OpenBrowser()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _authorizeUrl,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private static IBrush Res(string key, Color fallback)
    {
        var v = default(object);
        if (Application.Current?.TryGetResource(key, null, out v) == true && v is IBrush b) return b;
        return new SolidColorBrush(fallback);
    }
}
