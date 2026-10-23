using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Superb.Core;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

// Explicit opt-in LIVE diagnostic. Unlike the ordinary test suite, this reads the user's
// selected configuration and uses its saved credential. Only the public problem fixture
// below is sent, as an in-memory synthetic screenshot, to the official DeepSeek endpoint.
// It never reads the desktop/clipboard, writes configuration, or records response bodies.
internal static class AlgorithmDiagnostic
{
    internal static int Run(string[] args)
    {
        try
        {
            var config = Configuration.Load(); var scene = config.Scenes.First(s => s.Id == "algorithm");
            var tokenIndex = Array.IndexOf(args, "--tokens");
            if (tokenIndex >= 0)
            {
                var limit = int.Parse(args[tokenIndex + 1], CultureInfo.InvariantCulture);
                if (limit is not (4096 or 16384 or 65536)) throw new SafeFailure("diagnostic_limit_invalid");
                var selected = SceneRequestFactory.SelectBackend(config, scene);
                config = config with { Backends = config.Backends.Replace(selected, selected with { MaxTokens = limit }) };
            }
            const string problem = "3863. Minimum Operations to Sort a String\n\n" +
                "Given a string s consisting of lowercase English letters.\n" +
                "One operation: select any contiguous substring of s,\n" +
                "but NOT the entire string, and sort that substring\n" +
                "in non-descending alphabetical order.\n\n" +
                "Return the minimum operations to sort s.\n" +
                "Return -1 if this is impossible.\n\n" +
                "Examples: dog -> 1; card -> 2; gf -> -1.\n" +
                "Constraints: 1 <= length(s) <= 100000.\n" +
                "s contains only lowercase English letters.";
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1120, 800));
                var text = new FormattedText(problem, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 28, Brushes.Black, 1) { MaxTextWidth = 1040 };
                drawing.DrawText(text, new Point(35, 30));
            }
            var bitmap = new RenderTargetBitmap(1120, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var payload = ImageProcessor.Encode(bitmap);
            var request = SceneRequestFactory.Prepare(config, scene, new(Guid.NewGuid(), InputKind.Screen, payload, 0, DateTimeOffset.UtcNow));
            if (args.Contains("--direct-answer")) request = request with { Backend = request.Backend with { ThinkingMode = ThinkingMode.Disabled } };
            if (args.Contains("--timeout-300")) request = request with { Backend = request.Backend with { TimeoutSeconds = 300 } };
            if (request.Backend.Endpoint != DeepSeekSetup.Endpoint || request.Backend.Model != "deepseek-flash")
                throw new SafeFailure("diagnostic_target_not_supported");
            using var client = new ChatCompletionsClient(new CredentialStore());
            var coordinator = new Coordinator(client); coordinator.SetMode(RunState.Ready);
            var watch = Stopwatch.StartNew(); var thinking = false;
            coordinator.Updated += update =>
            {
                if (update.State == RequestState.Thinking && !thinking)
                { thinking = true; Console.WriteLine("LIVE: thinking activity received; reasoning text discarded"); }
            };
            Console.WriteLine($"LIVE: public synthetic screenshot, algorithm scene, tokens={request.Backend.MaxTokens}, thinkingMode={request.Backend.ThinkingMode}");
            coordinator.RunAsync(request).GetAwaiter().GetResult();
            var result = coordinator.Latest!;
            Console.WriteLine($"LIVE: state={result.State}, code={result.Code ?? "none"}, answerChars={result.Text.Length}, thinking={thinking}, seconds={watch.Elapsed.TotalSeconds:F1}");
            return result.State == RequestState.Completed ? 0 : 2;
        }
        catch (Exception ex)
        { Console.WriteLine("LIVE: " + (ex is SafeFailure failure ? failure.Code : "diagnostic_failed")); return 1; }
    }
}
