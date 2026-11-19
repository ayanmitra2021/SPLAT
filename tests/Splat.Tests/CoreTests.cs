using Splat.Core;

namespace Splat.Tests;

public class SourceTextTests
{
    [Fact]
    public void OffsetToLineCol_IsOneBased()
    {
        var s = new SourceText("t.splat", "ab\ncd\n");
        Assert.Equal((1, 1), s.GetLineCol(0));
        Assert.Equal((1, 3), s.GetLineCol(2));
        Assert.Equal((2, 1), s.GetLineCol(3));
        Assert.Equal((2, 2), s.GetLineCol(4));
    }

    [Fact]
    public void EndOfFileOffset_MapsToLastPosition()
    {
        var s = new SourceText("t.splat", "x := 1");
        Assert.Equal((1, 7), s.GetLineCol(6));
        Assert.Equal((1, 7), s.GetLineCol(999)); // clamped
        Assert.Equal("x := 1", s.GetLine(1));
    }

    [Fact]
    public void CrLfAndLoneCr_AreSingleLineBreaks()
    {
        var s = new SourceText("t", "a\r\nb\rc");
        Assert.Equal(3, s.LineCount);
        Assert.Equal("a", s.GetLine(1));
        Assert.Equal("b", s.GetLine(2));
        Assert.Equal("c", s.GetLine(3));
        Assert.Equal("", s.GetLine(9));
    }

    [Fact]
    public void Join_CoversMultiLineRange()
    {
        var s = new SourceText("t", "abc\ndef\nghi");
        var a = s.Span(1, 1);   // 'b'
        var b = s.Span(9, 1);   // 'h'
        var j = s.Join(a, b);
        Assert.Equal(1, j.Line);
        Assert.Equal(2, j.Col);
        Assert.Equal(9, j.Length);
    }
}

public class ConsolePrinterTests
{
    private static string Render(SourceText src, Diagnostic d)
    {
        var sw = new StringWriter();
        new ConsolePrinter(sw, useColor: false).Print(d, src);
        return sw.ToString().Replace("\r\n", "\n");
    }

    private static string CaretLine(string rendered) =>
        rendered.Split('\n').First(l => l.Contains('^'));

    [Fact]
    public void Caret_SitsUnderSpan()
    {
        var src = new SourceText("t.splat", "var x = 1 +\n");
        var d = Templates.TypeMismatch(src.Span(4, 1), "txt", "i32");
        var caret = CaretLine(Render(src, d));
        // " 1 | var x = 1 +" -> caret under the 'x' at column 5, i.e. 4 spaces after the "| " gutter.
        Assert.EndsWith("|     ^", caret.TrimEnd());
    }

    [Fact]
    public void Caret_ColumnAlignsWithSourceLine()
    {
        var src = new SourceText("t.splat", "abc def ghi");
        var rendered = Render(src, Templates.UndefinedName(src.Span(4, 3), "def"));
        var lines = rendered.Split('\n');
        var code = lines.First(l => l.Contains("abc def ghi"));
        var caret = CaretLine(rendered);
        Assert.Equal(code.IndexOf("def", StringComparison.Ordinal), caret.IndexOf('^'));
        Assert.Equal(3, caret.Count(c => c == '^'));
    }

    [Fact]
    public void SpanAtEndOfFile_StillShowsOneCaret()
    {
        var src = new SourceText("t.splat", "loop i in 0..3:\n    print i");
        var span = src.Span(src.Text.Length, 0);
        var rendered = Render(src, Templates.MissingSemicolon(span, openLine: 1));
        Assert.Equal(1, CaretLine(rendered).Count(c => c == '^'));
        Assert.Contains("print i", rendered);
        Assert.Contains("Fix: Add ';' to close the block opened on line 1.", rendered);
    }

    [Fact]
    public void MultiLineSpan_UnderlinesFirstLineWithEllipsis()
    {
        var src = new SourceText("t.splat", "abc\ndef\nghi");
        var span = src.Span(1, 8);
        var caret = CaretLine(Render(src, Templates.UndefinedName(span, "x")));
        Assert.EndsWith("^^...", caret);
    }

    [Fact]
    public void Tabs_ArePreservedInCaretPadding()
    {
        var src = new SourceText("t.splat", "\t\tfoo");
        var rendered = Render(src, Templates.UndefinedName(src.Span(2, 3), "foo"));
        var code = rendered.Split('\n').First(l => l.Contains("foo") && l.Contains('\t'));
        var caret = CaretLine(rendered);
        // Same leading characters (tabs) before the content/caret, so they align at any tab width.
        Assert.Equal(code[code.IndexOf('\t')..code.IndexOf("foo", StringComparison.Ordinal)],
                     caret[caret.IndexOf('\t')..caret.IndexOf('^')]);
    }

    [Fact]
    public void NoColor_WhenWriterIsPlain()
    {
        var src = new SourceText("t", "x");
        Assert.DoesNotContain("\u001b[", Render(src, Templates.DivideByZero(src.Span(0, 1))), StringComparison.Ordinal);
    }

    [Fact]
    public void Color_EmitsAnsi()
    {
        var src = new SourceText("t", "x");
        var sw = new StringWriter();
        new ConsolePrinter(sw, useColor: true).Print(Templates.DivideByZero(src.Span(0, 1)), src);
        Assert.Contains("\u001b[", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDiagnosticWithoutSpan_PrintsNoSnippet()
    {
        var rendered = Render(new SourceText("t", ""), Templates.FileNotFound(SourceSpan.None, "nope.txt"));
        Assert.DoesNotContain("^", rendered);
        Assert.Contains("DETECTIVE FAIL [Runtime]", rendered);
    }
}

public class TemplateTests
{
    private static readonly SourceSpan At7 = new("f.splat", 7, 1, 1);

    [Fact]
    public void SpecMessages_AreVerbatim()
    {
        Assert.Equal(
            "💀 OOF [Line 7]: You opened a ':' and walked away like an unfinished text message. Expected ';' to close your thought, Shakespeare.",
            Templates.MissingSemicolon(At7, 3).Message);
        Assert.Equal(
            "📐 GEOMETRY CRIME [Line 7]: Tried to '@' a 'f32[3, 4]' with a 'f32[5, 2]'. 4 != 5. Even in parallel universes, these matrices cannot hug. Tip: Transpose with \"'\"?",
            Templates.ShapeMismatch(At7, "f32[3, 4]", "f32[5, 2]", "4", "5").Message);
        Assert.Equal(
            "🎭 IDENTITY CRISIS [Line 7]: Tried to shove a 'txt' into a 'i32' slot. I'm strictly typed, bestie.",
            Templates.TypeMismatch(At7, "txt", "i32").Message);
        Assert.Equal(
            "👻 GHOST HUNTING [Line 7]: Tried to access 'v' after it died/moved on Line 2. We already held its deinit. Fix: Borrow with '&v' instead.",
            Templates.UseAfterMove(At7, "v", 2).Message);
        Assert.Equal(
            "🚧 CONTAINMENT BREACH [Line 7]: Borrowed reference to 'r' tried to sneak out of arena 'z'. Values cannot outlive their arena.",
            Templates.ArenaEscape(At7, "r", "z").Message);
        Assert.Equal(
            "🌋 MATH MELTDOWN [Runtime @ epoch 12]: Loss became NaN (Not a Number, Not a Good Time). Tip: Lower your learning rate; your weights are orbiting Mars.",
            Templates.NanMeltdown(SourceSpan.None, 12).Message);
        Assert.Equal(
            "🕵️ DETECTIVE FAIL [Runtime]: Looked everywhere for \"a.csv\" and found pure emptiness. Fix: Check path or wrap in 'try: ... catch e: ... ;'.",
            Templates.FileNotFound(SourceSpan.None, "a.csv").Message);
    }

    [Fact]
    public void ShapeMismatch_CanOmitTranspose_Tip()
    {
        Assert.DoesNotContain("Tip:", Templates.ShapeMismatch(At7, "a", "b", "1", "2", suggestTranspose: false).Message);
    }

    [Fact]
    public void EveryTemplate_HasFixAndCategory()
    {
        var all = new[]
        {
            Templates.MissingSemicolon(At7, 1), Templates.ShapeMismatch(At7, "a", "b", "1", "2"),
            Templates.TypeMismatch(At7, "a", "b"), Templates.UseAfterMove(At7, "x", 1),
            Templates.ArenaEscape(At7, "x", "z"), Templates.NanMeltdown(At7, 1), Templates.FileNotFound(At7, "p"),
            Templates.UnexpectedToken(At7, "}", "a statement"), Templates.UnterminatedString(At7),
            Templates.UnterminatedComment(At7), Templates.BadCharacter(At7, '$'), Templates.BadLiteral(At7, "bad", "fix"),
            Templates.UndefinedName(At7, "q"),
            Templates.WrongArgCount(At7, "f", 1, 2), Templates.DivideByZero(At7), Templates.ImmutableAssign(At7, "x"),
            Templates.StackOverflow(At7, 1000), Templates.Internal(At7, "catch"),
        };
        Assert.All(all, d => { Assert.False(string.IsNullOrWhiteSpace(d.Fix)); Assert.NotEmpty(d.Message); });
        Assert.Equal(all.Length, all.Select(d => d.Category).Distinct().Count());
    }

    [Fact]
    public void QuipRotation_IsDeterministic()
    {
        var a = Templates.UnexpectedToken(At7, "x", "y").Message;
        var b = Templates.UnexpectedToken(At7, "x", "y").Message;
        Assert.Equal(a, b);
    }
}

public class DiagnosticEngineTests
{
    [Fact]
    public void CapsErrors_AndFlagsTruncation()
    {
        var e = new DiagnosticEngine(maxErrors: 2);
        var span = new SourceSpan("f", 1, 1, 1);
        for (int i = 0; i < 5; i++) e.Report(Templates.DivideByZero(span));
        Assert.Equal(2, e.ErrorCount);
        Assert.Equal(2, e.Diagnostics.Count);
        Assert.True(e.Truncated);
        Assert.True(e.HasErrors);
    }

    [Fact]
    public void Warnings_DoNotCountTowardCap()
    {
        var e = new DiagnosticEngine(maxErrors: 1);
        var w = Templates.DivideByZero(SourceSpan.None) with { Severity = Severity.Warning };
        for (int i = 0; i < 3; i++) e.Report(w);
        Assert.False(e.HasErrors);
        Assert.Equal(3, e.Diagnostics.Count);
    }

    [Fact]
    public void PrinterUsesRegisteredSources()
    {
        var e = new DiagnosticEngine();
        var src = new SourceText("a.splat", "catch here");
        e.AddSource(src);
        e.Report(Templates.UndefinedName(src.Span(5, 4), "here"));
        var sw = new StringWriter();
        new ConsolePrinter(sw, false).Print(e);
        Assert.Contains("catch here", sw.ToString());
    }
}
