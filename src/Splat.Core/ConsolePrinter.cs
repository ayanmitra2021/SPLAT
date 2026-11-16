using System.Text;

namespace Splat.Core;

/// <summary>Renders diagnostics: headline, source line, caret underline, and a Fix line.</summary>
public sealed class ConsolePrinter
{
    private const string Reset = "\u001b[0m", Red = "\u001b[31;1m", Yellow = "\u001b[33;1m", Cyan = "\u001b[36m", Dim = "\u001b[2m";

    private readonly TextWriter _out;
    private readonly bool _color;

    public ConsolePrinter(TextWriter output, bool useColor) { _out = output; _color = useColor; }

    /// <summary>Printer for stderr: UTF-8 emoji, colour only when stderr is an interactive terminal.</summary>
    public static ConsolePrinter ForStdErr()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
        bool color = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        return new ConsolePrinter(Console.Error, color);
    }

    public void Print(DiagnosticEngine engine)
    {
        foreach (var d in engine.Diagnostics) Print(d, engine.GetSource(d.Span.File));
        if (engine.Truncated)
            _out.WriteLine(Paint($"... and more errors, but I stopped at {engine.MaxErrors}. Fix these first, champ.", Dim));
    }

    public void Print(Diagnostic d, SourceText? source)
    {
        _out.WriteLine(Paint($"[{d.Code}] {d.Message}", d.Severity == Severity.Error ? Red : Yellow));
        if (!d.Span.IsNone)
        {
            _out.WriteLine(Paint($"  --> {d.Span}", Cyan));
            if (source is not null) PrintSnippet(d.Span, source);
        }
        if (d.Fix is not null) _out.WriteLine($"  {Paint("Fix:", Cyan)} {d.Fix}");
        if (d.Trace is { Count: > 0 })
        {
            _out.WriteLine($"  {Paint("SPLAT stack trace (most recent call first):", Cyan)}");
            foreach (var frame in d.Trace) _out.WriteLine($"    at {frame}");
        }
        _out.WriteLine();
    }

    private void PrintSnippet(SourceSpan span, SourceText source)
    {
        string line = source.GetLine(span.Line);
        string gutter = span.Line.ToString();
        string pad = new(' ', gutter.Length);
        int startIdx = Math.Clamp(span.Col - 1, 0, line.Length);
        int available = line.Length - startIdx;

        // A span running past the end of its first line is underlined to the line end plus "...".
        bool multiLine = span.Length > available && available > 0;
        int len = multiLine ? available : Math.Max(1, span.Length);

        // Copy tabs from the source so the carets line up under any tab width.
        var prefix = new StringBuilder();
        for (int i = 0; i < startIdx; i++) prefix.Append(line[i] == '\t' ? '\t' : ' ');

        _out.WriteLine(Paint($" {pad} |", Cyan));
        _out.WriteLine($"{Paint($" {gutter} |", Cyan)} {line}");
        _out.WriteLine($"{Paint($" {pad} |", Cyan)} {prefix}{Paint(new string('^', len) + (multiLine ? "..." : ""), Red)}");
    }

    private string Paint(string text, string code) => _color ? code + text + Reset : text;
}
