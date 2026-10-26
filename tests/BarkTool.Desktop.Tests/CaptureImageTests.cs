using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BarkTool.Desktop;

internal static class CaptureImageTests
{
#if CAPTURE_IMAGE_TESTS
    // Optional standalone entry point; never invokes capture.
    [STAThread]
    private static void Main() => Run();
#endif

    internal static void Run()
    {
        var opaque = MakeImage(640, 320, false);
        var original = ImageOptimizer.Process(opaque, "original.png", new());
        Check(ReferenceEquals(original.Bytes, opaque) && !original.Changed && original.OriginalBytes == opaque.Length, "disabled preserves original");
        var reduced = ImageOptimizer.Process(opaque, "original.png", new(true, 160, 85));
        Check(reduced.Width == 160 && reduced.Height == 80 && reduced.Changed, "dimensions and aspect ratio");
        Check(reduced.FileName.EndsWith(".jpg") && reduced.Bytes.Length < opaque.Length, "opaque JPEG compression");
        var small = ImageOptimizer.Process(MakeImage(16, 8, false), "small.png", new(true, 1920));
        Check(small.Width == 16 && small.Height == 8, "never upscale");
        var transparent = ImageOptimizer.Process(MakeImage(200, 100, true), "alpha.png", new(true, 100));
        Check(transparent.Width == 100 && transparent.Height == 50 && transparent.FileName.EndsWith(".png"), "alpha stays PNG");
        using (var stream = new MemoryStream(transparent.Bytes))
        {
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            Check(pixels.Where((_, i) => i % 4 == 3).Any(a => a < 255), "transparency retained");
        }
        Reject(() => ImageOptimizer.Process([1, 2, 3], "bad.png", new()));
        Reject(() => ImageOptimizer.Process([], "empty.png", new()));
        Reject(() => ImageOptimizer.Process(opaque, "bad.png", new(true, 0)));
        Reject(() => ImageOptimizer.Process(opaque, "bad.png", new(true, 100, 101)));
        var gifEncoder = new GifBitmapEncoder();
        using var source = new MemoryStream(opaque);
        var frame = BitmapDecoder.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        gifEncoder.Frames.Add(BitmapFrame.Create(frame));
        gifEncoder.Frames.Add(BitmapFrame.Create(frame));
        using var gifStream = new MemoryStream();
        gifEncoder.Save(gifStream);
        var gif = gifStream.ToArray();
        var kept = ImageOptimizer.Process(gif, "animation.gif", new(true, 16));
        Check(ReferenceEquals(gif, kept.Bytes) && !kept.Changed, "GIF animation preserved");
        var oversized = new byte[ImageOptimizer.UploadLimit + 1];
        gif.CopyTo(oversized, 0);
        Reject(() => ImageOptimizer.Process(oversized, "large.gif", new(true)));
        var largePng = new byte[ImageOptimizer.UploadLimit + 1];
        opaque.CopyTo(largePng, 0);
        Reject(() => ImageOptimizer.Process(largePng, "large.png", new()));
        Check(ImageOptimizer.Process(largePng, "large.png", new(true, 100)).Bytes.Length < ImageOptimizer.UploadLimit,
            "upload limit applied after optimization");
        Console.WriteLine("PASS image optimization, alpha, aspect, no upscale, GIF retention and upload validation (no screen capture)");
    }

    private static byte[] MakeImage(int width, int height, bool alpha)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(42);
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = alpha ? (byte)100 : (byte)255;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected invalid image rejection");
    }
}
