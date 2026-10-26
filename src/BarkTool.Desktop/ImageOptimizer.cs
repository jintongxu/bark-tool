using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BarkTool.Desktop;

internal sealed record ImageOptimizationOptions(bool Enabled = false, int MaxDimension = 1920, int JpegQuality = 85);
internal sealed record OptimizedImage(byte[] Bytes, string FileName, int OriginalBytes, int Width, int Height, bool Changed);

internal static class ImageOptimizer
{
    internal const int UploadLimit = 10 * 1024 * 1024;
    // Bound compressed input and decoded pixel buffers independently. Metadata is read on demand.
    private const int InputLimit = 64 * 1024 * 1024;
    private const long PixelLimit = 40_000_000;

    internal static OptimizedImage Process(byte[] input, string fileName, ImageOptimizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (input is null || input.Length == 0 || input.Length > InputLimit)
            throw new ArgumentException("图片为空或输入文件超过 64 MiB 安全限制。");
        if (options.Enabled && (options.MaxDimension < 1 || options.MaxDimension > 16384 || options.JpegQuality < 1 || options.JpegQuality > 100))
            throw new ArgumentException("图片尺寸或 JPEG 质量设置无效。");
        try
        {
            using var stream = new MemoryStream(input, false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > PixelLimit)
                throw new ArgumentException("图片像素尺寸超过安全限制（最多 4000 万像素）。");
            OptimizedImage Original()
            {
                if (input.Length > UploadLimit)
                    throw new ArgumentException(decoder is GifBitmapDecoder
                        ? "GIF 超过 10 MiB；为保留动画，请手动缩小文件后重试。"
                        : "图片超过 10 MiB，请启用图片优化或减小图片后重试。");
                return new(input, fileName, input.Length, width, height, false);
            }
            // Never flatten GIF, TIFF or other multi-frame formats implicitly.
            if (!options.Enabled || decoder is GifBitmapDecoder || decoder.Frames.Count > 1) return Original();
            double scale = Math.Min(1d, (double)options.MaxDimension / Math.Max(width, height));
            int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(height * scale));
            BitmapSource source = frame;
            if (scale < 1)
                source = new TransformedBitmap(frame, new ScaleTransform((double)targetWidth / width, (double)targetHeight / height));
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var row = new byte[checked(bgra.PixelWidth * 4)];
            bool alpha = false;
            for (int y = 0; y < bgra.PixelHeight && !alpha; y++)
            {
                bgra.CopyPixels(new System.Windows.Int32Rect(0, y, bgra.PixelWidth, 1), row, row.Length, 0);
                for (int x = 3; x < row.Length; x += 4) if (row[x] != 255) { alpha = true; break; }
            }
            BitmapEncoder encoder = alpha ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = options.JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length > UploadLimit)
                throw new ArgumentException("优化后的图片仍超过 10 MiB，请减小最大尺寸或 JPEG 质量。");
            // Enforcing the requested dimension is itself a necessary size reduction.
            if (output.Length >= input.Length && scale == 1 && input.Length <= UploadLimit) return Original();
            return new(output.ToArray(), Path.ChangeExtension(fileName, alpha ? ".png" : ".jpg"), input.Length,
                source.PixelWidth, source.PixelHeight, true);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException("无法读取或处理图片，请选择有效且尺寸适中的图片。", ex);
        }
    }
}
