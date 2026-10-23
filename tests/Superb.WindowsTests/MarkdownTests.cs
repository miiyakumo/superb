using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Superb.Desktop;

namespace Superb.WindowsTests;

internal static class MarkdownTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Markdown assertion failed"); }
    private static string Text(AnswerView answer) => new TextRange(answer.Reader.Document.ContentStart, answer.Reader.Document.ContentEnd).Text;
    internal static void Add(List<(string, Action)> tests, string[] args)
    {
        CodeHighlightingTests.Add(tests, args);
        tests.Add(("Markdown renders native headings, nested lists, emphasis, quotes, code and tables while retaining source", () =>
        {
            const string markdown = "# 回答示例\n\n**重点**、*强调*、~~删除~~ 和 `inline_code`。\n\n" +
                "3. 第三项\n4. 第四项\n   - 嵌套项\n\n> 引用内容\n\n---\n\n" +
                "| 项目 | 复杂度 |\n| :--- | ---: |\n| 查询 | O(1) |\n\n```csharp\nvar x = \"<script>\";\n```\n";
            var answer = new AnswerView { FontSize = 16 }; answer.Render(markdown);
            var blocks = answer.Reader.Document.Blocks.ToArray();
            Check(blocks.Length == 7);
            var heading = (Paragraph)blocks[0];
            Check(heading.FontWeight == FontWeights.Bold && heading.FontSize > 16);
            var paragraph = (Paragraph)blocks[1];
            Check(paragraph.Inlines.OfType<Span>().Any(s => s.FontWeight == FontWeights.Bold));
            Check(paragraph.Inlines.OfType<Span>().Any(s => s.FontStyle == FontStyles.Italic));
            Check(paragraph.Inlines.OfType<Span>().Any(s => s.TextDecorations == TextDecorations.Strikethrough));
            Check(paragraph.Inlines.OfType<Run>().Any(r => r.FontFamily.Source == "Consolas"));
            var list = (System.Windows.Documents.List)blocks[2];
            Check(list.StartIndex == 3 && list.ListItems.Count == 2);
            Check(list.ListItems.LastListItem.Blocks.OfType<System.Windows.Documents.List>().Any());
            Check(blocks[3] is Section { BorderThickness.Left: 3 });
            var table = (Table)blocks[5];
            Check(table.RowGroups[0].Rows.Count == 2 && table.RowGroups[0].Rows[0].Cells[0].FontWeight == FontWeights.Bold);
            Check(table.RowGroups[0].Rows[1].Cells[1].TextAlignment == TextAlignment.Right);
            Check(blocks[6] is Paragraph { FontFamily.Source: "Consolas" } && Text(answer).Contains("<script>"));
            Check(answer.Text == markdown && !Text(answer).Contains("```"));
            answer.FontSize = 24; Check(heading.FontSize > 24 && ((Paragraph)blocks[6]).FontSize == 24);
            var index = Array.IndexOf(args, "--render-dir");
            if (index >= 0 && index + 1 < args.Length)
            {
                var surface = new Border { Width = 600, Height = 900, Background = new SolidColorBrush(Color.FromRgb(17, 26, 22)), Padding = new Thickness(20), Child = answer };
                surface.Measure(new Size(600, 900)); surface.Arrange(new Rect(0, 0, 600, 900)); surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap(600, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(args[index + 1]);
                using var output = File.Create(Path.Combine(args[index + 1], "markdown-answer.png")); encoder.Save(output);
            }
        }));
        tests.Add(("Streaming reparses unfinished Markdown and preserves completed blocks and late reference links", () =>
        {
            var answer = new AnswerView();
            const string prefix = "# Stable heading\n\n";
            answer.Render(prefix); var first = answer.Reader.Document.Blocks.FirstBlock;
            const string tail = "**bold** and `code`\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n~~~text\nliteral **stars**\n~~~";
            for (var i = 1; i <= tail.Length; i++)
            {
                answer.Render(prefix + tail[..i]);
                Check(ReferenceEquals(first, answer.Reader.Document.Blocks.FirstBlock));
            }
            Check(answer.Reader.Document.Blocks.Count == 4 && Text(answer).Contains("literal **stars**"));
            answer.Render("Title\n"); Check(answer.Reader.Document.Blocks.FirstBlock is Paragraph { FontWeight: var weight } && weight != FontWeights.Bold);
            answer.Render("Title\n===\n"); Check(answer.Reader.Document.Blocks.FirstBlock is Paragraph { FontWeight: var bold } && bold == FontWeights.Bold);
            answer.Render("**unfinished [link"); Check(Text(answer).Contains("**unfinished [link"));
            answer.Render("[label][ref]\n\n"); Check(Text(answer).Contains("[label][ref]"));
            answer.Render("[label][ref]\n\n[ref]: https://example.test/reference\n");
            Check(Text(answer).Contains("https://example.test/reference") && !Text(answer).Contains("[ref]"));
            answer.Clear(); Check(answer.Text == "" && answer.Reader.Document.Blocks.Count == 0);
        }));
        tests.Add(("Markdown treats HTML, commands and remote media as inert text", () =>
        {
            var answer = new AnswerView();
            const string markdown = "<script>alert('literal')</script>\n\n" +
                "[命令](file:///C:/Windows/System32/cmd.exe)\n\n![远程图](https://example.test/pixel.png)\n\n" +
                "<https://example.test/> &amp;\n\n<x:Button Command=\"run\" />";
            answer.Render(markdown);
            Check(Text(answer).Contains("<script>") && Text(answer).Contains("<x:Button"));
            Check(Text(answer).Contains("[图片：远程图]") && Text(answer).Contains("file:///C:/Windows/System32/cmd.exe"));
            foreach (var paragraph in answer.Reader.Document.Blocks.OfType<Paragraph>())
            {
                Check(!paragraph.Inlines.OfType<Hyperlink>().Any() && !paragraph.Inlines.OfType<InlineUIContainer>().Any());
                foreach (var span in paragraph.Inlines.OfType<Span>()) Check(!span.Inlines.OfType<Hyperlink>().Any());
            }
            Check(answer.Text == markdown);
        }));
    }
}
