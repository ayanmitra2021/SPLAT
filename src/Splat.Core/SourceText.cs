namespace Splat.Core;

/// <summary>A named source buffer with fast offset-to-line/column mapping. Lines and columns are 1-based.</summary>
public sealed class SourceText
{
    private readonly List<int> _lineStarts = [0];

    public string File { get; }
    public string Text { get; }

    public SourceText(string file, string text)
    {
        File = file;
        Text = text;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') _lineStarts.Add(i + 1);
            else if (text[i] == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n')) _lineStarts.Add(i + 1);
        }
    }

    public int LineCount => _lineStarts.Count;

    /// <summary>Maps an offset (clamped to 0..Length) to a 1-based line and column.</summary>
    public (int Line, int Col) GetLineCol(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        int idx = _lineStarts.BinarySearch(offset);
        if (idx < 0) idx = ~idx - 1;
        return (idx + 1, offset - _lineStarts[idx] + 1);
    }

    /// <summary>Text of a 1-based line without its line terminator; empty when out of range.</summary>
    public string GetLine(int line)
    {
        if (line < 1 || line > _lineStarts.Count) return "";
        int start = _lineStarts[line - 1];
        int end = line < _lineStarts.Count ? _lineStarts[line] : Text.Length;
        while (end > start && (Text[end - 1] == '\n' || Text[end - 1] == '\r')) end--;
        return Text[start..end];
    }

    public int OffsetOf(int line, int col) =>
        line < 1 || line > _lineStarts.Count ? Text.Length : Math.Min(_lineStarts[line - 1] + col - 1, Text.Length);

    /// <summary>Smallest span covering both spans (same file).</summary>
    public SourceSpan Join(SourceSpan a, SourceSpan b)
    {
        if (a.IsNone) return b;
        if (b.IsNone) return a;
        int aStart = OffsetOf(a.Line, a.Col), bStart = OffsetOf(b.Line, b.Col);
        int start = Math.Min(aStart, bStart);
        int end = Math.Max(aStart + a.Length, bStart + b.Length);
        return Span(start, end - start);
    }

    public SourceSpan Span(int start, int length)
    {
        start = Math.Clamp(start, 0, Text.Length);
        length = Math.Clamp(length, 0, Text.Length - start);
        var (line, col) = GetLineCol(start);
        return new SourceSpan(File, line, col, length);
    }
}
