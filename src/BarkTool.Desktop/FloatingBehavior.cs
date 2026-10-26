using System.Drawing;

namespace BarkTool.Desktop;

internal sealed record FloatingOptions(double Size = 40, double Opacity = 1, bool Snap = true, bool Fade = true, bool Peek = true)
{
    internal FloatingOptions Normalize() => this with
    {
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 32, 56) : 40,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .4, 1) : 1
    };
}

// Geometry is entirely in physical desktop pixels. Scale only the local DIP snap
// distance, never a virtual desktop origin (monitors can have negative origins).
internal static class FloatingBehavior
{
    internal enum DockSide { None, Left, Right }

    // Only the local DIP strip is scaled; work-area origins are already physical pixels.
    internal static DockSide DetectDock(Rectangle window, Rectangle work, double dpiScale)
    {
        var tolerance = 2 * (double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1);
        if (Math.Abs(window.Left - work.Left) <= tolerance) return DockSide.Left;
        if (Math.Abs((long)window.Right - work.Right) <= tolerance) return DockSide.Right;
        return DockSide.None;
    }

    internal static Point PeekPosition(Rectangle window, Rectangle work, DockSide side, double stripDip, double dpiScale)
    {
        var strip = Math.Max(4, stripDip * (double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1));
        return side switch
        {
            DockSide.Left => new Point((int)Math.Round(work.Left - window.Width + strip), window.Top),
            DockSide.Right => new Point((int)Math.Round(work.Right - strip), window.Top),
            _ => window.Location
        };
    }
    internal static Point Place(Rectangle window, Rectangle work, bool snap, double dpiScale)
    {
        var maxX = Math.Max(work.Left, work.Right - window.Width);
        var maxY = Math.Max(work.Top, work.Bottom - window.Height);
        var x = Math.Clamp(window.Left, work.Left, maxX);
        var y = Math.Clamp(window.Top, work.Top, maxY);
        var threshold = 16 * (double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1);
        if (snap)
        {
            if (Math.Min(x - work.Left, maxX - x) <= threshold)
                x = x - work.Left <= maxX - x ? work.Left : maxX;
            if (Math.Min(y - work.Top, maxY - y) <= threshold)
                y = y - work.Top <= maxY - y ? work.Top : maxY;
        }
        return new Point(x, y);
    }

    internal static double DisplayOpacity(FloatingOptions options, bool idle, bool interacting) =>
        options.Fade && idle && !interacting ? Math.Min(.3, options.Opacity) : options.Opacity;

    internal static bool IsSingleImage(string[]? paths) => paths is { Length: 1 }
        && !string.IsNullOrWhiteSpace(paths[0])
        && new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }
            .Contains(Path.GetExtension(paths[0]), StringComparer.OrdinalIgnoreCase);
}
