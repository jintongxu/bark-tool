using System.Drawing;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using BarkTool.Desktop;

internal static class FloatingTests
{
    internal static void Run()
    {
        Geometry();
        OptionsAndOpacity();
        Drops();
        Window();
        Console.WriteLine("PASS floating physical geometry, options, fade policy, drop gate and WPF surface");
    }

    private static void Geometry()
    {
        var negative = new Rectangle(-2560, -1440, 2560, 1400);
        Check(FloatingBehavior.Place(new Rectangle(-2540, -1000, 60, 60), negative, true, 1.5)
            == new Point(-2560, -1000), "150% snap uses local pixels, not scaled negative origin");
        Check(FloatingBehavior.Place(new Rectangle(-2535, -1000, 60, 60), negative, true, 1.5)
            == new Point(-2535, -1000), "outside 24-pixel threshold does not snap");
        Check(FloatingBehavior.Place(new Rectangle(-2540, -1000, 60, 60), negative, false, 1.5)
            == new Point(-2540, -1000), "snap disabled");
        Check(FloatingBehavior.Place(new Rectangle(-20, -100, 60, 60), negative, true, 1.5)
            == new Point(-60, -100), "clamp to negative monitor right edge");
        var right = new Rectangle(1920, -200, 3840, 2120);
        Check(FloatingBehavior.Place(new Rectangle(1952, -168, 80, 80), right, true, 2)
            == new Point(1920, -200), "200% monitor boundary inclusive");
        Check(FloatingBehavior.Place(new Rectangle(1953, -167, 80, 80), right, true, 2)
            == new Point(1953, -167), "200% monitor outside threshold");
        Check(FloatingBehavior.Place(new Rectangle(5600, 1850, 80, 80), right, true, 2)
            == new Point(5600, 1840), "bottom clamps without scaling desktop origin");
        Check(FloatingBehavior.Place(new Rectangle(0, 0, 80, 80), new Rectangle(-20, -20, 20, 20), true, 1)
            == new Point(-20, -20), "small work areas do not throw");
        Check(FloatingBehavior.Place(new Rectangle(-5000, 5000, 40, 40), negative, false, 1)
            == new Point(-2560, -80), "offscreen coordinates recover even with snap disabled");
    }

    private static void OptionsAndOpacity()
    {
        Check(new FloatingOptions() == new FloatingOptions(40, 1, true, true, true), "defaults");
        Check(new FloatingOptions(1, .1).Normalize() == new FloatingOptions(32, .4), "minimum options");
        Check(new FloatingOptions(100, 2).Normalize() == new FloatingOptions(56, 1), "maximum options");
        Check(new FloatingOptions(double.NaN, double.PositiveInfinity).Normalize() == new FloatingOptions(), "non-finite options");
        var options = new FloatingOptions(40, .6);
        Check(FloatingBehavior.DisplayOpacity(options, false, false) == .6, "normal configured opacity");
        Check(FloatingBehavior.DisplayOpacity(options, true, false) == .3, "idle opacity");
        Check(FloatingBehavior.DisplayOpacity(options, true, true) == .6, "hover drag menu and radial suppress fade");
        Check(FloatingBehavior.DisplayOpacity(options with { Fade = false }, true, false) == .6, "fade disabled");
        Check(FloatingBehavior.DisplayOpacity(options with { Opacity = .2 }, true, false) == .2, "fade never brightens a lower base opacity");
        var work = new Rectangle(1920, 0, 1920, 1040);
        var docked = new Rectangle(1920 + 1920 - 40, 300, 40, 40);
        Check(FloatingBehavior.DetectDock(docked, work, 2) == FloatingBehavior.DockSide.Right, "right dock detected at 200%");
        Check(FloatingBehavior.DetectDock(new Rectangle(1922, 300, 40, 40), work, 1) == FloatingBehavior.DockSide.Left, "left dock within tolerance");
        Check(FloatingBehavior.DetectDock(new Rectangle(2300, 300, 40, 40), work, 1) == FloatingBehavior.DockSide.None, "floating in middle does not peek");
        Check(FloatingBehavior.PeekPosition(docked, work, FloatingBehavior.DockSide.Right, 6, 2) == new Point(3828, 300), "right peek leaves a scaled strip");
        var leftDocked = new Rectangle(1920, 300, 40, 40);
        Check(FloatingBehavior.PeekPosition(leftDocked, work, FloatingBehavior.DockSide.Left, 6, 2) == new Point(1920 - 40 + 12, 300), "left peek leaves a scaled strip");
        Check(FloatingBehavior.PeekPosition(new Rectangle(2300, 300, 40, 40), work, FloatingBehavior.DockSide.None, 6, 2) == new Point(2300, 300), "undocked position unchanged");
    }

    private static void Drops()
    {
        Check(FloatingBehavior.IsSingleImage([@"C:\preview.JPG"]), "single image case insensitive");
        Check(!FloatingBehavior.IsSingleImage(null), "non-file payload rejected");
        Check(!FloatingBehavior.IsSingleImage([@"C:\preview.JPG", @"C:\second.png"]), "multi-file drop rejected");
        Check(!FloatingBehavior.IsSingleImage([@"C:\notes.txt"]), "non-image extension rejected");
        Check(!FloatingBehavior.IsSingleImage(["a.png", "b.png"]), "multiple images rejected");
        Check(!FloatingBehavior.IsSingleImage(["a.png.exe"]), "invalid extension rejected");
        Check(!FloatingBehavior.IsSingleImage([""]), "empty path rejected");
        // File existence, byte count and decoder validation belong to OpenDroppedImage;
        // this gate must never upload or mutate the text composition window.
    }

    private static void Window()
    {
        var window = new FloatWindow();
        try
        {
            Check(!window.ShowActivated && window.Topmost && !window.ShowInTaskbar, "non-activating floating flags");
            Check(window.AllowDrop, "drop enabled");
            var bubble = (Border)window.FindName("Bubble");
            Check(bubble.ToolTip is null && window.ToolTip is null, "no rectangular tooltips");
            Check(bubble.Child is System.Windows.Shapes.Path, "original arrow retained");
            var labels = bubble.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
            Check(labels.SequenceEqual(new[] { "写内容", "框选截图", "隐藏悬浮按钮", "设置", "退出" }), "context actions");
            window.ApplyOptions(new FloatingOptions(56, .4, false, false));
            Check(window.Width == 56 && window.Height == 56 && window.Opacity == .4, "live options update");
            var timer = (DispatcherTimer)typeof(FloatWindow).GetField("_fadeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(timer.Interval == TimeSpan.FromSeconds(3), "three-second idle deadline");
            Check(!timer.IsEnabled, "hidden window never schedules fade");
            window.ApplyOptions(new FloatingOptions());
            window.Show();
            window.RefreshInteractionState();
            bubble.ContextMenu.IsOpen = true;
            Check(!timer.IsEnabled && window.Opacity == 1, "open menu cancels fade");
            bubble.ContextMenu.IsOpen = false;
            window.Hide();
            Check(!timer.IsEnabled, "hidden state cancels idle timer");
        }
        finally { window.Close(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
