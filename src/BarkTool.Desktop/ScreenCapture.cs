using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Forms = System.Windows.Forms;

namespace BarkTool.Desktop;

/// <summary>Explicitly invoked local capture only. No clipboard, upload, or persistence.</summary>
internal static class ScreenCapture
{
    private static int active;

    internal static Task<byte[]?> CaptureAsync()
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return Task.FromException<byte[]?>(new InvalidOperationException("截图已在进行中。"));
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint oldDpi = 0;
            try
            {
                // A dedicated thread prevents changing the WPF UI thread's DPI context.
                oldDpi = SetThreadDpiAwarenessContext(new nint(-4));
                if (oldDpi == 0) throw new InvalidOperationException("无法启用截图所需的 DPI 模式。");
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException, true);
                var bounds = Forms.SystemInformation.VirtualScreen;
                if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 80_000_000)
                    throw new InvalidOperationException("桌面尺寸超过截图安全限制。");
                using var frozen = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (var graphics = Graphics.FromImage(frozen))
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                using var overlay = new CaptureOverlay(frozen, bounds);
                overlay.ShowDialog();
                completion.TrySetResult(overlay.Result);
            }
            catch (Exception ex)
            {
                completion.TrySetException(new InvalidOperationException("截图失败，请确认桌面可访问后重试。", ex));
            }
            finally
            {
                if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi);
                Interlocked.Exchange(ref active, 0);
            }
        }) { IsBackground = true, Name = "BarkTool screen selection" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref active, 0);
            completion.TrySetException(new InvalidOperationException("无法启动截图，请稍后重试。", ex));
        }
        return completion.Task;
    }

    private sealed class CaptureOverlay : Forms.Form
    {
        private readonly Bitmap frozen;
        private readonly Rectangle desktop;
        private Point? start;
        private Rectangle selection;
        private bool closing;
        private readonly nint previousForeground = GetForegroundWindow();
        internal byte[]? Result { get; private set; }

        internal CaptureOverlay(Bitmap frozen, Rectangle desktop)
        {
            this.frozen = frozen;
            this.desktop = desktop;
            AutoScaleMode = Forms.AutoScaleMode.None;
            FormBorderStyle = Forms.FormBorderStyle.None;
            StartPosition = Forms.FormStartPosition.Manual;
            Bounds = desktop;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = Forms.Cursors.Cross;
            DoubleBuffered = true;
            Text = "框选截图 · Esc 或右键取消";
        }

        protected override Forms.CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80; return p; } // Tool window: no Alt-Tab entry.
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Native physical bounds, independent of which monitor owns this window's DPI.
            if (!SetWindowPos(Handle, new nint(-1), desktop.X, desktop.Y, desktop.Width, desktop.Height, 0x40))
                throw new InvalidOperationException("无法显示截图选择区域。");
            Activate();
        }

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(frozen, 0, 0);
            if (selection.Width > 0 && selection.Height > 0)
            {
                using var pen = new Pen(Color.DeepSkyBlue, 2);
                e.Graphics.DrawRectangle(pen, selection.X, selection.Y, selection.Width - 1, selection.Height - 1);
            }
            using var background = new SolidBrush(Color.FromArgb(220, 20, 25, 35));
            var hint = new Rectangle(16, 16, 330, 34);
            e.Graphics.FillRectangle(background, hint);
            Forms.TextRenderer.DrawText(e.Graphics, "拖动框选 · 松开完成 · Esc / 右键取消", Font, hint, Color.White,
                Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter);
        }

        private Point Position()
        {
            if (!GetCursorPos(out var point)) throw new InvalidOperationException("无法读取截图位置。");
            return new Point(Math.Clamp(point.X - desktop.X, 0, desktop.Width), Math.Clamp(point.Y - desktop.Y, 0, desktop.Height));
        }
        protected override void OnMouseDown(Forms.MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == Forms.MouseButtons.Right) { Close(); return; }
            if (e.Button == Forms.MouseButtons.Left) { start = Position(); Capture = true; }
        }
        protected override void OnMouseMove(Forms.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateSelection();
        }
        private void UpdateSelection()
        {
            if (start is not Point first) return;
            var last = Position();
            selection = Rectangle.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
            Invalidate();
        }
        protected override void OnMouseUp(Forms.MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != Forms.MouseButtons.Left || start is null) return;
            UpdateSelection();
            start = null;
            Capture = false;
            if (selection.Width < 2 || selection.Height < 2) { Invalidate(); return; }
            using var cropped = frozen.Clone(selection, PixelFormat.Format32bppRgb);
            using var output = new MemoryStream();
            cropped.Save(output, ImageFormat.Png);
            Result = output.ToArray();
            Close();
        }
        protected override void OnKeyDown(Forms.KeyEventArgs e)
        {
            if (e.KeyCode == Forms.Keys.Escape) { e.Handled = true; Close(); }
            base.OnKeyDown(e);
        }
        protected override void OnFormClosing(Forms.FormClosingEventArgs e)
        {
            closing = true;
            bool restore = GetForegroundWindow() == Handle;
            base.OnFormClosing(e);
            if (restore && !e.Cancel && previousForeground != 0) SetForegroundWindow(previousForeground);
        }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (!closing && !Disposing && !IsDisposed) Close(); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
