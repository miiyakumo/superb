using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Superb.Desktop;

namespace Superb.WindowsTests;

internal static class MathRenderingTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Text(AnswerView answer) => new TextRange(answer.Reader.Document.ContentStart, answer.Reader.Document.ContentEnd).Text;
    private static IEnumerable<AnswerFormula> Formulas(DependencyObject parent)
    {
        if (parent is AnswerFormula formula) yield return formula;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var item in Formulas(child)) yield return item;
    }

    internal static void Add(List<(string, Action)> tests, string[] args)
    {
        tests.Add(("Math renders four delimiter forms, Chinese adjacency and nested Markdown", () =>
        {
            const string source = "中文$x^2$紧接文字，以及 \\(\\frac{a}{b}\\)。\n\n$$\\sqrt{x}$$\n\n" +
                "\\[\n\\sum_{i=1}^{n} i = \\frac{n(n+1)}{2}\n\\]\n\n" +
                "> 引用 **含 $a_1$**\n\n- 列表 $b^2$\n\n| 变量 | 公式 |\n| --- | --- |\n| 面积 | $\\pi r^2$ |";
            var answer = new AnswerView { FontSize = 18 }; answer.Render(source);
            var formulas = Formulas(answer.Reader.Document).ToArray();
            Check(formulas.Length == 7, $"Expected 7 formulas, got {formulas.Length}: {Text(answer)}");
            Check(formulas.Count(f => f.Display) == 2, "Display formulas should use display math layout");
            Check(formulas.All(f => !f.Focusable && !f.IsHitTestVisible && !f.IsTabStop), "Formulas must not take input");
            Check(answer.Text == source, "Copy source must remain exact");
        }));
        tests.Add(("Math streaming preserves stable blocks and falls back until delimiters close", () =>
        {
            foreach (var tail in new[] { "值为 $\\frac{1}{2}$。", "值为 \\(x^2\\)。", "$$\n\\frac{1}{2}\n$$", "\\[\n\\sqrt{x}\n\\]", "$$x^2$$", "$$ x^2 $$" })
            {
                const string prefix = "# 保持不变\n\n";
                var answer = new AnswerView(); answer.Render(prefix);
                var heading = answer.Reader.Document.Blocks.FirstBlock;
                for (var i = 1; i <= tail.Length; i++)
                {
                    answer.Render(prefix + tail[..i]);
                    Check(ReferenceEquals(heading, answer.Reader.Document.Blocks.FirstBlock), "Stable prefix was replaced");
                    Check(answer.Text == prefix + tail[..i], "Streaming source changed");
                }
                Check(Formulas(answer.Reader.Document).Count() == 1, "Completed stream did not render: " + tail);
            }
            foreach (var source in new[] { "$$\n\\frac{1}{", "\\[\nx^2", "$$x$", "$x", "\\(x", "\\(", "\\(\\)" })
            {
                var answer = new AnswerView(); answer.Render(source);
                Check(!Formulas(answer.Reader.Document).Any(), "Unclosed math should show source: " + source);
                Check(Text(answer).Contains(source), "Unclosed math source lost: " + source);
            }
        }));
        tests.Add(("Math keeps code, escaped delimiters, prices and invalid TeX readable", () =>
        {
            var answer = new AnswerView();
            const string source = "`$x$` and `\\(x\\)`\n\n```latex\n$$x$$\n\\[x\\]\n```\n\n    $x$\n\n" +
                "价格 $5 and $10，转义 \\$x\\$，\\\\(x\\\\)。\n\n$\\unsupportedcommand{x}$\n\n$$\n\\frac{1}{\n$$";
            answer.Render(source);
            Check(!Formulas(answer.Reader.Document).Any(), "Literal or malformed math was interpreted");
            Check(Text(answer).Contains("$5 and $10") && Text(answer).Contains("$\\unsupportedcommand{x}$"), "Fallback lost source");
            Check(answer.Text == source, "Literal copy source changed");
            Check(AnswerFormula.TryCreate(new string('{', 100) + "x" + new string('}', 100), "deep", false) is null, "Deep math was not bounded");
            answer.Render("$$\nx^2\n\ny^2\n$$");
            Check(Formulas(answer.Reader.Document).Count() == 1, "Blank line broke display math");
        }));
        tests.Add(("Math and typography resize, restyle and fit a narrow reading surface", () =>
        {
            const string source = "# 公式与阅读排版\n\n用 $E=mc^2$ 表示质能关系。行内分式 \\(\\frac{a+b}{c}\\) 与正文一起阅读。\n\n" +
                "## 求解一元二次方程\n\n当 $a \\ne 0$ 时，方程 $ax^2+bx+c=0$ 的解为：\n\n" +
                "$$\nx=\\frac{-b\\pm\\sqrt{b^2-4ac}}{2a}\n$$\n\n" +
                "1. 先计算判别式 $\\Delta=b^2-4ac$。\n2. 再根据符号判断实根个数。\n\n" +
                "> **阅读提示**：公式随回答字号缩放，复制保留原始 LaTeX。\n\n" +
                "| 条件 | 实根个数 |\n| :--- | ---: |\n| $\\Delta>0$ | 2 |\n| $\\Delta=0$ | 1 |\n\n" +
                "```python\ndelta = b * b - 4 * a * c\n```";
            var index = Array.IndexOf(args, "--render-dir");
            foreach (var themeId in new[] { Theme.PaperId, Theme.InkId })
            {
                Check(Theme.TryResolve(themeId, "", "", "", out var palette, out _), "Missing theme"); Theme.Apply(palette);
                var answer = new AnswerView { FontSize = 18, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") };
                answer.Render(source);
                var formula = Formulas(answer.Reader.Document).First();
                formula.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); var width = formula.DesiredSize.Width;
                answer.FontSize = 30;
                formula.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Check(formula.DesiredSize.Width > width * 1.5, "Formula did not resize with answer font");
                answer.FontSize = 18; answer.Restyle();
                Check(Formulas(answer.Reader.Document).All(f => ((SolidColorBrush)f.Foreground).Color == palette.Ink), "Math lost theme foreground");
                foreach (var size in new[] { 600, 360 })
                {
                    var surface = new Border { Background = new SolidColorBrush(palette.Base), Padding = new Thickness(12), Child = answer };
                    surface.Measure(new Size(size, 900)); surface.Arrange(new Rect(0, 0, size, 900)); surface.UpdateLayout();
                    Check(Formulas(answer.Reader.Document).All(f => f.ActualHeight > 0 && f.ActualWidth <= size - 48), "Formula clipped or missing");
                    if (index >= 0 && index + 1 < args.Length)
                    {
                        Directory.CreateDirectory(args[index + 1]);
                        var bitmap = new RenderTargetBitmap(size, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(args[index + 1], $"math-{themeId}-{size}.png")); encoder.Save(output);
                    }
                    surface.Child = null;
                }
                var longFormula = AnswerFormula.TryCreate(string.Join("+", Enumerable.Repeat("x^2", 40)), "long", true)!;
                longFormula.FontSize = 36; longFormula.Measure(new Size(260, double.PositiveInfinity));
                Check(longFormula.DesiredSize.Width <= 260, "Long formula exceeds available width");
                foreach (var delimiter in new[] { "$", "$$" })
                {
                    answer.Render(delimiter + string.Join("+", Enumerable.Repeat("x^2", 40)) + delimiter);
                    var narrow = new Border { Child = answer };
                    narrow.Measure(new Size(300, 300)); narrow.Arrange(new Rect(0, 0, 300, 300)); narrow.UpdateLayout();
                    Check(Formulas(answer.Reader.Document).Single().ActualWidth <= 276, "Long math overflows the real document");
                    narrow.Child = null;
                }
            }
        }));
        tests.Add(("Markdown uses soft wrapping while retaining explicit hard breaks", () =>
        {
            var answer = new AnswerView { FontSize = 18 }; answer.Render("soft\nwrap  \nhard\n\nnext");
            var paragraph = (Paragraph)answer.Reader.Document.Blocks.FirstBlock;
            Check(paragraph.Inlines.OfType<LineBreak>().Count() == 1 && Text(answer).Contains("soft wrap"), "Soft wrap became forced line break");
            Check(paragraph.LineHeight >= 18 * 1.6, "Body leading is too tight");
        }));
    }
}
