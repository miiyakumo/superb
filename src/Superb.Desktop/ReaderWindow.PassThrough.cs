using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

public partial class ReaderWindow
{
    private ReaderDisplayLayer? displayLayer;
    internal nint DisplayLayerHandle => displayLayer?.Handle ?? 0;

    private void InitializePassThrough()
    {
        displayLayer = new(this, WindowSurface);
        LayoutUpdated += (_, _) => RefreshInputRegions();
        LocationChanged += (_, _) => RefreshInputRegions();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RefreshInputRegions();
            else displayLayer.Hide();
        };
    }

    internal void RefreshInputRegions()
    {
        if (!IsVisible || displayLayer is null || handlingProtectionLoss) return;
        if (!displayLayer.Update(InputRegions())) Check();
    }

    internal IReadOnlyList<Rect> InputRegions()
    {
        var result = new List<Rect>();
        if (!ClickThrough) Visit(WindowSurface, new Rect(RenderSize));
        return result;

        void Visit(DependencyObject node, Rect clip)
        {
            if (node is UIElement { IsVisible: false } or UIElement { IsHitTestVisible: false }) return;
            if (node is FrameworkElement element)
            {
                var bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
                if (element.ClipToBounds || element is ScrollContentPresenter) clip.Intersect(bounds);
                if (element.Clip is { } geometry)
                    clip.Intersect(element.TransformToAncestor(this).TransformBounds(geometry.Bounds));
                if (clip.IsEmpty) return;
                if (element is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Slider or ScrollBar or Thumb ||
                    ReferenceEquals(element, DragHeader) || (Interactive && ReferenceEquals(element, Answer)))
                {
                    bounds.Intersect(clip);
                    if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0) result.Add(bounds);
                    return;
                }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i), clip);
        }
    }
}
