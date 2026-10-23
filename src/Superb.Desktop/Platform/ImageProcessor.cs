using System.Collections.Immutable;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal static class ImageProcessor
{
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
