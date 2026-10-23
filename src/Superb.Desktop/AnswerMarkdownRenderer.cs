using System.Globalization;
using System.Windows;
using System.Windows.Data;
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
    private Brush Accent => (Brush)owner.FindResource("Accent");

    internal Block Render(MdBlock source)
    {
        switch (source)
        {
            case HeadingBlock heading:
                var title = Paragraph(heading);
                title.FontWeight = FontWeights.Bold;
                title.Margin = new Thickness(0, 10, 0, 6);
                title.SetBinding(TextElement.FontSizeProperty, new Binding(nameof(owner.FontSize))
                {
                    Source = owner, Converter = FontScale.Instance,
                    ConverterParameter = heading.Level switch { 1 => 1.6, 2 => 1.35, 3 => 1.15, _ => 1.0 }
                });
                return title;
            case ParagraphBlock paragraph:
                return Paragraph(paragraph);
            case CodeBlock code:
                var codeParagraph = new Paragraph
                {
                    FontFamily = new FontFamily("Consolas"), Background = Surface,
                    Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 10)
                };
                foreach (var run in CodeHighlighting.CreateRuns(code.Lines.ToString(), (code as FencedCodeBlock)?.Info,
                    (SolidColorBrush)owner.FindResource("Ink"))) codeParagraph.Inlines.Add(run);
                return codeParagraph;
            case ListBlock list:
                var result = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Padding = new Thickness(28, 0, 0, 0), Margin = new Thickness(0, 2, 0, 8)
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0)
                    result.StartIndex = start;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var output = new ListItem();
                    foreach (var child in item) output.Blocks.Add(Render(child));
                    result.ListItems.Add(output);
                }
                return result;
            case QuoteBlock quote:
                var section = new Section
                {
                    BorderBrush = Accent, BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 4, 0, 10)
                };
                foreach (var child in quote) section.Blocks.Add(Render(child));
                return section;
            case MdTable table:
                return RenderTable(table);
            case ThematicBreakBlock:
                return new Paragraph
                {
                    BorderBrush = Accent, BorderThickness = new Thickness(0, 0, 0, 1),
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
        var result = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        if (source.Inline is { } inline) AddInlines(result.Inlines, inline);
        else result.Inlines.Add(new Run(source.Lines.ToString()));
        return result;
    }

    private Table RenderTable(MdTable source)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
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
                    BorderBrush = Accent, BorderThickness = new Thickness(0.5), Padding = new Thickness(8, 5, 8, 5),
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
                foreach (var block in cell) target.Blocks.Add(Render(block));
                output.Cells.Add(target);
                column = columnIndex + target.ColumnSpan;
            }
        }
        return table;
    }

    private void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = Surface }); break;
                case LineBreakInline:
                    // Keep the reader's existing line-by-line presentation for model output.
                    target.Add(new LineBreak()); break;
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
    }

    private sealed class FontScale : IValueConverter
    {
        internal static readonly FontScale Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (double)value * (double)parameter;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
