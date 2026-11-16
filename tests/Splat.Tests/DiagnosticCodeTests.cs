using System.Text.RegularExpressions;
using Splat.Core;

namespace Splat.Tests;

public class DiagnosticCodeTests
{
    private static readonly SourceSpan S = new("t.splat", 1, 1, 1);

    /// <summary>One instance of every template. Add new templates here when you add them to <c>Templates</c>.</summary>
    private static IEnumerable<Diagnostic> AllTemplates() =>
    [
        Templates.MissingSemicolon(S, 1), Templates.ShapeMismatch(S, "a", "b", "1", "2"), Templates.TypeMismatch(S, "a", "b"),
        Templates.UseAfterMove(S, "x", 1), Templates.ZoneEscape(S, "x", "z"), Templates.NanMeltdown(S, 1),
        Templates.FileNotFound(S, "p"), Templates.UnexpectedToken(S, "a", "b"), Templates.UnterminatedString(S),
        Templates.UnterminatedComment(S), Templates.BadCharacter(S, 'x'), Templates.BadLiteral(S, "p", "f"),
        Templates.UndefinedName(S, "x"), Templates.WrongArgCount(S, "f", 1, 2), Templates.DivideByZero(S),
        Templates.ImmutableAssign(S, "x"), Templates.StackOverflow(S, 1), Templates.StraySemicolon(S),
        Templates.OrphanBranch(S, "meh"), Templates.NotAssignable(S), Templates.NotYetBuilt(S, "mold", 8),
        Templates.TooDeep(S), Templates.Internal(S, "d"),
        Templates.NotCallable(S, "i32"), Templates.BadOperator(S, "+", "a", "b", "f"), Templates.Redeclared(S, "x", 1),
        Templates.LoopControlOutside(S, "bail"), Templates.YeetOutside(S), Templates.MissingReturn(S, "f", "i32"),
        Templates.BadFormat(S, ".3", "p"), Templates.CannotInfer(S, "w", "f"), Templates.IntegerOverflow(S, "i32", "+"),
        Templates.Panic(S, "m", null), Templates.BadConversion(S, "v", "i8"), Templates.MathDomain(S, "p"),
    ];

    [Fact]
    public void EveryCategoryHasARegisteredCode()
    {
        foreach (var c in Enum.GetValues<DiagnosticCategory>())
            Assert.True(DiagnosticCodes.IsRegistered(c), $"{c} has no entry in DiagnosticCodes.Table");
    }

    [Fact]
    public void CodesAreUniqueWithinEachSeverityAndWellFormed()
    {
        var seen = new HashSet<string>();
        foreach (var c in Enum.GetValues<DiagnosticCategory>())
        {
            var code = DiagnosticCodes.For(c);
            Assert.Matches(new Regex("^S[EW][0-9]{5}$"), code);
            Assert.True(seen.Add(code), $"duplicate code {code}");
        }
    }

    [Fact]
    public void ErrorNumbersAndWarningNumbersFormSeparateSequences()
    {
        Assert.Equal("SE00001", DiagnosticCodes.Format(Severity.Error, 1));
        Assert.Equal("SW00001", DiagnosticCodes.Format(Severity.Warning, 1));
        Assert.Equal("SE00001", DiagnosticCodes.For(DiagnosticCategory.MissingSemicolon));
    }

    [Fact]
    public void EveryTemplateSeverityMatchesItsCodePrefix()
    {
        foreach (var d in AllTemplates())
        {
            var expectedPrefix = d.Severity == Severity.Warning ? "SW" : "SE";
            Assert.StartsWith(expectedPrefix, d.Code);
        }
    }

    [Fact]
    public void EveryCategoryHasATemplate() =>
        Assert.Equal(Enum.GetValues<DiagnosticCategory>().Length, AllTemplates().Select(d => d.Category).Distinct().Count());

    [Fact]
    public void PrinterShowsTheCodeBeforeTheHeadline()
    {
        var w = new StringWriter { NewLine = "\n" };
        new ConsolePrinter(w, useColor: false).Print(Templates.MissingSemicolon(S, 1), null);
        Assert.StartsWith("[SE00001] 💀 OOF [Line 1]:", w.ToString());
    }
}
