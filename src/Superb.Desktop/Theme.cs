using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Superb.Desktop;

internal readonly record struct Palette(Color Base, Color Surface, Color Line, Color Ink, Color Muted, Color Accent, Color OnAccent);

// Presets are the readable combinations. Custom colors only supply background, text, and accent;
// surface, line, and muted text are derived so a pair cannot become an unreadable mix.
internal static class Theme
{
    internal const string InkId = "ink";
    internal const string MistId = "mist";
    internal const string PaperId = "paper";
    internal const string PineId = "pine";
    internal static readonly string[] Ids = [InkId, MistId, PaperId, PineId];
    internal static readonly string[] Accents = ["#8AB4F8", "#7DCEA0", "#E0B15A", "#E08B8B", "#C6A4E8", "#8FD0D6", "#E6E8EB", "#5F6368"];
    internal static Color DarkInk { get; } = Color.FromRgb(0x13, 0x14, 0x16);
    internal static Color LightInk { get; } = Color.FromRgb(0xF7, 0xF8, 0xF9);

    private static readonly Dictionary<string, string[]> Presets = new()
    {
        [InkId] = ["#131416", "#1B1D21", "#2A2E33", "#E8EAED", "#9399A1", "#8AB4F8"],
        [MistId] = ["#24272C", "#2E3238", "#454B53", "#F2F3F5", "#B0B6BE", "#A9C7FA"],
        [PaperId] = ["#F4F5F6", "#FFFFFF", "#E1E3E6", "#1A1C1F", "#5E656E", "#1A56C4"],
        [PineId] = ["#101716", "#192320", "#33433D", "#E5EEE8", "#9DAFA6", "#B7E3BE"]
    };

    internal static bool TryResolve(string? theme, string? accent, string? customBase, string? customInk, out Palette palette, out string? error)
    {
        palette = default;
        if (string.IsNullOrWhiteSpace(theme) || !Presets.TryGetValue(theme, out var preset))
        {
            error = "未知的配色预设。"; return false;
        }
        var hasBase = !string.IsNullOrWhiteSpace(customBase);
        var hasInk = !string.IsNullOrWhiteSpace(customInk);
        if (hasBase != hasInk)
        {
            error = "自定义颜色需要同时填写背景和文字。"; return false;
        }
        if ((hasBase && !TryParse(customBase, out _)) || (hasInk && !TryParse(customInk, out _)) ||
            (!string.IsNullOrWhiteSpace(accent) && !TryParse(accent, out _)))
        {
            error = "颜色格式应为 #RRGGBB。"; return false;
        }
        var colors = preset.Select(Parse).ToArray();
        var baseColor = colors[0];
        var ink = colors[3];
        if (hasBase)
        {
            baseColor = Parse(customBase!); ink = Parse(customInk!);
            if (Contrast(baseColor, ink) < 4.5)
            {
                error = "文字与背景对比度不足，请拉开深浅。"; return false;
            }
        }
        var accentColor = string.IsNullOrWhiteSpace(accent) ? colors[5] : Parse(accent!);
        palette = hasBase
            ? new(baseColor, Mix(baseColor, ink, 0.08), Mix(baseColor, ink, 0.18), ink, Mix(ink, baseColor, 0.42), accentColor, OnColor(accentColor))
            : new(colors[0], colors[1], colors[2], colors[3], colors[4], accentColor, OnColor(accentColor));
        error = null; return true;
    }

    internal static string EffectiveAccent(string theme, string accent) =>
        string.IsNullOrWhiteSpace(accent) ? Presets[theme][5] : Canonical(accent);

    internal static void Apply(Palette palette)
    {
        var resources = Application.Current.Resources;
        Paint(resources, "Base", palette.Base);
        Paint(resources, "Surface", palette.Surface);
        Paint(resources, "Line", palette.Line);
        Paint(resources, "Ink", palette.Ink);
        Paint(resources, "Muted", palette.Muted);
        Paint(resources, "Accent", palette.Accent);
        Paint(resources, "OnAccent", palette.OnAccent);
    }

    internal static string Canonical(string text) => text.Trim().StartsWith('#')
        ? $"#{Parse(text).R:X2}{Parse(text).G:X2}{Parse(text).B:X2}"
        : Canonical("#" + text.Trim());

    internal static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    internal static double Contrast(Color left, Color right)
    {
        var lighter = Math.Max(Luminance(left), Luminance(right));
        var darker = Math.Min(Luminance(left), Luminance(right));
        return (lighter + 0.05) / (darker + 0.05);
    }

    internal static Color OnColor(Color background) =>
        Contrast(DarkInk, background) >= Contrast(LightInk, background) ? DarkInk : LightInk;

    private static void Paint(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is not SolidColorBrush brush)
            throw new InvalidOperationException("Theme brush is missing: " + key);
        // WPF may freeze a shared brush while sealing a consumer. Replace frozen
        // resources for DynamicResource controls; otherwise preserve references.
        if (brush.IsFrozen) resources[key] = new SolidColorBrush(color);
        else brush.Color = color;
    }

    private static Color Mix(Color from, Color toward, double amount)
    {
        byte Channel(byte start, byte end) => (byte)Math.Round(start + (end - start) * amount);
        return Color.FromRgb(Channel(from.R, toward.R), Channel(from.G, toward.G), Channel(from.B, toward.B));
    }

    private static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static Color Parse(string text) => TryParse(text, out var color) ? color : throw new ArgumentException("color");
}
