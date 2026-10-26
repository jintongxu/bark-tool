using System.Drawing;
using BarkTool.Desktop;

internal static class LifecycleTests
{
    public static void Run()
    {
        ExclusionAndActivation();
        ConcurrentConstruction();
        DisposeRaces();
        Geometry();
        // Smoke-test native probing without assuming anything about the user's desktop.
        _ = FullScreenDetector.IsFullScreenOnMonitor(IntPtr.Zero);
        var window = new System.Windows.Window();
        var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        try { _ = FullScreenDetector.IsFullScreenOnMonitor(handle); }
        finally { window.Close(); }
        Console.WriteLine("PASS single-instance lifecycle and fullscreen geometry");
    }

    private static void ExclusionAndActivation()
    {
        var name = "LifecycleTests." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(name);
        using var secondary = new SingleInstance(name);
        Check(primary.IsPrimary && !secondary.IsPrimary, "same-thread exclusion is not recursive");
        using var activated = new ManualResetEventSlim();
        secondary.RequestActivation(); // Also verify notification before listener registration.
        primary.StartListening(activated.Set);
        Check(activated.Wait(TimeSpan.FromSeconds(5)), "activation received");
        activated.Reset();
        Task.Run(secondary.RequestActivation).GetAwaiter().GetResult();
        Check(activated.Wait(TimeSpan.FromSeconds(5)), "background activation received");
        primary.Dispose();
        primary.Dispose();
        secondary.RequestActivation(); // A vanished primary is harmless.
        using var replacement = new SingleInstance(name);
        Check(replacement.IsPrimary, "secondary retains no exclusion handle");
        secondary.Dispose();
        try
        {
            secondary.RequestActivation();
            throw new InvalidOperationException("disposed instance accepted activation");
        }
        catch (ObjectDisposedException) { }
    }

    private static void ConcurrentConstruction()
    {
        var name = "LifecycleTests." + Guid.NewGuid().ToString("N");
        var instances = new SingleInstance?[8];
        try
        {
            Parallel.For(0, instances.Length, i => instances[i] = new SingleInstance(name));
            Check(instances.Count(instance => instance!.IsPrimary) == 1, "one concurrent primary");
        }
        finally
        {
            foreach (var instance in instances)
                instance?.Dispose();
        }
        using var replacement = new SingleInstance(name);
        Check(replacement.IsPrimary, "concurrent instances release exclusion");
    }

    private static void DisposeRaces()
    {
        var name = "LifecycleTests." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(name);
        using var secondary = new SingleInstance(name);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var count = 0;
        primary.StartListening(() =>
        {
            Interlocked.Increment(ref count);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        secondary.RequestActivation();
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "callback entered before disposal race");
        var disposal = Task.Run(primary.Dispose);
        try
        {
            Parallel.For(0, 32, _ => secondary.RequestActivation());
        }
        finally { release.Set(); }
        Check(disposal.Wait(TimeSpan.FromSeconds(5)), "dispose does not deadlock callback");
        var afterDispose = Volatile.Read(ref count);
        secondary.RequestActivation();
        Thread.Sleep(100); // Give previously queued registered-wait callbacks a chance to run.
        Check(Volatile.Read(ref count) == afterDispose, "no callback after dispose returns");
        using var replacement = new SingleInstance(name);
        Check(replacement.IsPrimary, "racing disposal releases exclusion");
    }

    private static void Geometry()
    {
        var monitor = new Rectangle(-1920, -1080, 1920, 1080);
        Check(FullScreenDetector.CoversMonitor(monitor, monitor), "negative-coordinate fullscreen");
        Check(FullScreenDetector.CoversMonitor(new Rectangle(-1922, -1082, 1924, 1084), monitor), "two-pixel tolerance");
        Check(!FullScreenDetector.CoversMonitor(new Rectangle(-1923, -1080, 1923, 1080), monitor), "three pixels rejected");
        Check(!FullScreenDetector.CoversMonitor(new Rectangle(-1920, -1080, 1920, 1040), monitor), "maximized work area is not fullscreen");
        Check(!FullScreenDetector.CoversMonitor(new Rectangle(0, 0, 1920, 1080), monitor), "different monitor rejected");
        Check(FullScreenDetector.CoversWithFrame(new Rectangle(-1928, -1088, 1936, 1096), monitor), "game resize frame accepted");
        Check(!FullScreenDetector.CoversWithFrame(new Rectangle(-1920, -1080, 1920, 1040), monitor), "windowed game not fullscreen");
        Check(!FullScreenDetector.CoversWithFrame(new Rectangle(-1920, -1080, 3840, 1080), monitor), "spanning window excluded from game fallback");
        Check(!FullScreenDetector.CoversMonitor(Rectangle.Empty, monitor), "empty window rejected");
        Check(!FullScreenDetector.CoversMonitor(Rectangle.Empty, Rectangle.Empty), "empty monitor rejected");
        Check(!FullScreenDetector.CoversMonitor(new Rectangle(-1920, -1080, 3840, 1080), monitor), "spanning window rejected");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
