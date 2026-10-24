using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Superb.Desktop;

// Parse before Markdown escapes/emphasis, so TeX remains intact. Code spans and
// fenced/indented code blocks still belong to Markdig's code parsers.
internal sealed class AnswerMathInline : LeafInline
{
    internal required string Formula { get; init; }
    internal required string Source { get; init; }
    internal bool Display { get; init; }
}

internal sealed class AnswerMathInlineParser : InlineParser
{
    internal AnswerMathInlineParser() => OpeningCharacters = ['$', '\\'];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var start = slice.Start;
        var text = slice.Text;
        var dollar = slice.CurrentChar == '$';
        if (dollar && slice.PeekCharExtra(-1) == '$') return false;
        var display = dollar ? slice.PeekChar() == '$' : slice.PeekChar() == '[';
        if (!dollar && slice.PeekChar() is not ('(' or '[')) return false;
        var length = dollar && !display ? 1 : 2;
        var endToken = dollar ? new string('$', length) : display ? "\\]" : "\\)";
        var contentStart = start + length;
        if (dollar && (contentStart > slice.End || (!display && char.IsWhiteSpace(text[contentStart])))) return false;
        var unmatchedEnd = slice.End + 1;
        for (var end = contentStart; end <= slice.End; end++)
        {
            if (!display && text[end] is '\r' or '\n') { unmatchedEnd = end; break; }
            if (text[end] == '\\' && !text.AsSpan(end).StartsWith(endToken, StringComparison.Ordinal))
            {
                end++; // escaped dollar/backslash is part of the formula
                continue;
            }
            if (end + length - 1 > slice.End || !text.AsSpan(end).StartsWith(endToken, StringComparison.Ordinal)) continue;
            if (dollar && !display && end < slice.End && text[end + 1] == '$') return false;
            if (dollar && (end == contentStart || (!display && char.IsWhiteSpace(text[end - 1])))) return false;
            // Do not interpret ordinary prices such as "$5 and $10" as math.
            if (dollar && !display && end < slice.End && char.IsDigit(text[end + 1])) return false;
            processor.Inline = new AnswerMathInline
            {
                Formula = text[contentStart..end], Source = text[start..(end + length)], Display = display,
                Span = new SourceSpan(processor.GetSourcePosition(start, out var line, out var column),
                    processor.GetSourcePosition(end + length - 1)), Line = line, Column = column
            };
            slice.Start = end + length;
            return true;
        }
        if (dollar) return false;
        // Markdig's escape parser would otherwise eat the opening backslash
        // while a streamed \(...\) expression is still incomplete.
        processor.Inline = new LiteralInline(text[start..unmatchedEnd])
        {
            Span = new SourceSpan(processor.GetSourcePosition(start), processor.GetSourcePosition(unmatchedEnd - 1))
        };
        slice.Start = unmatchedEnd;
        return true;
    }
}

internal sealed class AnswerMathBlock(BlockParser parser, string opening, string closing) : LeafBlock(parser)
{
    internal string Opening { get; } = opening;
    internal string Closing { get; } = closing;
    internal bool Complete { get; set; }
}

// Delimiters on their own line also allow blank lines inside an equation.
internal sealed class AnswerMathBlockParser : BlockParser
{
    internal AnswerMathBlockParser() => OpeningCharacters = ['$', '\\'];

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent) return BlockState.None;
        var opening = processor.Line.ToString().Trim();
        if (opening is not ("$$" or "\\[")) return BlockState.None;
        processor.NewBlocks.Push(new AnswerMathBlock(this, opening, opening == "$$" ? "$$" : "\\]")
        {
            Span = new SourceSpan(processor.Start, processor.Line.End), Column = processor.Column
        });
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var math = (AnswerMathBlock)block;
        math.Span.End = processor.Line.End;
        if (processor.Line.ToString().Trim() != math.Closing) return BlockState.Continue;
        math.Complete = true;
        return BlockState.BreakDiscard;
    }
}
