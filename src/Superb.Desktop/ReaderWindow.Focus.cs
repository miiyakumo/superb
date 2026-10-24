using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

public partial class ReaderWindow
{
    private Point? headerDragStart;

    private void InitializeFocusPolicy()
    {
        protection.AllowMouseActivation = () => false;
        PreviewGotKeyboardFocus += (_, e) => e.Handled = true;
        Answer.PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
        DragHeader.MouseMove += DragHeader_MouseMove;
        DragHeader.MouseLeftButtonUp += (_, e) =>
        {
            if (headerDragStart is null) return;
            headerDragStart = null; DragHeader.ReleaseMouseCapture(); e.Handled = true;
        };
        DragHeader.LostMouseCapture += (_, _) => headerDragStart = null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) => node is Visual
        ? VisualTreeHelper.GetParent(node) : node is ContentElement content
            ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
            : LogicalTreeHelper.GetParent(node);

    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != DragHeader; node = ParentOf(node))
            if (node is ButtonBase) return;
        // Native DragMove starts a system move loop that may activate the window.
        // WPF mouse capture lets us move the surface without changing foreground.
        if (!DragHeader.CaptureMouse()) return;
        headerDragStart = PointToScreen(e.GetPosition(this));
        e.Handled = true;
    }

    private void DragHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (headerDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var point = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        headerDragStart = point;
        Left += (point.X - start.X) / dpi.DpiScaleX;
        Top += (point.Y - start.Y) / dpi.DpiScaleY;
        e.Handled = true;
    }
}
