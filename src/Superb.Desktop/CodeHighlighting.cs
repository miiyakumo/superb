using System.Windows.Documents;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Superb.Desktop;

// Only the lexer is used, never the editor control. The answer remains selectable
// native text, with no extra focus target, HTML, resource loading, or execution.
internal static class CodeHighlighting
{
    internal const int MaximumLength = 64_000;
    private const int MaximumRuns = 8_000;
    private static readonly SolidColorBrush Keyword = ColorBrush(0xC6A4E8);
    private static readonly SolidColorBrush String = ColorBrush(0xD6BA8B);
    private static readonly SolidColorBrush Comment = ColorBrush(0x8FA897);
    private static readonly SolidColorBrush Number = ColorBrush(0x9ED8AE);
    private static readonly SolidColorBrush Name = ColorBrush(0x86CFE0);

    internal static IReadOnlyList<Run> CreateRuns(string code, string? language, SolidColorBrush foreground)
    {
        var definitionName = language?.Trim().ToLowerInvariant() switch
        {
            "c" or "h" or "cpp" or "c++" or "cc" or "cxx" or "hpp" => "C++",
            "cs" or "csharp" or "c#" => "C#",
            "py" or "python" or "python3" => "Python",
            "js" or "javascript" or "jsx" or "ts" or "typescript" or "tsx" => "JavaScript",
            "java" => "Java",
            "json" or "jsonc" => "Json",
            "html" or "htm" => "HTML",
            "xml" or "xaml" or "svg" => "XML",
            "css" => "CSS",
            "sql" or "tsql" => "TSQL",
            "ps" or "ps1" or "powershell" or "pwsh" => "PowerShell",
            _ => null
        };
        if (code.Length == 0 || code.Length > MaximumLength || definitionName is null)
            return [new Run(code)];
        var definition = HighlightingManager.Instance.GetDefinition(definitionName);
        if (definition is null) return [new Run(code)];

        var document = new TextDocument(code);
        document.UndoStack.SizeLimit = 0;
        using var highlighter = new DocumentHighlighter(document, definition);
        var colors = new Dictionary<HighlightingColor, HighlightingColor>();
        var runs = new List<Run>();
        foreach (var line in document.Lines)
        {
            var highlighted = highlighter.HighlightLine(line.LineNumber);
            foreach (var section in highlighted.Sections)
            {
                if (!colors.TryGetValue(section.Color, out var themed))
                {
                    themed = new HighlightingColor
                    {
                        Foreground = new SimpleHighlightingBrush(Theme(section.Color.Name, foreground).Color)
                    };
                    colors.Add(section.Color, themed);
                }
                section.Color = themed;
            }
            // RichText flattens nested lexer spans without duplicating or losing text.
            runs.AddRange(highlighted.ToRichText().CreateRuns());
            if (line.DelimiterLength > 0) runs.Add(new Run(document.GetText(line.EndOffset, line.DelimiterLength)));
            // Bound WPF element allocation for long or punctuation-heavy responses.
            if (runs.Count > MaximumRuns) return [new Run(code)];
        }
        return runs;
    }

    private static SolidColorBrush Theme(string? colorName, SolidColorBrush foreground)
    {
        var name = colorName?.ToLowerInvariant() ?? "";
        if (name.Contains("comment") || name.Contains("preprocessor")) return Comment;
        if (name.Contains("string") || name.Contains("char") || name.Contains("regex") ||
            name.Contains("attributevalue") || name.Contains("cdata")) return String;
        if (name.Contains("number") || name.Contains("digit")) return Number;
        if (name.Contains("keyword") || name is "bool" or "null") return Keyword;
        if (name.Contains("type") || name.Contains("method") || name.Contains("function") ||
            name.Contains("command") || name.Contains("variable") || name.Contains("tag") ||
            name.Contains("attributename") || name.Contains("fieldname")) return Name;
        return foreground;
    }

    private static SolidColorBrush ColorBrush(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze(); return brush;
    }
}
