using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;

namespace Superb.Desktop;

// A local vector drawing: no browser, network, TeX executable or bitmap cache.
// Draw with the current foreground so translucent backgrounds never fade math.
internal sealed class AnswerFormula : Control
{
    private readonly Geometry geometry;
    private readonly Rect bounds;
    internal string Source { get; }
    internal bool Display { get; }

    private AnswerFormula(Geometry geometry, string source, bool display)
    {
        this.geometry = geometry; bounds = geometry.Bounds;
        Source = source; Display = display;
        Focusable = false; IsTabStop = false; IsHitTestVisible = false;
        AutomationProperties.SetName(this, source);
    }

    internal static AnswerFormula? TryCreate(string formula, string source, bool display)
    {
        // Bound parser work for malformed/model-generated input, including deeply
        // nested TeX that could otherwise exhaust the parser stack.
        if (string.IsNullOrWhiteSpace(formula) || formula.Length > 4096) return null;
        var depth = 0; var commands = 0;
        foreach (var c in formula)
        {
            if (c == '{' && ++depth > 64) return null;
            if (c == '}') depth--;
            if (c == '\\' && ++commands > 256) return null;
        }
        try
        {
            var parsed = WpfTeXFormulaParser.Instance.Parse(formula);
            var environment = WpfTeXEnvironment.Create(style: display ? TexStyle.Display : TexStyle.Text,
                scale: 1, systemTextFontName: "Microsoft YaHei UI");
            var shape = parsed.RenderToGeometry(environment, scale: 1);
            var bounds = shape.Bounds;
            if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return null;
            shape.Freeze();
            return new AnswerFormula(shape, source, display);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unsupported commands and incomplete formulas remain readable source.
            return null;
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var scale = Scale(constraint.Width);
        return new Size(bounds.Width * scale, bounds.Height * scale + 4);
    }

    private double Scale(double width) => bounds.Width > 0 ? Math.Min(FontSize, Math.Max(0, width) / bounds.Width) : FontSize;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var scale = Scale(RenderSize.Width);
        drawingContext.PushTransform(new TranslateTransform((RenderSize.Width - bounds.Width * scale) / 2, 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
        drawingContext.DrawGeometry(Foreground, null, geometry);
        drawingContext.Pop(); drawingContext.Pop(); drawingContext.Pop();
    }
}
