using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using VAlign = Avalonia.Layout.VerticalAlignment;

namespace StarryLauncher;

internal sealed class ModpackChoiceWindow : Window
{
    public enum Choice { None, Install, Export }

    private readonly TaskCompletionSource<Choice> _tcs = new();
    private bool _done;

    public ModpackChoiceWindow(string packTitle)
    {
        Title = S.Get("dl.packchoice.title");
        Width = 470;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("SurfaceBrush", Colors.White);
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 22), Spacing = 10 };

        root.Children.Add(new TextBlock
        {
            Text = S.Get("dl.packchoice.title"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res("OnSurfaceBrush", Colors.Black),
        });
        root.Children.Add(new TextBlock
        {
            Text = S.Get("dl.packchoice.hint"),
            FontSize = 12.5,
            Foreground = Res("OnSurfaceVarBrush", Colors.Gray),
            Margin = new Thickness(0, -4, 0, 6),
        });

        root.Children.Add(new TextBlock
        {
            Text = packTitle,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res("OnSurfaceBrush", Colors.Black),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        root.Children.Add(BuildOption("dl.packchoice.install", "xz", Choice.Install));
        root.Children.Add(BuildOption("dl.packchoice.export", "kj", Choice.Export));

        var cancel = new Button
        {
            Content = S.Get("dl.packchoice.cancel"),
            MinHeight = 34,
            MinWidth = 96,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        cancel.Click += (_, _) => { Finish(Choice.None); Close(); };
        root.Children.Add(cancel);

        Content = root;
        Closed += (_, _) => Finish(Choice.None);
    }

    private Button BuildOption(string titleKey, string iconKey, Choice choice)
    {
        var icon = new Image
        {
            Source = SvgIcon.Get(iconKey),
            Width = 26,
            Height = 26,
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var title = new TextBlock
        {
            Text = S.Get(titleKey), FontSize = 14, FontWeight = FontWeight.SemiBold,
            Foreground = Res("OnSurfaceBrush", Colors.Black),
            VerticalAlignment = VAlign.Center,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(icon);
        grid.Children.Add(title);
        Grid.SetColumn(title, 1);

        var btn = new Button
        {
            Classes = { "tonal" },
            MinHeight = 58,
            HorizontalAlignment = HAlign.Stretch,
            HorizontalContentAlignment = HAlign.Left,
            Padding = new Thickness(16, 10),
            Content = grid,
        };
        btn.Click += (_, _) => { Finish(choice); Close(); };
        return btn;
    }

    private void Finish(Choice c)
    {
        if (_done) return;
        _done = true;
        _tcs.TrySetResult(c);
    }

    public Task<Choice> Pick() => _tcs.Task;

    private static IBrush Res(string key, Color fallback)
        => Application.Current?.TryGetResource(key, null, out var v) == true && v is IBrush b
            ? b : new SolidColorBrush(fallback);
}
