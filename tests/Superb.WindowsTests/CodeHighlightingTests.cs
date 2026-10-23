using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Superb.Desktop;

namespace Superb.WindowsTests;

internal static class CodeHighlightingTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Code highlighting assertion failed"); }
    private static string Text(IEnumerable<Run> runs) => string.Concat(runs.Select(r => r.Text));
    private static Color ColorAt(IReadOnlyList<Run> runs, int offset)
    {
        foreach (var run in runs)
        {
            if (offset < run.Text.Length) return ((SolidColorBrush)run.Foreground).Color;
            offset -= run.Text.Length;
        }
        throw new ArgumentOutOfRangeException(nameof(offset));
    }

    internal static void Add(List<(string, Action)> tests, string[] args)
    {
        tests.Add(("Code highlighting recognizes common language aliases and retains every character", () =>
        {
            (string Tags, string Code)[] samples =
            [
                ("c,h,cpp,c++,cc,cxx,hpp", "// 注释\r\nint main() { return 42; }"),
                ("cs,csharp,c#", "// 注释\nvar text = \"你好\\n\";\nint n = 42;"),
                ("py,python,python3", "# 注释\ndef greet(name):\n\treturn \"你好\" + str(42)\n\n"),
                ("js,javascript,jsx,ts,typescript,tsx", "// 注释\nfunction greet(name) { return \"hello\" + 42; }"),
                ("java", "// 注释\npublic class Main { int n = 42; }"),
                ("json,jsonc", "{\"name\": \"你好\", \"value\": 42, \"ok\": true, \"nil\": null}"),
                ("html,htm", "<!-- 注释 -->\n<div class=\"x\">&amp; 你好</div>"),
                ("xml,xaml,svg", "<!-- 注释 -->\n<Button Content=\"你好\" />"),
                ("css", "/* 注释 */\n.item { color: red; font-size: 16px; }"),
                ("sql,tsql", "-- 注释\nSELECT name FROM users WHERE id = 42;"),
                ("ps,ps1,powershell,pwsh", "# 注释\nif ($true) { Write-Output \"你好\" }\n$value = 42")
            ];
            foreach (var (tags, code) in samples)
                foreach (var tag in tags.Split(','))
                {
                    var runs = CodeHighlighting.CreateRuns(code, " " + tag.ToUpperInvariant() + " ", Brushes.White);
                    Check(Text(runs) == code && runs.Count > 1);
                    Check(runs.Any(r => r.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color != Colors.White));
                }
        }));
        tests.Add(("Code colors distinguish keywords, numbers, strings and multiline comments without bleeding", () =>
        {
            const string code = "/* if 99\n  return \"text\" */\nvar text = \"return 42\";\nint n = 42;";
            var runs = CodeHighlighting.CreateRuns(code, "cs", Brushes.White);
            var comment = ColorAt(runs, code.IndexOf("if", StringComparison.Ordinal));
            Check(comment == ColorAt(runs, code.IndexOf("return", StringComparison.Ordinal)));
            var keyword = ColorAt(runs, code.IndexOf("var", StringComparison.Ordinal));
            var literal = ColorAt(runs, code.IndexOf("return 42", StringComparison.Ordinal));
            var number = ColorAt(runs, code.LastIndexOf("42", StringComparison.Ordinal));
            Check(new[] { comment, keyword, literal, number }.Distinct().Count() == 4);
            Check(keyword == Color.FromRgb(0xC6, 0xA4, 0xE8));
            var light = CodeHighlighting.CreateRuns(code, "cs", Brushes.Black);
            Check(ColorAt(light, code.IndexOf("var", StringComparison.Ordinal)) == Color.FromRgb(0x6B, 0x4C, 0x9A));
            Check(ColorAt(light, code.IndexOf("return 42", StringComparison.Ordinal)) == Color.FromRgb(0x8A, 0x5A, 0x12));
            Check(Theme.Luminance(ColorAt(light, code.IndexOf("var", StringComparison.Ordinal))) < 0.4);
            Check(Text(runs) == code);
            const string python = "value = '''first\nreturn 42\n'''\nreturn 42";
            var pyRuns = CodeHighlighting.CreateRuns(python, "python", Brushes.White);
            Check(ColorAt(pyRuns, python.IndexOf("return", StringComparison.Ordinal)) != ColorAt(pyRuns, python.LastIndexOf("return", StringComparison.Ordinal)));
        }));
        tests.Add(("Unlabelled, unsupported and very large code blocks fall back to exact plain text", () =>
        {
            const string code = "if (value < 42) return \"<script>\";\n\t\n";
            foreach (var language in new string?[] { null, "", "text", "unknown", "https://example.test/grammar" })
            {
                var runs = CodeHighlighting.CreateRuns(code, language, Brushes.White);
                Check(runs.Count == 1 && Text(runs) == code);
                Check(runs[0].ReadLocalValue(TextElement.ForegroundProperty) == DependencyProperty.UnsetValue);
            }
            foreach (var large in new[] { new string('x', CodeHighlighting.MaximumLength + 1), string.Concat(Enumerable.Repeat("if ", 5_000)) })
            {
                var runs = CodeHighlighting.CreateRuns(large, "cs", Brushes.White);
                Check(runs.Count == 1 && Text(runs) == large);
            }
        }));
        tests.Add(("Streamed highlighted fences match a complete render and preserve existing blocks and source", () =>
        {
            const string prefix = "# 代码高亮\n\n";
            const string tail = "```cs\n/* first\nsecond */\nvar text = \"<script>你好\";\nint n = 42;\n```";
            var answer = new AnswerView { FontSize = 16 }; answer.Render(prefix);
            var first = answer.Reader.Document.Blocks.FirstBlock;
            for (var i = 1; i <= tail.Length; i++)
            {
                answer.Render(prefix + tail[..i]);
                Check(ReferenceEquals(first, answer.Reader.Document.Blocks.FirstBlock));
            }
            var complete = new AnswerView(); complete.Render(prefix + tail);
            var runs = ((Paragraph)answer.Reader.Document.Blocks.LastBlock).Inlines.OfType<Run>().ToArray();
            var expected = ((Paragraph)complete.Reader.Document.Blocks.LastBlock).Inlines.OfType<Run>().ToArray();
            Check(runs.Select(r => (r.Text, r.Foreground.ToString())).SequenceEqual(expected.Select(r => (r.Text, r.Foreground.ToString()))));
            Check(runs.Select(r => r.Foreground.ToString()).Distinct().Count() >= 4);
            Check(answer.Text == prefix + tail && Text(runs).Contains("<script>你好"));
            answer.FontSize = 24; Check(runs.All(r => r.FontSize == 24));
            answer.Render("```python title=sample\ndef greet():\n    return 42\n```");
            Check(((Paragraph)answer.Reader.Document.Blocks.FirstBlock).Inlines.OfType<Run>().Any(r => r.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush));
            SavePreview(answer, args);
            answer.Clear(); Check(answer.Text == "" && answer.Reader.Document.Blocks.Count == 0);
        }));
    }

    private static void SavePreview(AnswerView answer, string[] args)
    {
        var index = Array.IndexOf(args, "--render-dir");
        if (index < 0 || index + 1 >= args.Length) return;
        answer.FontSize = 18;
        answer.Render("## Python\n\n```python\n# 保留首次出现的元素\ndef unique(values):\n    seen = set()\n    for value in values:\n        if value not in seen:\n            seen.add(value)\n            yield value\n\nprint(list(unique([1, 2, 2, 3])))\n```\n\n" +
            "## C#\n\n```csharp\n// 基础语法高亮\nvar message = \"Hello, Superb!\";\nfor (int i = 0; i < 3; i++)\n{\n    Console.WriteLine(message);\n}\n```\n\n" +
            "## JSON\n\n```json\n{\"name\": \"Superb\", \"enabled\": true, \"count\": 42}\n```");
        var surface = new Border { Width = 760, Height = 960, Background = new SolidColorBrush(Color.FromRgb(17, 26, 22)), Padding = new Thickness(20), Child = answer };
        surface.Measure(new Size(760, 960)); surface.Arrange(new Rect(0, 0, 760, 960)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(760, 960, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(args[index + 1]);
        using var output = File.Create(Path.Combine(args[index + 1], "code-highlighting.png")); encoder.Save(output);
    }
}
