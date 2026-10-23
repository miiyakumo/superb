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
        protection.AllowMouseActivation = () => Native.GetCursorPos(out var point) &&
            PrepareMouseActivation(InputHitTest(PointFromScreen(new Point(point.X, point.Y))) as DependencyObject);
        PreviewGotKeyboardFocus += (_, e) =>
        {
            if (!IsKeyboardTarget(e.NewFocus as DependencyObject)) e.Handled = true;
        };
        // The answer has no context menu. WPF's text editor nevertheless focuses it
        // on right-down unless we handle that otherwise inert background interaction.
        Answer.PreviewMouseRightButtonDown += (_, e) => { if (!IsActive) e.Handled = true; };
        DragHeader.MouseMove += DragHeader_MouseMove;
        DragHeader.MouseLeftButtonUp += (_, e) =>
        {
            if (headerDragStart is null) return;
            headerDragStart = null; DragHeader.ReleaseMouseCapture(); e.Handled = true;
        };
        DragHeader.LostMouseCapture += (_, _) => headerDragStart = null;
    }

    internal bool PrepareMouseActivation(DependencyObject? target)
    {
        if (!Interactive)
        {
            for (var node = target; node is not null && node != this; node = ParentOf(node))
            {
                if (node is ScrollBar or Thumb or ButtonBase) return false;
                if (!ReferenceEquals(node, DraftText)) continue;
                if (!ready || busy || !SetMode(true)) return false;
                break;
            }
        }
        return IsKeyboardTarget(target);
    }

    internal bool IsKeyboardTarget(DependencyObject? target)
    {
        if (!Interactive) return false;
        for (var node = target; node is not null && node != this; node = ParentOf(node))
        {
            if (node is ScrollBar or Thumb or ButtonBase) return false;
            if (ReferenceEquals(node, DraftText)) return ready && !busy;
            if (ReferenceEquals(node, Answer)) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) => node is Visual
        ? VisualTreeHelper.GetParent(node) : node is ContentElement content
            ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
            : LogicalTreeHelper.GetParent(node);

    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBlock || e.LeftButton != MouseButtonState.Pressed) return;
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
