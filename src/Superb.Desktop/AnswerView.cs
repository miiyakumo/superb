using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Superb.Desktop;

// Shared by both protected windows. Streaming preserves completed paragraphs and scroll intent.
public sealed class AnswerView : UserControl
{
    internal FlowDocumentScrollViewer Reader { get; } = new()
    {
        IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Brushes.Transparent, ContextMenu = null, Focusable = false
    };
    private readonly Button latest = new() { Content = "回到最新", HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed, Focusable = false };
    private FlowDocument document = null!;
    private Paragraph? tail;
    private string text = "";
    private int committed, revision;
    private bool code, adjusting;
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
    internal void SetInteractive(bool interactive) => Reader.IsSelectionEnabled = interactive;
    internal void Clear()
    {
        revision++; text = ""; committed = 0; tail = null; code = false; SetFollow(true);
        document = new FlowDocument { PagePadding = new Thickness(0), Foreground = (Brush)FindResource("Ink") };
        document.SetBinding(TextElement.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        Reader.Document = document;
    }
    internal void Render(string value)
    {
        if (value == text) return;
        if (!value.StartsWith(text, StringComparison.Ordinal)) Clear();
        var offset = Scroll?.VerticalOffset ?? 0;
        if (tail is not null) { document.Blocks.Remove(tail); tail = null; }
        int end;
        while ((end = value.IndexOf('\n', committed)) >= 0)
        {
            var line = value[committed..end].TrimEnd('\r');
            if (line.StartsWith("```", StringComparison.Ordinal)) code = !code;
            else document.Blocks.Add(ParagraphFor(line));
            committed = end + 1;
        }
        var remaining = value[committed..];
        if (remaining.Length > 0 && !remaining.StartsWith("```", StringComparison.Ordinal))
        { tail = ParagraphFor(remaining); document.Blocks.Add(tail); }
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
    private Paragraph ParagraphFor(string line)
    {
        var paragraph = new Paragraph(new Run(line.Length == 0 ? " " : line)) { Margin = new Thickness(0, 0, 0, code ? 0 : 6) };
        if (code)
        {
            paragraph.FontFamily = new FontFamily("Consolas"); paragraph.Background = (Brush)FindResource("Surface");
            paragraph.Padding = new Thickness(8, 2, 8, 2);
        }
        return paragraph;
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
