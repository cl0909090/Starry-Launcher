using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace StarryLauncher;

internal static class Jelly
{

    public static Task PopOpenAsync(Control target, double originX = 1, double originY = 0,
        Control? staggerHost = null, CancellationToken ct = default)
        => PopOpenAsync(target, new RelativePoint(originX, originY, RelativeUnit.Relative), staggerHost, ct);

    public static async Task PopOpenAsync(Control target, RelativePoint origin,
        Control? staggerHost = null, CancellationToken ct = default)
    {
        try
        {
            target.RenderTransformOrigin = origin;

            target.Transitions = null;
            target.RenderTransform = TransformOperations.Parse("scale(0.25)");
            target.Opacity = 0;
            if (staggerHost != null) staggerHost.Opacity = 0;
            target.IsVisible = true;

            target.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(360),
                    Easing = new BackEaseOver(),
                },
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(120),
                    Easing = new CubicOut(),
                },
            };
            target.RenderTransform = TransformOperations.Parse("scale(1)");
            target.Opacity = 1;

            await Task.Delay(380, ct);

            target.Transitions = null;
            target.RenderTransform = null;
            if (staggerHost != null)
            {

                staggerHost.Opacity = 1;
                StaggerIn(staggerHost, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"果冻弹出动画异常: {ex.Message}");
            SnapOpen(target, staggerHost);
        }
    }

    public static Task CollapseAsync(Control target, double originX = 1, double originY = 0,
        CancellationToken ct = default)
        => CollapseAsync(target, new RelativePoint(originX, originY, RelativeUnit.Relative), ct);

    public static async Task CollapseAsync(Control target, RelativePoint origin,
        CancellationToken ct = default)
    {
        try
        {
            target.RenderTransformOrigin = origin;

            target.Transitions = null;
            target.RenderTransform = TransformOperations.Parse("scale(1)");
            target.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(120),
                    Easing = new CubicOut(),
                },
            };
            target.RenderTransform = TransformOperations.Parse("scale(1.06,0.9)");
            await Task.Delay(120, ct);

            target.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(180),
                    Easing = new CubicIn(),
                },
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(150),
                },
            };
            target.RenderTransform = TransformOperations.Parse("scale(0.25,0.05)");
            target.Opacity = 0;
            await Task.Delay(190, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"果冻收起动画异常: {ex.Message}");
        }
        finally
        {
            target.Transitions = null;
            target.RenderTransform = null;
            target.Opacity = 1;
        }
    }

    public static void SnapOpen(Control target, Control? staggerHost = null)
    {
        target.Transitions = null;
        target.RenderTransform = null;
        target.Opacity = 1;
        target.IsVisible = true;
        if (staggerHost != null) staggerHost.Opacity = 1;
    }

    public static void StaggerIn(Control host, CancellationToken ct = default)
    {
        int i = 0;
        foreach (var child in host.GetVisualChildren())
        {
            if (child is not Control c) continue;
            if (i >= 8) break;
            _ = FadeInDelayedAsync(c, i++ * 45, ct);
        }
    }

    private static async Task FadeInDelayedAsync(Control c, int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);

            c.Transitions = null;
            c.Opacity = 0;
            c.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(140),
                    Easing = new CubicOut(),
                },
            };
            c.Opacity = 1;
            await Task.Delay(150, ct);
            c.Transitions = null;
        }
        catch (OperationCanceledException) { }
        catch {   }
    }
}

public class BackEaseOver : Avalonia.Animation.Easings.Easing
{
    public override double Ease(double progress)
    {
        const double s = 1.9;
        double p = progress - 1;
        return p * p * ((s + 1) * p + s) + 1;
    }
}

public class CubicIn : Avalonia.Animation.Easings.Easing
{
    public override double Ease(double progress) => progress * progress * progress;
}

public class CubicOut : Avalonia.Animation.Easings.Easing
{
    public override double Ease(double progress) => 1 - Math.Pow(1 - progress, 3);
}
