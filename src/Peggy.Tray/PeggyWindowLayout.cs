using System;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Peggy.Tray;

internal static class PeggyWindowLayout
{
    public static void ConstrainToWorkArea(Window window, double margin = 16)
    {
        ArgumentNullException.ThrowIfNull(window);
        ConstrainToWorkArea(window, GetWorkArea(window), margin);
    }

    internal static void ConstrainToWorkArea(
        Window window,
        Rect workArea,
        double margin)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (workArea.Width <= 0 ||
            workArea.Height <= 0 ||
            margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        var availableWidth = Math.Max(1, workArea.Width - margin);
        var availableHeight = Math.Max(1, workArea.Height - margin);
        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);
        window.MaxWidth = availableWidth;
        window.MaxHeight = availableHeight;
        if (window.Width > availableWidth)
        {
            window.Width = availableWidth;
        }
        if (window.Height > availableHeight)
        {
            window.Height = availableHeight;
        }

        window.UpdateLayout();
        var width = window.ActualWidth > 0
            ? window.ActualWidth
            : window.Width;
        var height = window.ActualHeight > 0
            ? window.ActualHeight
            : window.Height;
        window.Left = Math.Clamp(
            window.Left,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - width));
        window.Top = Math.Clamp(
            window.Top,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - height));
    }

    private static Rect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var physical = Forms.Screen.FromHandle(handle).WorkingArea;
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
        {
            return SystemParameters.WorkArea;
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(
            new Point(physical.Left, physical.Top));
        var bottomRight = transform.Transform(
            new Point(physical.Right, physical.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
