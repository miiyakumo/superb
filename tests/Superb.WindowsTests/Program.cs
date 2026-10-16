using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

internal static class Program
{
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    [STAThread]
    private static int Main(string[] args)
    {
        var tests = new List<(string, Action)>();
        var source = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 64).ToArray(), 16);
        var input = ImageProcessor.Encode(source);
        var processor = new ImageProcessor();
        tests.Add(("Mask rewrites exported PNG pixels with opaque black", () =>
        {
            var output = processor.Apply(input, new(StepKind.Mask, Rectangle: new(1, 1, 2, 2)));
            var decoded = new FormatConvertedBitmap(ImageProcessor.Decode(output.Png), PixelFormats.Bgra32, null, 0);
            var pixels = new byte[64]; decoded.CopyPixels(pixels, 16, 0);
            for (var y = 0; y < 4; y++) for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4; var redacted = x is 1 or 2 && y is 1 or 2;
                Assert(pixels[i] == (redacted ? 0 : 255) && pixels[i + 1] == pixels[i] && pixels[i + 2] == pixels[i] && pixels[i + 3] == 255);
            }
        }));
        tests.Add(("Crop changes output dimensions without changing original snapshot", () =>
        {
            var output = processor.Apply(input, new(StepKind.Crop, Rectangle: new(1, 1, 2, 3)));
            Assert(output.Width == 2 && output.Height == 3 && input.Width == 4 && input.Height == 4);
        }));
        tests.Add(("Out-of-range mandatory mask blocks processing", () =>
        {
            try { Preprocessor.Process(input, new("v1", [new(StepKind.Mask, Rectangle: new(3, 3, 2, 2))]), processor); throw new Exception("Accepted invalid mask"); }
            catch (SafeFailure ex) { Assert(ex.Code == "processing_required_failed"); }
        }));
        tests.Add(("Current-user DPAPI credential round trip and path confinement", () =>
        {
            var store = new CredentialStore(); var reference = store.Save("synthetic-test-key");
            try { Assert(store.Read(reference) == "synthetic-test-key"); }
            finally { store.Delete(reference); }
            try { store.Read("../../arbitrary-file"); throw new Exception("Accepted invalid reference"); }
            catch (SafeFailure ex) { Assert(ex.Code == "credential_unavailable"); }
        }));
        if (args.Contains("--interactive")) tests.Add(("Protection attributes and no-activate styles on an interactive desktop", () =>
        {
            var window = new Window { ShowActivated = false, ShowInTaskbar = false, Topmost = true, Width = 200, Height = 120 };
            new WindowInteropHelper(window).EnsureHandle(); var protection = new Protection(window, true);
            try { Assert(protection.Install()); window.Show(); Assert(protection.Check()); }
            finally { window.Close(); }
            // Attribute checks intentionally do not assert screenshot exclusion.
        }));
        if (args.Contains("--clipboard")) tests.Add(("Clipboard ownership, history markers, race guard and source filtering", () =>
        {
            // Explicit opt-in: this overwrites the current clipboard with synthetic test data.
            using var first = new HwndSource(new HwndSourceParameters("Superb test owner") { ParentWindow = new nint(-3) });
            using var second = new HwndSource(new HwndSourceParameters("Superb test competitor") { ParentWindow = new nint(-3) });
            var a = new ClipboardService(first.Handle); var b = new ClipboardService(second.Handle);
            Assert(a.Write("synthetic-one", Guid.NewGuid(), 1, null)); var baseline = a.Sequence;
            Assert(a.Read().OwnOutput);
            Assert(Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing")));
            Assert(b.Write("synthetic-two", Guid.NewGuid(), 2, null));
            Assert(!a.Write("must-not-overwrite", Guid.NewGuid(), 3, baseline));
            var beforeClear = a.Sequence; a.ClearOwned(); Assert(a.Sequence == beforeClear);
            b.ClearOwned();
        }));
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine("PASS " + name); }
            catch { failures++; Console.WriteLine("FAIL " + name); }
        }
        Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed; capture-chain verification requires manual evidence.");
        return failures == 0 ? 0 : 1;
    }
}
