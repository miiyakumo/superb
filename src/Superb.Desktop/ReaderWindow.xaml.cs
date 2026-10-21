using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

public partial class ReaderWindow : Window
{
    private readonly Protection protection;
    public event Action<string>? Command;
    public event Action? ProtectionLost;
    private bool allowClose;
    public ReaderWindow()
    {
        InitializeComponent();
        protection = new(this, true);
        SourceInitialized += (_, _) =>
        {
            var ok = protection.Install();
            ProtectionStatus.Text = ok ? "属性已设置 · 当前采集配置未实测" : "未验证 / 不支持 · 敏感显示已关闭";
            if (!ok) Clear();
        };
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; Hide(); } };
        LocationChanged += (_, _) => Check();
        SizeChanged += (_, _) => Check();
    }
    public bool Check()
    {
        if (protection.Handle == 0) return false;
        if (protection.Check()) return true;
        Clear();
        ProtectionStatus.Text = "未验证 / 不支持 · 敏感显示已关闭";
        ProtectionLost?.Invoke();
        return false;
    }
    public new void Show()
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (!protection.RefreshDisplayAffinity()) { Clear(); ProtectionLost?.Invoke(); return; }
        base.Show();
        Check();
    }
    public new void Hide()
    {
        base.Hide();
        if (protection.Handle != 0 && !protection.RefreshDisplayAffinity())
        { Clear(); ProtectionLost?.Invoke(); }
    }
    public void Clear()
    {
        Reader.Document = new FlowDocument();
        PreviewImage.Source = null;
        PreviewScroll.Visibility = Visibility.Collapsed;
        PreviewText.Text = "";
        Reader.Visibility = Visibility.Visible;
        StateText.Text = "已遮蔽 · 等待操作";
    }
    public void Status(string text) => StateText.Text = text;
    public void Preview(Payload payload)
    {
        if (!Check()) return;
        Clear();
        StateText.Text = "已准备 · 等待独立发送授权";
        if (payload.HasImage)
        {
            PreviewImage.Source = ImageProcessor.Decode(payload.Png);
            PreviewText.Text = payload.Text ?? "";
            Reader.Visibility = Visibility.Collapsed;
            PreviewScroll.Visibility = Visibility.Visible;
        }
        else Render(payload.Text ?? "", "已准备 · 等待独立发送授权");
    }
    public void Render(string text, string state)
    {
        if (!Check()) return;
        PreviewImage.Source = null;
        PreviewScroll.Visibility = Visibility.Collapsed;
        PreviewText.Text = "";
        Reader.Visibility = Visibility.Visible;
        StateText.Text = state;
        // Construct text-only WPF objects. Never interpret XAML, HTML, URLs or remote images.
        var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = FontSize, Foreground = (Brush)FindResource("Ink") };
        var code = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            var paragraph = new Paragraph(new Run(line.Length == 0 ? " " : line)) { Margin = new Thickness(0, 0, 0, code ? 0 : 5) };
            if (code) { paragraph.FontFamily = new FontFamily("Consolas"); paragraph.Background = (Brush)FindResource("Surface"); paragraph.Padding = new Thickness(8, 2, 8, 2); }
            document.Blocks.Add(paragraph);
        }
        Reader.Document = document;
    }
    public void Page(bool down)
    {
        if (PreviewScroll.Visibility == Visibility.Visible) { if (down) PreviewScroll.PageDown(); else PreviewScroll.PageUp(); return; }
        var scroll = FindScroll(Reader);
        if (down) scroll?.PageDown(); else scroll?.PageUp();
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } result) return result;
        return null;
    }
    internal PanelLayout Layout() => new(Left, Top, Width, Height, FontSize, IsVisible);
    internal void ApplyLayout(PanelLayout layout)
    {
        Left = Math.Clamp(layout.Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100));
        Top = Math.Clamp(layout.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100));
        Width = layout.Width; Height = layout.Height; FontSize = layout.FontSize;
    }
    public void Finish() { allowClose = true; Close(); }
    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is TextBlock && e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => Command?.Invoke("toggle");
    private void Send_Click(object sender, RoutedEventArgs e) => Command?.Invoke("send");
    private void Copy_Click(object sender, RoutedEventArgs e) => Command?.Invoke("copy");
    private void Pause_Click(object sender, RoutedEventArgs e) => Command?.Invoke("pause");
    private void Settings_Click(object sender, RoutedEventArgs e) => Command?.Invoke("settings");
}
