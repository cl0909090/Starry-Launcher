using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace StarryLauncher;

internal static class DialogChrome
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    private const double DragStripHeight = 44;

    public static void ApplyChromeless(Window w)
    {
        w.ExtendClientAreaToDecorationsHint = false;
        w.WindowDecorations = WindowDecorations.BorderOnly;

        w.PointerPressed += (_, e) =>
        {
            if (w.WindowState != WindowState.Normal) return;
            if (!e.GetCurrentPoint(w).Properties.IsLeftButtonPressed) return;
            if (e.GetPosition(w).Y > DragStripHeight) return;
            if (IsInteractive(e.Source as Avalonia.Visual)) return;
            w.BeginMoveDrag(e);
        };
    }

    public static void HideCaptionButtons(Window w)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        try { Win32Properties.SetWindowCornerPreference(w, Win32Properties.WindowCornerPreference.Round); }
        catch {   }

        try
        {
            var m = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }
        catch {   }
    }

    private static bool IsInteractive(Visual? v)
    {
        while (v != null)
        {
            if (v is Button or TextBox or Slider or ToggleButton or ListBox or ComboBox) return true;
            v = v.Parent as Visual;
        }
        return false;
    }
}
