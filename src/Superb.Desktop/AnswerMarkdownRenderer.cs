using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using Block = System.Windows.Documents.Block;
using List = System.Windows.Documents.List;
using Table = System.Windows.Documents.Table;
using TableRow = System.Windows.Documents.TableRow;
using TableCell = System.Windows.Documents.TableCell;

namespace Superb.Desktop;

// Converts a parsed tree directly to text elements. No HTML/XAML interpreter,
// navigation handler, image loader, or executable content is involved.
internal sealed class AnswerMarkdownRenderer(AnswerView owner)
{
    private Brush Surface => (Brush)owner.FindResource("Surface");
    private Brush Line => (Brush)owner.FindResource("Line");
    private Brush Accent => (Brush)owner.FindResource("Accent");

    internal Block Render(MdBlock source)
    {
        switch (source)
        {
            case HeadingBlock heading:
                var title = Paragraph(heading);
                title.FontWeight = FontWeights.Bold;
                title.Margin = new Thickness(0, heading.Level <= 2 ? 22 : 16, 0, 10);
                title.KeepWithNext = true;
                Scale(title, Block.LineHeightProperty, heading.Level <= 2 ? 1.9 : 1.65);
                title.SetBinding(TextElement.FontSizeProperty, new Binding(nameof(owner.FontSize))
                {
                    Source = owner, Converter = FontScale.Instance,
                    ConverterParameter = heading.Level switch { 1 => 1.5, 2 => 1.28, 3 => 1.12, _ => 1.0 }
                });
                return title;
            case ParagraphBlock paragraph:
                return RenderParagraph(paragraph);
            case AnswerMathBlock math:
                var formula = math.Lines.ToString();
                var raw = math.Opening + "\n" + formula + (math.Complete ? "\n" + math.Closing : "");
                return DisplayFormula(formula, raw, math.Complete);
            case CodeBlock code:
                var codeParagraph = new Paragraph
                {
                    FontFamily = new FontFamily("Consolas"), Background = Surface,
                    Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 6, 0, 16),
                    BorderBrush = Line, BorderThickness = new Thickness(1)
                };
                Scale(codeParagraph, Block.LineHeightProperty, 1.45);
                foreach (var run in CodeHighlighting.CreateRuns(code.Lines.ToString(), (code as FencedCodeBlock)?.Info,
                    (SolidColorBrush)owner.FindResource("Ink"))) codeParagraph.Inlines.Add(run);
                return codeParagraph;
            case ListBlock list:
                var result = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Padding = new Thickness(26, 0, 0, 0), Margin = new Thickness(0, 2, 0, 12), MarkerOffset = 7
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0)
                    result.StartIndex = start;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var output = new ListItem { Margin = new Thickness(0, 0, 0, 5) };
                    foreach (var child in item)
                    {
                        var rendered = Render(child);
                        if (list.IsLoose == false && rendered is Paragraph) rendered.Margin = new Thickness(0, 0, 0, 3);
                        output.Blocks.Add(rendered);
                    }
                    result.ListItems.Add(output);
                }
                return result;
            case QuoteBlock quote:
                var section = new Section
                {
                    BorderBrush = Accent, BorderThickness = new Thickness(2, 0, 0, 0), Background = Surface,
                    Padding = new Thickness(14, 8, 12, 0), Margin = new Thickness(0, 6, 0, 16)
                };
                foreach (var child in quote) section.Blocks.Add(Render(child));
                return section;
            case MdTable table:
                return RenderTable(table);
            case ThematicBreakBlock:
                return new Paragraph
                {
                    BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 8, 0, 12), FontSize = 1
                };
            case LeafBlock leaf:
                return Paragraph(leaf);
            case ContainerBlock container:
                var group = new Section();
                foreach (var child in container) group.Blocks.Add(Render(child));
                return group;
            default:
                return new Paragraph();
        }
    }

    private Paragraph Paragraph(LeafBlock source)
    {
        var result = BodyParagraph();
        if (source.Inline is { } inline) AddInlines(result.Inlines, inline);
        else result.Inlines.Add(new Run(source.Lines.ToString()));
        return result;
    }

    private void Scale(FrameworkContentElement element, DependencyProperty property, double factor) =>
        element.SetBinding(property, new Binding(nameof(owner.FontSize))
        {
            Source = owner, Converter = FontScale.Instance, ConverterParameter = factor
        });

    private Paragraph BodyParagraph()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        // MaxHeight stacking allows tall inline fractions to expand a line.
        Scale(paragraph, Block.LineHeightProperty, 1.65);
        return paragraph;
    }

    private Block RenderParagraph(ParagraphBlock source)
    {
        if (source.Inline is not { } inline || !inline.Any(i => i is AnswerMathInline { Display: true })) return Paragraph(source);
        var section = new Section();
        var paragraph = BodyParagraph();
        foreach (var child in inline)
        {
            if (child is AnswerMathInline { Display: true } math)
            {
                if (paragraph.Inlines.Count > 0) section.Blocks.Add(paragraph);
                section.Blocks.Add(DisplayFormula(math.Formula, math.Source, true));
                paragraph = BodyParagraph();
            }
            else AddInline(paragraph.Inlines, child);
        }
        if (paragraph.Inlines.Count > 0) section.Blocks.Add(paragraph);
        return section;
    }

    private AnswerFormula? Formula(string formula, string source, bool display)
    {
        var visual = AnswerFormula.TryCreate(formula, source, display);
        if (visual is null) return null;
        visual.SetBinding(Control.FontSizeProperty, new Binding(nameof(owner.FontSize)) { Source = owner });
        visual.SetResourceReference(Control.ForegroundProperty, "Ink");
        return visual;
    }

    private Block DisplayFormula(string formula, string source, bool complete)
    {
        if (complete && Formula(formula, source, true) is { } visual)
            return new BlockUIContainer(visual) { Margin = new Thickness(0, 8, 0, 18), Padding = new Thickness(8, 4, 8, 4) };
        var fallback = BodyParagraph();
        fallback.Inlines.Add(new Run(source) { FontFamily = new FontFamily("Consolas") });
        return fallback;
    }

    private Table RenderTable(MdTable source)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 18), BorderBrush = Line, BorderThickness = new Thickness(0.5) };
        for (var i = 0; i < source.ColumnDefinitions.Count; i++) table.Columns.Add(new TableColumn());
        var rows = new TableRowGroup(); table.RowGroups.Add(rows);
        foreach (var row in source.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var output = new TableRow(); rows.Rows.Add(output);
            var column = 0;
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var target = new TableCell
                {
                    BorderBrush = Line, BorderThickness = new Thickness(0.5), Padding = new Thickness(10, 8, 10, 8),
                    ColumnSpan = Math.Max(1, cell.ColumnSpan), RowSpan = Math.Max(1, cell.RowSpan)
                };
                if (row.IsHeader) { target.FontWeight = FontWeights.Bold; target.Background = Surface; }
                var columnIndex = cell.ColumnIndex >= 0 ? cell.ColumnIndex : column;
                if (columnIndex < source.ColumnDefinitions.Count)
                    target.TextAlignment = source.ColumnDefinitions[columnIndex].Alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left
                    };
                foreach (var block in cell)
                {
                    var rendered = Render(block);
                    rendered.Margin = new Thickness(0);
                    target.Blocks.Add(rendered);
                }
                output.Cells.Add(target);
                column = columnIndex + target.ColumnSpan;
            }
        }
        return table;
    }

    private void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
            AddInline(target, inline);
    }

    private void AddInline(InlineCollection target, Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case AnswerMathInline math:
                if (Formula(math.Formula, math.Source, math.Display) is { } visual)
                    target.Add(new InlineUIContainer(visual) { BaselineAlignment = BaselineAlignment.Center });
                else target.Add(new Run(math.Source));
                break;
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString())); break;
            case CodeInline code:
                target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = Surface }); break;
            case LineBreakInline lineBreak:
                if (lineBreak.IsHard) target.Add(new LineBreak());
                else target.Add(new Run(" "));
                break;
            case EmphasisInline emphasis:
                var styled = new Span();
                if (emphasis.DelimiterChar == '~') styled.TextDecorations = TextDecorations.Strikethrough;
                else if (emphasis.DelimiterCount >= 2) styled.FontWeight = FontWeights.Bold;
                else styled.FontStyle = FontStyles.Italic;
                AddInlines(styled.Inlines, emphasis); target.Add(styled); break;
            case LinkInline link:
                var label = new Span();
                if (link.IsImage) label.Inlines.Add(new Run("[图片："));
                AddInlines(label.Inlines, link);
                if (link.IsImage) label.Inlines.Add(new Run("]"));
                else { label.Foreground = Accent; label.TextDecorations = TextDecorations.Underline; }
                // Show the destination as selectable text; links never navigate.
                if (!string.IsNullOrEmpty(link.Url)) label.Inlines.Add(new Run($" ({link.Url})"));
                target.Add(label); break;
            case AutolinkInline link:
                target.Add(new Run(link.Url) { Foreground = Accent, TextDecorations = TextDecorations.Underline }); break;
            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString())); break;
            case DelimiterInline delimiter:
                target.Add(new Run(delimiter.ToLiteral()));
                AddInlines(target, delimiter); break;
            case ContainerInline child:
                AddInlines(target, child); break;
        }
    }

    private sealed class FontScale : IValueConverter
    {
        internal static readonly FontScale Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (double)value * (double)parameter;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
