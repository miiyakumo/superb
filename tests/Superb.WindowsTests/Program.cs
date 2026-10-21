using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Superb.Desktop;
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
        tests.Add(("Existing backend JSON retains direct transport and legacy generation defaults", () =>
        {
            const string old = "{\"Id\":\"old\",\"Name\":\"old\",\"Endpoint\":\"https://example.test/v1/chat/completions\",\"Model\":\"test\",\"CredentialRef\":\"\"}";
            var saved = JsonSerializer.Deserialize<Backend>(old, Configuration.Json)!;
            Policy.ValidateBackend(saved);
            Assert(saved.ProxyMode == ProxyMode.Direct && saved.SendTemperature && saved.TokenLimitParameter == TokenLimitParameter.MaxTokens);
        }));
        tests.Add(("Saved proxy configuration and DPAPI credentials authenticate a real HTTP request", () =>
        {
            var store = new CredentialStore(); var reference = store.Save("synthetic-test-key");
            try { TestSavedBackend(store, reference).GetAwaiter().GetResult(); }
            finally { store.Delete(reference); }
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
    private static async Task TestSavedBackend(CredentialStore store, string reference)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var configured = new Backend("saved", "synthetic", $"http://127.0.0.1:{((IPEndPoint)unused.LocalEndpoint).Port}/v1/chat/completions", "test", reference,
            AllowLoopbackHttp: true, Stream: false, ProxyMode: ProxyMode.Custom, ProxyAddress: $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            SendTemperature: false, TokenLimitParameter: TokenLimitParameter.MaxCompletionTokens);
        var json = JsonSerializer.Serialize(new Configuration { Backends = [configured], SelectedBackendId = configured.Id }, Configuration.Json);
        Assert(!json.Contains("synthetic-test-key"));
        var restored = JsonSerializer.Deserialize<Configuration>(json, Configuration.Json)!; restored.Validate();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = socket.GetStream(); using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            var authorized = false; string? line; var size = 0;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellation.Token)))
            {
                if (line == "Authorization: Bearer synthetic-test-key") authorized = true;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) size = int.Parse(line[15..]);
            }
            var input = new char[size]; await reader.ReadBlockAsync(input.AsMemory(), cancellation.Token);
            using var request = JsonDocument.Parse(new string(input));
            Assert(request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == BackendProbe.TestPrompt);
            Assert(!request.RootElement.TryGetProperty("temperature", out _) && request.RootElement.TryGetProperty("max_completion_tokens", out _));
            const string body = "{\"choices\":[{\"message\":{\"content\":\"OK\",\"tool_calls\":null},\"finish_reason\":\"stop\"}]}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), cancellation.Token);
            return authorized;
        });
        using var client = new ChatCompletionsClient(store);
        var result = await BackendProbe.RunAsync(client, restored.Backends[0], cancellation.Token);
        Assert(result.Success && await server && !unused.Pending());
    }
}
