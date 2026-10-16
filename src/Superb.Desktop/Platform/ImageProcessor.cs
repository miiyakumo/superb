using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal sealed class ImageProcessor : IImageProcessor
{
    public Payload Apply(Payload input, ProcessStep step)
    {
        if (!input.HasImage) throw new SafeFailure("image_required_for_step");
        BitmapSource bitmap = Decode(input.Png);
        if (step.Kind == StepKind.Resize)
        {
            if (step.MaxDimension is < 64 or > 8192) throw new SafeFailure("resize_invalid");
            var scale = Math.Min(1, (double)step.MaxDimension / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        }
        else
        {
            var r = step.Rectangle ?? throw new SafeFailure("rectangle_missing");
            if (r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 ||
                (long)r.X + r.Width > bitmap.PixelWidth || (long)r.Y + r.Height > bitmap.PixelHeight)
                throw new SafeFailure("rectangle_out_of_bounds");
            if (step.Kind == StepKind.Crop) bitmap = new CroppedBitmap(bitmap, new Int32Rect(r.X, r.Y, r.Width, r.Height));
            else
            {
                // Rewrite opaque pixels, rather than drawing an overlay over recoverable source pixels.
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                try
                {
                    for (var y = r.Y; y < r.Y + r.Height; y++)
                    for (var x = r.X; x < r.X + r.Width; x++)
                    {
                        var i = (y * bitmap.PixelWidth + x) * 4;
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; pixels[i + 3] = 255;
                    }
                    bitmap = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, bitmap.PixelWidth * 4);
                }
                finally { Array.Clear(pixels); }
            }
        }
        return Encode(bitmap, input.Text);
    }
    public static BitmapSource Decode(ImmutableArray<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray());
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        ValidateSize(bitmap.PixelWidth, bitmap.PixelHeight);
        bitmap.Freeze();
        return bitmap;
    }
    public static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > Policy.MaximumPixels) throw new SafeFailure("image_dimensions_invalid");
    }
    public static Payload Encode(BitmapSource source, string? text = null)
    {
        ValidateSize(source.PixelWidth, source.PixelHeight);
        // Rebuild a fresh frame without EXIF, comments, color profiles, or hidden attachments.
        var clean = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(clean.PixelWidth * clean.PixelHeight * 4)];
        clean.CopyPixels(pixels, clean.PixelWidth * 4, 0);
        using var stream = new MemoryStream();
        try
        {
            var frame = BitmapSource.Create(clean.PixelWidth, clean.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, clean.PixelWidth * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            encoder.Save(stream);
            if (stream.Length > Policy.AbsoluteInputLimit) throw new SafeFailure("image_too_large");
            return new(text, ImmutableArray.CreateRange(stream.ToArray()), clean.PixelWidth, clean.PixelHeight);
        }
        finally { Array.Clear(pixels); if (stream.TryGetBuffer(out var buffer)) Array.Clear(buffer.Array!); }
    }
}
