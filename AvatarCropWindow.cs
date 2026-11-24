using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace StarryLauncher;

internal sealed class AvatarCropWindow : Window
{
    private const double Area = 400;
    private const double Circle = 280;
    private const double Out = 256;

    private readonly Bitmap _src;
    private readonly Image _img;
    private readonly TranslateTransform _tt = new();
    private readonly ScaleTransform _st = new();
    private readonly double _baseW;
    private readonly double _baseH;
    private readonly string _destPath;
    private bool _drag;
    private Point _last;

    public string? ResultPath { get; private set; }

    public AvatarCropWindow(string srcPath, string destPath)
    {
        _src = new Bitmap(srcPath);
        _destPath = destPath;

        double fit = Math.Max(Circle / _src.PixelSize.Width, Circle / _src.PixelSize.Height);
        _baseW = _src.PixelSize.Width * fit;
        _baseH = _src.PixelSize.Height * fit;

        Title = S.Get("avatar.cropTitle");
        Width = 500;
        Height = 620;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = MainWindow.CurrentUiFont;
        Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        DialogChrome.ApplyChromeless(this);
        Opened += (_, _) => DialogChrome.HideCaptionButtons(this);

        _img = new Image
        {
            Source = _src,
            Width = _baseW,
            Height = _baseH,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new TransformGroup { Children = { _tt, _st } },
        };

        var overlay = new Avalonia.Controls.Shapes.Path { IsHitTestVisible = false };
        double c = Area / 2, r = Circle / 2;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.SetFillRule(FillRule.EvenOdd);
            ctx.BeginFigure(new Point(0, 0), isFilled: true);
            ctx.LineTo(new Point(Area, 0));
            ctx.LineTo(new Point(Area, Area));
            ctx.LineTo(new Point(0, Area));
            ctx.EndFigure(true);

            ctx.BeginFigure(new Point(c, c - r), isFilled: true);
            var s = new Size(r, r);
            ctx.ArcTo(new Point(c + r, c), s, 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(c, c + r), s, 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(c - r, c), s, 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(c, c - r), s, 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        overlay.Data = geo;
        overlay.Fill = new SolidColorBrush(Color.FromArgb(0x9C, 0x58, 0x58, 0x58));

        var ring = new Ellipse
        {
            Width = Circle,
            Height = Circle,
            Stroke = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };

        var stage = new Panel
        {
            Width = Area,
            Height = Area,
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Children =
            {
                new Canvas
                {
                    Children = { _img },
                },
                overlay,
                ring,
            },
        };
        Canvas.SetLeft(_img, (Area - _baseW) / 2);
        Canvas.SetTop(_img, (Area - _baseH) / 2);

        stage.PointerPressed += OnStagePressed;
        stage.PointerMoved += OnStageMoved;
        stage.PointerReleased += OnStageReleased;
        stage.PointerWheelChanged += OnStageWheel;

        var btnOk = new Button { Classes = { "primary" }, Content = S.Get("btn.ok"), MinWidth = 110, MinHeight = 38 };
        btnOk.Click += (_, _) => Confirm();
        var btnCancel = new Button { Classes = { "outlined" }, Content = S.Get("btn.cancel"), MinWidth = 110, MinHeight = 38 };
        btnCancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(28, 24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = S.Get("avatar.cropTitle"), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.Black },
                new TextBlock
                {
                    Text = S.Get("avatar.cropHint"),
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                },
                stage,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 12,
                    Children = { btnCancel, btnOk },
                },
            },
        };
    }

    private void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _drag = true;
            _last = e.GetPosition(this);
            e.Pointer.Capture(_img);
        }
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (!_drag) return;
        var p = e.GetPosition(this);
        _tt.X += p.X - _last.X;
        _tt.Y += p.Y - _last.Y;
        _last = p;
        ClampOffset();
    }

    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        _drag = false;
        e.Pointer.Capture(null);
    }

    private void OnStageWheel(object? sender, PointerWheelEventArgs e)
    {
        double k = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        double ns = Math.Clamp(_st.ScaleX * k, 1, 8);
        k = ns / _st.ScaleX;
        if (Math.Abs(k - 1) < 0.0001) return;
        _st.ScaleX = ns;
        _st.ScaleY = ns;

        _tt.X *= k;
        _tt.Y *= k;
        ClampOffset();
    }

    private void ClampOffset()
    {
        double s = _st.ScaleX;
        double mx = Math.Max(0, (s * _baseW - Circle) / 2);
        double my = Math.Max(0, (s * _baseH - Circle) / 2);
        _tt.X = Math.Clamp(_tt.X, -mx, mx);
        _tt.Y = Math.Clamp(_tt.Y, -my, my);
    }

    private void Confirm()
    {
        if (string.IsNullOrEmpty(_destPath)) { Close(); return; }
        try
        {
            double s = _st.ScaleX;
            double k = _src.PixelSize.Width / _baseW;
            double r = Circle / 2;

            var srcRect = new Rect(
                (_baseW / 2 - _tt.X / s - r / s) * k,
                (_baseH / 2 - _tt.Y / s - r / s) * k,
                2 * r * k / s,
                2 * r * k / s);

            using var rtb = new RenderTargetBitmap(new PixelSize((int)Out, (int)Out), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext())
                dc.DrawImage(_src, srcRect, new Rect(0, 0, Out, Out));
            rtb.Save(_destPath);
            ResultPath = _destPath;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"头像裁剪保存失败: {ex.Message}");
        }
        Close();
    }
}
