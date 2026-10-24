using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = System.Windows.Documents.Block;

namespace Superb.Desktop;

// Markdown stays in native WPF elements; streaming retains unchanged blocks and scroll intent.
public sealed class AnswerView : UserControl
{
    internal FlowDocumentScrollViewer Reader { get; } = new()
    {
        IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Brushes.Transparent, ContextMenu = null, Focusable = false, IsSelectionEnabled = false
    };
    private readonly Button latest = new() { Content = "回到最新", HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed, Focusable = false };
    private FlowDocument document = null!;
    private string text = "";
    private readonly List<(string Key, Block Block)> rendered = [];
    private int revision;
    private bool adjusting;
    private double? restyleOffset;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml().UsePipeTables().UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough).Build();
    internal bool Following { get; private set; } = true;
    internal string Text => text;
    public AnswerView()
    {
        var root = new DockPanel();
        DockPanel.SetDock(latest, Dock.Bottom); root.Children.Add(latest); root.Children.Add(Reader); Content = root;
        latest.Click += (_, _) => Follow();
        Reader.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (adjusting || e.VerticalChange == 0 || e.ExtentHeightChange != 0) return;
            if (Scroll is { } scroll) SetFollow(scroll.VerticalOffset >= scroll.ScrollableHeight - 2);
        }));
        Clear();
    }
    internal ScrollViewer? Scroll => FindScroll(Reader);
    internal void Clear()
    {
        revision++; text = ""; rendered.Clear(); SetFollow(true);
        document = new FlowDocument { PagePadding = new Thickness(0) };
        // Theme restoration can replace a frozen brush before the first answer exists.
        // Follow the resource rather than retaining the previous theme's brush instance.
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        document.SetBinding(TextElement.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        document.SetBinding(TextElement.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        Reader.Document = document;
    }
    internal void Restyle()
    {
        var value = text;
        var follow = Following;
        restyleOffset = Scroll?.VerticalOffset ?? 0;
        Clear();
        SetFollow(follow);
        if (value.Length > 0) Render(value);
        else restyleOffset = null;
    }
    internal void Render(string value)
    {
        var restore = restyleOffset;
        restyleOffset = null;
        if (value == text) return;
        if (!value.StartsWith(text, StringComparison.Ordinal)) Clear();
        var offset = restore ?? Scroll?.VerticalOffset ?? 0;
        // Reparse the open tail as well: streamed delimiters can turn a paragraph into
        // a heading/table, or complete emphasis and fences. Reference links may also
        // acquire their destination later in the stream, so include it in the key.
        var parsed = Markdown.Parse(value, Pipeline);
        var next = parsed.Where(b => b is not LinkReferenceDefinitionGroup).Select(b =>
            (Node: b, Key: b.GetType().Name + ":" + Source(value, b.Span) +
                string.Concat((b is LeafBlock { Inline: { } inline } ? inline.Descendants<LinkInline>() : b.Descendants<LinkInline>())
                    .Select(l => $"\0{l.Url}\0{l.Title}")))).ToArray();
        var keep = 0;
        while (keep < rendered.Count && keep < next.Length && rendered[keep].Key == next[keep].Key) keep++;
        for (var i = rendered.Count - 1; i >= keep; i--)
        {
            document.Blocks.Remove(rendered[i].Block); rendered.RemoveAt(i);
        }
        var renderer = new AnswerMarkdownRenderer(this);
        for (var i = keep; i < next.Length; i++)
        {
            var block = renderer.Render(next[i].Node);
            document.Blocks.Add(block); rendered.Add((next[i].Key, block));
        }
        text = value;
        var version = ++revision;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (version != revision || Scroll is not { } scroll) return;
            adjusting = true;
            try { if (Following) scroll.ScrollToEnd(); else scroll.ScrollToVerticalOffset(offset); }
            finally { adjusting = false; }
        });
    }
    private static string Source(string value, SourceSpan span)
    {
        // Open fences can have a synthetic end past EOF while streaming.
        var start = Math.Clamp(span.Start, 0, value.Length);
        var end = Math.Clamp(span.End + 1, start, value.Length);
        return value[start..end];
    }
    private void SetFollow(bool value) { Following = value; latest.Visibility = value ? Visibility.Collapsed : Visibility.Visible; }
    internal void Follow() { SetFollow(true); Scroll?.ScrollToEnd(); }
    internal void Page(bool down)
    {
        if (!down) SetFollow(false);
        if (down) Scroll?.PageDown(); else Scroll?.PageUp();
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}
