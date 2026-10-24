using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Superb.Desktop;

public partial class MainWindow
{
    private bool appearanceSettingsReady, applyingAppearance;
    private void InitializeAppearanceSettings()
    {
        reader.ApplyLayout(config.Panel);
        appearanceSettingsReady = true;
        SyncAppearanceControls();
    }
    private void SyncAppearanceControls()
    {
        if (!appearanceSettingsReady || applyingAppearance) return;
        applyingAppearance = true;
        try
        {
            var layout = reader.Layout();
            var area = reader.AppearanceWorkArea;
            PanelWidth.Maximum = Math.Max(reader.MinWidth, Math.Min(reader.MaxWidth, area.Width));
            PanelHeight.Maximum = Math.Max(reader.MinHeight, Math.Min(reader.MaxHeight, area.Height));
            PanelWidth.Value = layout.Width; PanelHeight.Value = layout.Height;
            PanelOpacity.Value = layout.BackgroundOpacity; PanelTextSize.Value = layout.FontSize;
            foreach (var button in new[] { ThemeInk, ThemeMist, ThemePaper, ThemePine })
            {
                var selected = layout.CustomBase.Length == 0 && layout.CustomInk.Length == 0 && layout.Theme == (string)button.Tag;
                button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                if (selected) button.Style = (Style)FindResource("Primary"); else button.ClearValue(StyleProperty);
            }
            foreach (var button in AccentSwatches.Children.OfType<Button>())
            {
                var selected = string.Equals(button.Tag as string, Theme.EffectiveAccent(layout.Theme, layout.Accent), StringComparison.OrdinalIgnoreCase);
                button.BorderBrush = (Brush)FindResource(selected ? "Ink" : "Line");
                button.BorderThickness = new Thickness(selected ? 2 : 1);
            }
            if (!CustomBaseBox.IsKeyboardFocusWithin && !CustomInkBox.IsKeyboardFocusWithin && !CustomAccentBox.IsKeyboardFocusWithin)
            {
                CustomBaseBox.Text = layout.CustomBase; CustomInkBox.Text = layout.CustomInk; CustomAccentBox.Text = layout.Accent;
            }
        }
        finally { applyingAppearance = false; }
    }
    private void ApplyAppearance(PanelLayout next)
    {
        if (!appearanceSettingsReady || applyingAppearance) return;
        (config with { Panel = next }).Validate();
        applyingAppearance = true;
        try { reader.ApplyLayout(next); config = config with { Panel = reader.Layout() }; }
        finally { applyingAppearance = false; }
        SyncAppearanceControls();
        if (!isolatedWorkspaceTest && !configLoadFailed && !exiting) { layoutSaveTimer.Stop(); layoutSaveTimer.Start(); }
    }
    private void Appearance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!appearanceSettingsReady || applyingAppearance) return;
        var next = reader.Layout();
        if (ReferenceEquals(sender, PanelWidth)) next = next with { Width = e.NewValue };
        else if (ReferenceEquals(sender, PanelHeight)) next = next with { Height = e.NewValue };
        else if (ReferenceEquals(sender, PanelOpacity)) next = next with { BackgroundOpacity = e.NewValue };
        else if (ReferenceEquals(sender, PanelTextSize)) next = next with { FontSize = Math.Round(e.NewValue) };
        ApplyAppearance(next);
    }
    private void Comfortable_Click(object sender, RoutedEventArgs e)
    {
        ApplyAppearance(reader.Layout() with { Width = 800, Height = 850 });
        reader.CollapseInput();
    }
    private void ResetAppearance_Click(object sender, RoutedEventArgs e) => ApplyAppearance(new PanelLayout(Left: reader.Left, Top: reader.Top, Visible: reader.IsVisible));
    private void ThemePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) ApplyAppearance(reader.Layout() with { Theme = id, Accent = "", CustomBase = "", CustomInk = "" });
    }
    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) ApplyAppearance(reader.Layout() with { Accent = hex });
    }
    private void ApplyCustom_Click(object sender, RoutedEventArgs e)
    {
        var layout = reader.Layout();
        var background = CustomBaseBox.Text.Trim(); var ink = CustomInkBox.Text.Trim(); var accent = CustomAccentBox.Text.Trim();
        if (!Theme.TryResolve(layout.Theme, accent, background, ink, out _, out var error)) { ThemeError.Text = error; return; }
        ThemeError.Text = "";
        ApplyAppearance(layout with { CustomBase = background.Length == 0 ? "" : Theme.Canonical(background),
            CustomInk = ink.Length == 0 ? "" : Theme.Canonical(ink), Accent = accent.Length == 0 ? "" : Theme.Canonical(accent) });
    }
}
