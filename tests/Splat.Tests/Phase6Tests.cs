using Splat.Cli;
using Splat.Core;
using Splat.Runtime;
using Splat.Syntax;

namespace Splat.Tests;

/// <summary>Golden-file comparison shared by the Phase 6 tests. <c>SPLAT_UPDATE_GOLDEN=1</c> rewrites expectations in the source tree.</summary>
internal static class Golden
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "golden");
    public static string M1Dir => Path.Combine(AppContext.BaseDirectory, "m1");

    public static string Read(string dir, string name) => File.ReadAllText(Path.Combine(dir, name)).Replace("\r\n", "\n");

    public static void Compare(string dir, string name, string actual)
    {
        if (Environment.GetEnvironmentVariable("SPLAT_UPDATE_GOLDEN") == "1")
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tests", "golden"))) root = root.Parent;
            var target = dir == M1Dir ? Path.Combine(root!.FullName, "tests", name) : Path.Combine(root!.FullName, "tests", "golden", name);
            File.WriteAllText(target, actual);
            return;
        }
        Assert.Equal(Read(dir, name), actual);
    }
}

public class IterationParserTests
{
    [Theory]
    [InlineData("0..10 by 2", "(by (.. 0 10) 2)")]
    [InlineData("0..=10 by n + 1", "(by (..= 0 10) (+ n 1))")]
    [InlineData("a..b by 2 then c..d", "(then (by (.. a b) 2) (.. c d))")]
    [InlineData("0..10 by 2 then 50..100 by 10", "(then (by (.. 0 10) 2) (by (.. 50 100) 10))")]
    [InlineData("0..10 then 50..100", "(then (.. 0 10) (.. 50 100))")]
    [InlineData("a then b then c", "(then a b c)")]
    [InlineData("\"abc\" then \"xyz\"", "(then \"abc\" \"xyz\")")]
    [InlineData("rev(0..10 by 2)", "(call rev (by (.. 0 10) 2))")]
    [InlineData("x in 0..10", "(in x (.. 0 10))")]
    [InlineData("x in 0..10 then 50..100", "(in x (then (.. 0 10) (.. 50 100)))")]
    [InlineData("x in 0..10 by 2", "(in x (by (.. 0 10) 2))")]
    [InlineData("c in (c\"a\", c\"e\", c\"i\")", "(in c (members c\"a\" c\"e\" c\"i\"))")]
    [InlineData("c in (c\"a\", c\"e\",)", "(in c (members c\"a\" c\"e\"))")]
    [InlineData("x in (a)", "(in x a)")]
    [InlineData("x in (0..5) then 9..12", "(in x (then (.. 0 5) (.. 9 12)))")]
    [InlineData("1 + 2 in r", "(in (+ 1 2) r)")]
    [InlineData("a and b in c", "(and a (in b c))")]
    [InlineData("a in b == c", "(== (in a b) c)")]
    [InlineData("!(x in S)", "(! (in x S))")]
    [InlineData("a in b or c in d", "(or (in a b) (in c d))")]
    [InlineData("x in a..b |> f", "(|> (in x (.. a b)) f)")]
    public void PrecedenceOfByThenIn(string source, string expected) => Assert.Equal(expected, ParseHelper.Expr(source));

    [Fact]
    public void ByAndThenAreOrdinaryNamesEverywhereElse()
    {
        Assert.Equal("(+ by then)", ParseHelper.Expr("by + then"));
        Assert.Equal(
            ["(bind by 3)", "(bind then (+ by 1))", "(call squish then by)"],
            ParseHelper.Statements("by := 3\nthen := by + 1\nsquish then, by"));
    }

    [Fact]
    public void ByAndThenAtTheStartOfALineAreNeverOperators()
    {
        // `x` ends the first statement; `by`/`then` on the next line start new ones.
        Assert.Equal(["(bind x 5)", "(bind by 2)", "(bind then 3)"], ParseHelper.Statements("x := 5\nby := 2\nthen := 3"));
        Assert.Equal(["(bind r (.. 0 5))", "(call squish by)"], ParseHelper.Statements("r := 0..5\nsquish by"));
    }

    [Fact]
    public void SpinHeaderTakesTheWholeChain()
    {
        Assert.Equal("(spin-in x (then (by (.. 0 10) 2) (.. 50 60)) (block))",
            ParseHelper.Single("spin x in 0..10 by 2 then 50..60:\n;"));
    }

    [Theory]
    [InlineData("x := y by 2")]            // only a range can be stepped
    [InlineData("x := 0..10 by 2 by 3")]   // steps do not stack
    [InlineData("x := (1, 2) in r")]       // a comma list is only legal AFTER in
    public void MalformedIterationForms(string source)
    {
        var (_, diag, _) = ParseHelper.Parse(source);
        Assert.True(diag.HasErrors);
        Assert.All(diag.Diagnostics, d => Assert.Equal(DiagnosticCategory.UnexpectedToken, d.Category));
    }

    [Theory]
    [InlineData("x := 0..10 by 2 then 50..100 by (3 + 1)")]
    [InlineData("x := (a or b) in (c then d)")]
    [InlineData("x := (a == b) in r then s")]
    [InlineData("hmm c in (c\"a\", c\"b\"):\n    bail\n;")]
    [InlineData("spin i in rev(0..=9 by 3) then 5..6:\n    skip\n;")]
    [InlineData("b := !(x in 0..3 then 7..9) and y in (1, 2)")]
    public void FormatterRoundTrips(string text)
    {
        var (p1, d1, _) = ParseHelper.Parse(text);
        Assert.False(d1.HasErrors, ParseHelper.Messages(d1));
        var formatted = SourceFormatter.Format(p1);
        var (p2, d2, _) = ParseHelper.Parse(formatted);
        Assert.False(d2.HasErrors, formatted + ParseHelper.Messages(d2));
        Assert.Equal(AstPrinter.ProgramText(p1), AstPrinter.ProgramText(p2));
        Assert.Equal(formatted, SourceFormatter.Format(p2));
    }
}

public class IterationRunTests
{
    [Theory]
    [InlineData("spin i in 0..10 by 3:\n    squish i\n;", "0\n3\n6\n9\n")]
    [InlineData("spin i in 0..=10 by 5:\n    squish i\n;", "0\n5\n10\n")]
    [InlineData("spin i in 0..4 by 10:\n    squish i\n;", "0\n")]
    [InlineData("spin i in rev(0..4):\n    squish i\n;", "3\n2\n1\n0\n")]
    [InlineData("spin i in rev(1..=4):\n    squish i\n;", "4\n3\n2\n1\n")]
    [InlineData("spin i in rev(0..10 by 4):\n    squish i\n;", "8\n4\n0\n")]
    [InlineData("spin i in rev(rev(0..3)):\n    squish i\n;", "0\n1\n2\n")]
    [InlineData("spin i in 5..5:\n    squish i\n;", "")]
    [InlineData("spin i in 5..=5:\n    squish i\n;", "5\n")]
    [InlineData("spin i in 9..3:\n    squish i\n;", "")]
    [InlineData("spin i in 0..3 then 10..12:\n    squish i\n;", "0\n1\n2\n10\n11\n")]
    [InlineData("spin c in \"ab\" then \"cd\":\n    squish c\n;", "a\nb\nc\nd\n")]
    [InlineData("spin i in 0..2 then rev(5..7) then 20..=20:\n    squish i\n;", "0\n1\n6\n5\n20\n")]
    [InlineData("spin i in 0..6 by 2 then 100..106 by 3:\n    squish i\n;", "0\n2\n4\n100\n103\n")]
    [InlineData("spin i in 0..10 by 2:\n    hmm i == 6:\n        bail\n    ;\n    squish i\n;", "0\n2\n4\n")]
    [InlineData("spin i in 0..5 then 8..10:\n    hmm i == 1:\n        skip\n    ;\n    squish i\n;", "0\n2\n3\n4\n8\n9\n")]
    public void Iteration(string code, string expected) => Assert.Equal(expected, RunHelper.Ok(code));

    [Fact]
    public void ReturnFromInsideAChain()
    {
        const string code = "cook find(target::i32)::i32:\n    spin i in 0..10 then 50..60:\n        hmm i == target:\n            yeet i * 2\n        ;\n    ;\n    -1\n;\nsquish find(52), find(7), find(99)";
        Assert.Equal("104 14 -1\n", RunHelper.Ok(code));
    }

    [Fact]
    public void RangeElementTypeFollowsItsEndpoints()
    {
        const string code = "big::i64 = 3_000_000_000\nwag n::i64 = 0\nspin i in big..big + 10 by 4:\n    n += i - big\n;\nsquish n";
        Assert.Equal("12\n", RunHelper.Ok(code)); // 0 + 4 + 8
    }

    [Fact]
    public void HugeRangesAreLazy()
    {
        // A range over the whole u64 space is never materialised; bail after three items.
        const string code = "wag n = 0\nspin i in u64(0)..u64(18446744073709551615):\n    n += 1\n    hmm n == 3:\n        bail\n    ;\n;\nsquish n";
        Assert.Equal("3\n", RunHelper.Ok(code));
    }

    [Theory]
    [InlineData("4 in 0..10 by 2", "yep")]
    [InlineData("5 in 0..10 by 2", "nop")]
    [InlineData("10 in 0..10", "nop")]
    [InlineData("10 in 0..=10", "yep")]
    [InlineData("-1 in 0..10", "nop")]
    [InlineData("10 in 0..=10 by 5", "yep")]
    [InlineData("11 in 0..=10 by 5", "nop")]
    [InlineData("5 in rev(0..10 by 5)", "yep")]
    [InlineData("3 in 5..5", "nop")]
    [InlineData("51 in 0..3 then 50..53", "yep")]
    [InlineData("7 in 0..3 then 50..53", "nop")]
    [InlineData("c\"b\" in \"abc\"", "yep")]
    [InlineData("c\"z\" in \"abc\"", "nop")]
    [InlineData("c\"é\" in \"café\"", "yep")]
    [InlineData("\"bc\" in \"abcd\"", "yep")]
    [InlineData("\"ca\" in \"abcd\"", "nop")]
    [InlineData("\"\" in \"abcd\"", "yep")]
    [InlineData("c\"y\" in \"abc\" then \"xyz\"", "yep")]
    [InlineData("3 in (1, 2, 3)", "yep")]
    [InlineData("4 in (1, 2, 3)", "nop")]
    [InlineData("\"b\" in (\"a\", \"b\")", "yep")]
    [InlineData("2.5 in (1.5, 2.5)", "yep")]
    [InlineData("yep in (nop,)", "nop")]
    [InlineData("!(5 in (1, 2, 3))", "yep")]
    [InlineData("3 in (1, 2, 3) and !(9 in 0..5)", "yep")]
    public void Membership(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Fact]
    public void MemberListStopsAtTheFirstMatch() =>
        // The second member would divide by zero if it were evaluated.
        Assert.Equal("yep", RunHelper.Eval("1 in (1, 1 / 0)"));

    [Fact]
    public void MembershipTypesFollowTheLeftOperand()
    {
        const string code = "x::i64 = 5\nsquish x in 0..10, x in (4, 5), x in 0..3 then 5..6";
        Assert.Equal("yep yep yep\n", RunHelper.Ok(code));
    }

    [Fact]
    public void RangesDisplayTheirStepAndDirection() =>
        Assert.Equal("0..10 by 2 rev(1..=4) 0..3 then 5..6", RunHelper.Eval("0..10 by 2, rev(1..=4), 0..3 then 5..6"));

    // ---- diagnostics ----

    [Fact]
    public void LiteralNonPositiveStepsAreCheckErrorsWithTheirOwnCode()
    {
        var r = RunHelper.Run("spin i in 0..10 by 0:\n    squish i\n;\nspin j in 0..10 by -3:\n    squish j\n;");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal("", r.Out);
        Assert.Equal([DiagnosticCategory.BadStep, DiagnosticCategory.BadStep], r.Categories);
        Assert.All(r.Diag.Diagnostics, d => Assert.Equal("SE00113", d.Code));
    }

    [Fact]
    public void RuntimeNonPositiveStepIsTheSameDiagnostic()
    {
        var r = RunHelper.RuntimeError("cook go(s::i32):\n    spin i in 0..3 by s:\n        squish i\n    ;\n;\ngo(1)\ngo(-1)");
        Assert.Equal("0\n1\n2\n", r.Out);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal("SE00113", d.Code);
        Assert.Contains("-1", d.Message);
        Assert.NotNull(d.Trace);
    }

    [Fact]
    public void ChainPartsMustShareOneElementType()
    {
        var cats = RunHelper.CompileErrors("spin x in 0..3 then \"abc\":\n    squish x\n;");
        Assert.Equal([DiagnosticCategory.ChainMismatch], cats);
        Assert.Equal([DiagnosticCategory.ChainMismatch],
            RunHelper.CompileErrors("a::i64 = 1\nspin x in 0..3 then a..a + 2:\n    squish x\n;").Take(1));
    }

    [Fact]
    public void ChainPartsMustBeIterable() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("spin x in 0..3 then 5:\n    squish x\n;"));

    [Fact]
    public void NonIterableSubjectUsesTheNewWording()
    {
        var r = RunHelper.Run("spin x in 42:\n    squish x\n;");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.TypeMismatch, d.Category);
        Assert.Contains("something iterable (a range, a txt, ...)", d.Message);
        Assert.DoesNotContain("a range such as", d.Message);
    }

    [Theory]
    [InlineData("b := 5 in 5", DiagnosticCategory.TypeMismatch)]                  // right side has nothing to look inside
    [InlineData("b := \"a\" in 0..3", DiagnosticCategory.TypeMismatch)]           // txt left, integer elements
    [InlineData("x::i64 = 1\nb := x in 0..3 then 5..6\ny::i32 = 2\nc := y in 0..3 by x", DiagnosticCategory.TypeMismatch)]
    [InlineData("b := 1 in \"abc\"", DiagnosticCategory.TypeMismatch)]            // an int is not a char
    [InlineData("b := 1 in (1, \"a\")", DiagnosticCategory.BadOperator)]          // member types must equal the left type
    [InlineData("b := c\"a\" in (c\"a\", 1)", DiagnosticCategory.BadOperator)]
    [InlineData("b := rev(5)", DiagnosticCategory.TypeMismatch)]
    [InlineData("b := rev(\"abc\")", DiagnosticCategory.TypeMismatch)]
    public void MembershipAndRevTypeErrors(string code, DiagnosticCategory first) =>
        Assert.Equal(first, RunHelper.CompileErrors(code)[0]);

    [Fact]
    public void ForcedConversionIsRequiredBetweenElementTypes()
    {
        // `x in 0..3` with x::i64: the literal range adopts i64, so no error and no conversion is needed.
        Assert.Equal("yep", RunHelper.Ok("x::i64 = 2\nsquish x in 0..3").Trim());
        // ... but two typed (non-literal) sides must agree exactly.
        Assert.NotEmpty(RunHelper.CompileErrors("x::i64 = 2\nn::i32 = 3\nsquish x in 0..n"));
    }

    [Fact]
    public void MembershipListOutsideInIsRejected() =>
        Assert.NotEmpty(ParseHelper.Parse("x := (1, 2)").Diag.Diagnostics);

    [Fact]
    public void SteppedRangesCannotStartFromNonRanges() =>
        Assert.Contains(DiagnosticCategory.UnexpectedToken, ParseHelper.Parse("x := 5 by 2").Diag.Diagnostics.Select(d => d.Category));
}

public class PreludeTests
{
    [Theory]
    [InlineData("len(\"héllo\")", "5")]
    [InlineData("len(\"\")", "0")]
    [InlineData("len(\"a😀b\")", "3")]       // counts Unicode scalars, like iteration does
    [InlineData("upper(\"mixed Case\")", "MIXED CASE")]
    [InlineData("lower(\"MIXED Case\")", "mixed case")]
    [InlineData("trim(\"  padded \\t\")", "padded")]
    [InlineData("parse_i32(\"42\") + 1", "43")]
    [InlineData("parse_i32(\" -7 \")", "-7")]
    [InlineData("parse_i64(\"9000000000\")", "9000000000")]
    [InlineData("parse_mid(\"2.5\") * 2.0", "5.0")]
    [InlineData("parse_chonk(\"1e3\")", "1000.0")]
    [InlineData("parse_chonk(\"7\")", "7.0")]
    [InlineData("len(\"abc\") + len(\"de\")", "5")]
    [InlineData("\"text\" |> upper |> len", "4")]
    public void StringAndParseFunctions(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Fact]
    public void LenIsAUsz() =>
        Assert.Equal([DiagnosticCategory.BadOperator], RunHelper.CompileErrors("n::i32 = 4\nsquish n + len(\"x\")"));

    [Theory]
    [InlineData("parse_i32(\"twelve\")")]
    [InlineData("parse_i32(\"3.5\")")]
    [InlineData("parse_i32(\"\")")]
    [InlineData("parse_i32(\"2147483648\")")]
    [InlineData("parse_mid(\"abc\")")]
    [InlineData("parse_mid(\"NaN\")")]
    public void BadParseInputRipsForNow(string expr)
    {
        var r = RunHelper.RuntimeError($"squish({expr})");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.Panic, d.Category);
    }

    [Theory]
    [InlineData("len(5)")]
    [InlineData("upper(c\"a\")")]
    [InlineData("parse_i32(12)")]
    public void ArgumentTypesAreChecked(string expr) =>
        Assert.Equal(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors($"x := {expr}")[0]);

    [Theory]
    [InlineData("len()")]
    [InlineData("len(\"a\", \"b\")")]
    [InlineData("slurp(\"a\", \"b\")")]
    [InlineData("rev()")]
    public void ArityIsChecked(string expr) =>
        Assert.Equal(DiagnosticCategory.WrongArgCount, RunHelper.CompileErrors($"x := {expr}")[0]);

    [Fact]
    public void UserDefinitionsShadowThePrelude()
    {
        Assert.Equal("shadowed\n", RunHelper.Ok("cook len(s::txt)::txt = \"shadowed\"\nsquish len(\"abc\")"));
        Assert.Equal("7\n", RunHelper.Ok("upper := 7\nsquish upper"));
    }

    [Fact]
    public void SlurpReadsLinesFromStdin()
    {
        var r = RunHelper.Run("a := slurp()\nb := slurp(\"next? \")\nsquish \"[{a}] [{b}]\"\nsquish \"[{slurp()}]\"", stdin: "one\ntwo\n");
        Assert.Equal(RunOutcome.Ok, r.Outcome);
        Assert.Equal("next? [one] [two]\n[]\n", r.Out); // the prompt prints, and end of input reads as ""
    }

    [Fact]
    public void SlurpedLinesCanBeParsedAndUsed()
    {
        var r = RunHelper.Run("n := parse_i32(slurp())\nsquish n * 2", stdin: "21\r\n");
        Assert.Equal("42\n", r.Out); // a CRLF line ending does not leak into the text
    }

    [Fact]
    public void PreludeNamesAreStillNotValues() =>
        Assert.Equal([DiagnosticCategory.NotYetBuilt], RunHelper.CompileErrors("f := upper"));

    [Theory]
    [InlineData("prelude_strings.splat")]
    [InlineData("iter_vowels.splat")]
    [InlineData("iter_ranges.splat")]
    [InlineData("iter_chain.splat")]
    public void GoldenProgramsRun(string file)
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, file), file);
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Golden.Compare(Golden.Dir, Path.ChangeExtension(file, ".out"), r.Out);
    }

    [Fact]
    public void SlurpGoldenReadsItsInputFile()
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, "prelude_slurp.splat"), "prelude_slurp.splat", Golden.Read(Golden.Dir, "prelude_slurp.in"));
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Golden.Compare(Golden.Dir, "prelude_slurp.out", r.Out);
    }

    [Theory]
    [InlineData("bad_step.splat", RunOutcome.CompileError)]
    [InlineData("bad_step_runtime.splat", RunOutcome.RuntimeError)]
    [InlineData("bad_parse.splat", RunOutcome.RuntimeError)]
    public void BadGoldenProgramsReportTheirDiagnostics(string file, RunOutcome expected)
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, file), file);
        Assert.Equal(expected, r.Outcome);
        Assert.All(r.Diag.Diagnostics, d => Assert.Matches("^SE[0-9]{5}$", d.Code));
        Golden.Compare(Golden.Dir, Path.ChangeExtension(file, ".err"), r.Rendered);
    }
}

public class SessionStateTests
{
    private static (SessionState Session, StringWriter Out, StringWriter Err) New(string stdin = "")
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        return (new SessionState(new StringReader(stdin), o, e), o, e);
    }

    [Fact]
    public void BindingsAndFunctionsPersistAcrossEntries()
    {
        var (s, o, _) = New();
        Assert.Equal(RunOutcome.Ok, s.Eval("x := 40").Outcome);
        Assert.Equal(RunOutcome.Ok, s.Eval("cook plus_two(n::i32)::i32 = n + 2").Outcome);
        Assert.Equal("=> 42 :: i32", s.Eval("plus_two(x)").Echo);
        Assert.Equal(["plus_two", "x"], s.Names);
    }

    [Fact]
    public void WagVariablesKeepTheirTypeAndValueAcrossEntries()
    {
        var (s, _, _) = New();
        s.Eval("wag n = 1");
        s.Eval("n += 4");
        Assert.Equal("=> 5 :: i32", s.Eval("n").Echo);
        // Reassigning with a different type is still a type error in a later entry.
        var bad = s.Eval("n = \"five\"");
        Assert.Equal(RunOutcome.CompileError, bad.Outcome);
        Assert.Equal("=> 5 :: i32", s.Eval("n").Echo);
    }

    [Fact]
    public void RedefiningATopLevelNameInALaterEntryIsAllowed()
    {
        var (s, _, _) = New();
        s.Eval("x := 1");
        Assert.Equal(RunOutcome.Ok, s.Eval("x := \"now text\"").Outcome); // would be a Redeclared error inside one file
        Assert.Equal("=> \"now text\" :: txt", s.Eval("x").Echo);
    }

    [Fact]
    public void RedeclaringInsideOneEntryIsStillAnError()
    {
        var (s, _, _) = New();
        var r = s.Eval("a := 1\na := 2");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal([DiagnosticCategory.Redeclared], r.Diagnostics.Diagnostics.Select(d => d.Category));
    }

    [Fact]
    public void EarlierFunctionsKeepTheDefinitionsTheyWereCheckedAgainst()
    {
        var (s, _, _) = New();
        s.Eval("cook base()::i32 = 1");
        s.Eval("cook uses_base()::i32 = base() + 10");
        s.Eval("cook base()::txt = \"redefined\"");
        Assert.Equal("=> 11 :: i32", s.Eval("uses_base()").Echo);
        Assert.Equal("=> \"redefined\" :: txt", s.Eval("base()").Echo);
    }

    [Fact]
    public void FailedCompileLeavesNothingBehind()
    {
        var (s, _, _) = New();
        s.Eval("keep := 1");
        var r = s.Eval("fresh := 2\nbad := fresh + \"x\"");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal(["keep"], s.Names);
        Assert.Equal(RunOutcome.CompileError, s.Eval("fresh").Outcome);
    }

    [Fact]
    public void ParseErrorLeavesNothingBehind()
    {
        var (s, _, _) = New();
        var r = s.Eval("q := (1 +");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Empty(s.Names);
    }

    [Fact]
    public void FailedRuntimeEntryRollsBackNewBindingsAndAssignments()
    {
        var (s, o, _) = New();
        s.Eval("wag total = 10");
        s.Eval("old := 1");
        var r = s.Eval("total += 5\nnew_name := 99\nsquish \"before the crash\"\nboom := 1 / (total - 15)\nsquish \"unreachable\"");
        Assert.Equal(RunOutcome.RuntimeError, r.Outcome);
        Assert.Contains("before the crash\n", o.ToString());          // output already written stays written
        Assert.DoesNotContain("unreachable", o.ToString());
        Assert.Equal("=> 10 :: i32", s.Eval("total").Echo);            // the += was undone
        Assert.Equal(["old", "total"], s.Names);                       // new_name and boom were never committed
    }

    [Fact]
    public void ARuntimeErrorInALoopRestoresAssignmentsMadeSoFar()
    {
        var (s, _, _) = New();
        s.Eval("wag total = 0");
        var r = s.Eval("spin i in 0..5:\n    total += 10\n    hmm i == 3:\n        squish 1 / 0\n    ;\n;");
        Assert.Equal(RunOutcome.RuntimeError, r.Outcome);
        Assert.Equal("=> 0 :: i32", s.Eval("total").Echo);
    }

    [Fact]
    public void SessionKeepsWorkingAfterAnyKindOfFailure()
    {
        var (s, _, _) = New();
        s.Eval("cook deep(n::i32)::i32 = deep(n + 1) + 1");
        Assert.Equal(RunOutcome.RuntimeError, s.Eval("deep(0)").Outcome);   // stack overflow
        Assert.Equal(RunOutcome.RuntimeError, s.Eval("rip \"nope\"").Outcome);
        Assert.Equal("=> 3 :: i32", s.Eval("1 + 2").Echo);
    }

    [Theory]
    [InlineData("42", "=> 42 :: i32")]
    [InlineData("-3 * 2", "=> -6 :: i32")]
    [InlineData("1.5", "=> 1.5 :: mid")]
    [InlineData("\"hi\"", "=> \"hi\" :: txt")]
    [InlineData("\"quote \\\" brace \\{\"", "=> \"quote \\\" brace \\{\" :: txt")]
    [InlineData("c\"x\"", "=> c\"x\" :: char")]
    [InlineData("yep", "=> yep :: bit")]
    [InlineData("0..4", "=> 0..4 :: range<i32>")]
    [InlineData("rev(0..=4 by 2)", "=> rev(0..=4 by 2) :: range<i32>")]
    [InlineData("0..2 then 5..6", "=> 0..2 then 5..6 :: chain<i32>")]
    [InlineData("3 in (1, 2, 3)", "=> yep :: bit")]
    [InlineData("len(\"abc\")", "=> 3 :: usz")]
    [InlineData("|x::i32| x + 1", "=> <cook> :: cook(i32)::i32")]
    public void ExpressionsEchoValueAndType(string source, string echo) => Assert.Equal(echo, New().Session.Eval(source).Echo);

    [Theory]
    [InlineData("x := 1")]
    [InlineData("wag w = 1")]
    [InlineData("squish \"printed, not echoed\"")]
    [InlineData("cook f()::i32 = 1")]
    [InlineData("wag w = 1\nw = 2")]
    [InlineData("spin i in 0..2:\n    i\n;")]
    public void StatementsAndZipValuesDoNotEcho(string source) => Assert.Null(New().Session.Eval(source).Echo);

    [Fact]
    public void OnlyTheLastExpressionOfAnEntryEchoes()
    {
        var (s, _, _) = New();
        Assert.Equal("=> 3 :: i32", s.Eval("1 + 1\n1 + 2").Echo);
        Assert.Null(s.Eval("1 + 1\nx := 5").Echo);
    }

    [Fact]
    public void EchoCanBeSuppressedForLoadedFiles() =>
        Assert.Null(New().Session.Eval("1 + 1", echo: false).Echo);

    [Fact]
    public void ResetForgetsEverything()
    {
        var (s, _, _) = New();
        s.Eval("x := 1");
        s.Eval("cook f()::i32 = 2");
        s.Reset();
        Assert.Empty(s.Names);
        Assert.Equal(RunOutcome.CompileError, s.Eval("x").Outcome);
        Assert.Equal(RunOutcome.CompileError, s.Eval("f()").Outcome);
        Assert.Equal(RunOutcome.Ok, s.Eval("x := 7").Outcome);
    }

    [Fact]
    public void SlurpReadsFromTheSessionInput()
    {
        var (s, o, _) = New("first\nsecond\n");
        s.Eval("a := slurp(\"? \")");
        s.Eval("b := slurp()");
        Assert.Equal("=> \"first/second\" :: txt", s.Eval("a + \"/\" + b").Echo);
        Assert.Equal("? ", o.ToString());
    }

    [Fact]
    public void ClosuresKeepTheirStateAcrossEntries()
    {
        var (s, _, _) = New();
        s.Eval("cook make()::cook()::i32:\n    wag n = 0\n    cook next()::i32:\n        n += 1\n        yeet n\n    ;\n    yeet next\n;");
        s.Eval("tick := make()");
        s.Eval("tick()");
        s.Eval("tick()");
        Assert.Equal("=> 3 :: i32", s.Eval("tick()").Echo);
    }
}

public class ReplTests
{
    private static (int Code, string Out, string Err) Session(string input, bool prompts = false)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        var code = new Repl(new StringReader(input), o, e, prompts, color: false).Run();
        return (code, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData("1 + 2", false)]
    [InlineData("x := 5", false)]
    [InlineData("", false)]
    [InlineData("hmm yep:", true)]
    [InlineData("hmm yep:\n    squish 1\n", true)]
    [InlineData("hmm yep:\n    squish 1\n;", false)]
    [InlineData("spin i in 0..3:\n    hmm i == 1:\n        skip\n    ;\n", true)]       // inner closed, outer still open
    [InlineData("spin i in 0..3:\n    hmm i == 1:\n        skip\n    ;\n;", false)]
    [InlineData("hmm a:\n    x\nmeh b:\n    y\nnah:\n    z\n", true)]                  // meh/nah colons do not open blocks
    [InlineData("hmm a:\n    x\nmeh b:\n    y\nnah:\n    z\n;", false)]
    [InlineData("hmm a:\n    hmm b:\n        x\n    nah:\n        y\n    ;\n", true)]
    [InlineData("hmm a:\n    hmm b:\n        x\n    nah:\n        y\n    ;\nnah:\n    z\n;", false)]
    [InlineData("cook f(n::i32)::i32:", true)]
    [InlineData("cook f(n::i32)::i32 = n + 1", false)]                                 // '::' is not a block opener
    [InlineData("x := (1 +", true)]
    [InlineData("x := (1 + 2)", false)]
    [InlineData("squish(1,\n  2", true)]
    [InlineData("x := \"unfinished", true)]
    [InlineData("x := \"multi\nline\"", false)]
    [InlineData("#- a comment that never ends", true)]
    [InlineData("#- closed -# x := 1", false)]
    [InlineData("# a line comment: with a colon", false)]
    [InlineData("x := \"a: colon in a string\"", false)]
    [InlineData(")", false)]                                                          // junk is not 'incomplete'; the parser will complain
    [InlineData(";", false)]
    public void ContinuationDetection(string text, bool incomplete) => Assert.Equal(incomplete, Repl.IsIncomplete(text));

    [Fact]
    public void ExpressionsEchoAndMultiLineBlocksRun()
    {
        var (code, o, e) = Session("1 + 2\nwag n = 0\nspin i in 0..4:\n    n += i\n;\nn\n");
        Assert.Equal(0, code);
        Assert.Equal("=> 3 :: i32\n=> 6 :: i32\n", o);
        Assert.Equal("", e);
    }

    [Fact]
    public void EndOfInputInsideABlockReportsTheMissingSemicolon()
    {
        var (_, _, e) = Session("hmm yep:\n    squish 1\n");
        Assert.Contains("[SE00001]", e);
    }

    [Fact]
    public void ErrorsDoNotEndTheSession()
    {
        var (code, o, e) = Session("nope\n1 / 0\nrip \"bye\"\nx := \"fine\"\nx\n");
        Assert.Equal(0, code);
        Assert.Equal("=> \"fine\" :: txt\n", o);
        Assert.Contains("[SE00101]", e);
        Assert.Contains("[SE00300]", e);
        Assert.Contains("[SE00305]", e);
    }

    [Fact]
    public void QuitStopsReading()
    {
        var (_, o, _) = Session("1\n:quit\n2\n");
        Assert.Equal("=> 1 :: i32\n", o);
        Assert.Equal("=> 1 :: i32\n", Session("1\n:q\n2\n").Out);
    }

    [Fact]
    public void HelpListsTheCommands()
    {
        var (_, o, _) = Session(":help\n");
        foreach (var c in new[] { ":help", ":reset", ":load", ":history", ":quit" }) Assert.Contains(c, o);
    }

    [Fact]
    public void UnknownCommandIsADiagnostic()
    {
        var (code, o, e) = Session(":bogus\n");
        Assert.Equal(0, code);
        Assert.Equal("", o);
        Assert.Contains("[SE00012]", e);
        Assert.Contains(":bogus", e);
    }

    [Fact]
    public void ResetClearsTheSession()
    {
        var (_, o, e) = Session("x := 1\nx\n:reset\nx\n");
        Assert.Contains("=> 1 :: i32", o);
        Assert.Contains("Session cleared", o);
        Assert.Contains("[SE00101]", e);
    }

    [Fact]
    public void LoadRunsAFileInsideTheSessionWithoutEchoing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"splat_load_{Guid.NewGuid():N}.splat");
        File.WriteAllText(path, "base := 100\ncook add(n::i32)::i32 = n + base\n1 + 1\nsquish \"loaded\"\n");
        try
        {
            var (_, o, e) = Session($":load {path}\nadd(5)\n:vars\n");
            Assert.Equal("loaded\n=> 105 :: i32\nadd, base\n", o);
            Assert.Equal("", e);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadIsAllOrNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"splat_load_{Guid.NewGuid():N}.splat");
        File.WriteAllText(path, "kept := 1\nbad := kept + \"x\"\n");
        try
        {
            var (_, o, e) = Session($":load {path}\n:vars\n");
            Assert.Equal("(nothing defined yet)\n", o);
            Assert.Contains("[SE00106]", e);
            Assert.Contains(Path.GetFileName(path), e); // spans name the loaded file
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadOfAMissingFileIsDetectiveFail()
    {
        var (_, o, e) = Session(":load definitely_not_here.splat\n:load\n");
        Assert.Equal("", o);
        Assert.Contains("[SE00302]", e);
        Assert.Contains("[SE00002]", e); // ':load' with no file name
    }

    [Fact]
    public void HistoryListsEntriesButNotCommands()
    {
        var (_, o, _) = Session("x := 1\nspin i in 0..2:\n    x\n;\n:history\n");
        Assert.Contains("   1  x := 1", o);
        Assert.Contains("   2  spin i in 0..2:", o);
        Assert.DoesNotContain(":history\n   3", o);
    }

    [Fact]
    public void SlurpAnswersFromTheTranscriptItself()
    {
        var (_, o, _) = Session("name := slurp()\nZed\nsquish \"hi {name}\"\n");
        Assert.Equal("hi Zed\n", o);
    }

    [Fact]
    public void PromptsAppearOnlyWhenAskedFor()
    {
        Assert.DoesNotContain(Repl.Prompt, Session("1\n").Out);
        var (_, o, _) = Session("hmm yep:\n    squish 1\n;\n:quit\n", prompts: true);
        Assert.Contains("SPLAT", o);
        Assert.Contains(Repl.Prompt, o);
        Assert.Contains(Repl.ContinuationPrompt, o);
    }

    [Fact]
    public void TranscriptMatchesItsGoldenFiles()
    {
        var (code, o, e) = Session(Golden.Read(Golden.Dir, "repl_session.in"));
        Assert.Equal(0, code);
        Golden.Compare(Golden.Dir, "repl_session.out", o);
        Golden.Compare(Golden.Dir, "repl_session.err", e);
        Assert.All(e.Split('\n').Where(l => l.StartsWith('[')), l => Assert.Matches(@"^\[SE[0-9]{5}\] ", l));
    }
}

public class Milestone1AcceptanceTests
{
    [Fact]
    public void AcceptanceScriptRunsAndMatchesItsExpectedOutput()
    {
        var r = RunHelper.Run(Golden.Read(Golden.M1Dir, "m1_acceptance.splat"), "m1_acceptance.splat", Golden.Read(Golden.M1Dir, "m1_acceptance.in"));
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Golden.Compare(Golden.M1Dir, "m1_acceptance.out", r.Out);
    }

    [Fact]
    public void AcceptanceScriptCoversEveryMilestone1Bullet()
    {
        // Lexer, Pratt parser, blocks, variables, hmm/meh/nah, spin (all forms), squish/slurp, functions.
        var text = Golden.Read(Golden.M1Dir, "m1_acceptance.splat");
        foreach (var needle in new[] { ":=", "wag ", "hmm ", "meh ", "nah:", "spin while", "spin:", "spin i in", " by ", " then ", "rev(", " in (", "slurp(", "squish ", "cook ", "|>", "**", "0xFF", "0b1010", "#-" })
            Assert.Contains(needle, text);
    }

    [Fact]
    public void ErrorTourShowsTheComedicDiagnostics()
    {
        // Each snippet is one category from spec section 9 that Milestone 1 can produce; every one carries its ID,
        // a caret under the right span, and a technical Fix line.
        (string Code, string Id, string Headline)[] tour =
        [
            ("spin:\n    squish 1\n", "SE00001", "OOF"),
            ("x::i32 = \"hello\"", "SE00100", "IDENTITY CRISIS"),
            ("squish nobody_home", "SE00101", "WHO DIS"),
            ("cook f(a::i32)::i32 = a\nsquish f(1, 2)", "SE00102", "COUNT YOUR ARGS"),
            ("x := 1\nx = 2", "SE00103", "FROZEN SOLID"),
            ("squish 1 +", "SE00002", "WAIT WHAT"),
            ("squish 1 / 0", "SE00300", "BLACK HOLE"),
            ("x::i8 = 127\nsquish x + 1", "SE00304", "OVERFLOW"),
            ("rip \"manual panic\"", "SE00305", "RIP"),
            ("squish 0..9 by 0", "SE00113", "STEP BROTHER"),
            ("squish \"never closed", "SE00003", "DANGLING STRING"),
            ("squish 5 § 3", "SE00005", "ALIEN GLYPH"),
        ];
        foreach (var (code, id, headline) in tour)
        {
            var r = RunHelper.Run(code);
            Assert.NotEqual(RunOutcome.Ok, r.Outcome);
            var line = r.Rendered.Split('\n').FirstOrDefault(l => l.StartsWith($"[{id}] "));
            Assert.True(line is not null && line.Contains(headline), $"[{id}] {headline} not found in:\n{r.Rendered}");
            Assert.Contains("^", r.Rendered);
            Assert.Contains("Fix:", r.Rendered);
        }
    }
}

[Collection("Console")]
public class CliPhase6Tests
{
    private static (int Code, string Out, string Err) Cli(string stdin, params string[] args)
    {
        var oldOut = Console.Out; var oldErr = Console.Error; var oldIn = Console.In;
        var so = new StringWriter { NewLine = "\n" }; var se = new StringWriter { NewLine = "\n" };
        Console.SetOut(so); Console.SetError(se); Console.SetIn(new StringReader(stdin));
        try { return (Splat.Cli.Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); Console.SetIn(oldIn); }
    }

    [Fact]
    public void NoArgumentsStartsTheReplOnStandardInput()
    {
        var (code, o, e) = Cli("6 * 7\n:quit\n");
        Assert.Equal(0, code);
        Assert.Equal("=> 42 :: i32\n", o);
        Assert.Equal("", e);
    }

    [Fact]
    public void ReplSubcommandIsTheSame() => Assert.Equal("=> 2 :: i32\n", Cli("1 + 1\n", "repl").Out);

    [Fact]
    public void RunReadsSlurpFromStandardInput()
    {
        var (code, o, _) = Cli("Ayan\n41\n", "run", Path.Combine(Golden.M1Dir, "m1_acceptance.splat"));
        Assert.Equal(0, code);
        Assert.Contains("Hello, Ayan! Your number plus one is 42.", o);
    }

    [Fact]
    public void ExitCodesAreZeroSixtyFiveAndSeventy()
    {
        Assert.Equal(0, Cli("", "run", Path.Combine(Golden.Dir, "iter_chain.splat")).Code);
        Assert.Equal(65, Cli("", "run", Path.Combine(Golden.Dir, "bad_step.splat")).Code);
        Assert.Equal(70, Cli("", "run", Path.Combine(Golden.Dir, "bad_step_runtime.splat")).Code);
        Assert.Equal(66, Cli("", "run", Path.Combine(Golden.Dir, "nope.splat")).Code);
        Assert.Equal(64, Cli("", "run").Code);
        Assert.Equal(64, Cli("", "frobnicate").Code);
    }

    [Fact]
    public void CheckAndTokensAndAstStillWork()
    {
        Assert.Equal(0, Cli("", "check", Path.Combine(Golden.Dir, "iter_ranges.splat")).Code);
        Assert.Equal(65, Cli("", "check", Path.Combine(Golden.Dir, "bad_step.splat")).Code);
        Assert.Equal(0, Cli("", "tokens", Path.Combine(Golden.Dir, "iter_vowels.splat")).Code);
        var (code, o, _) = Cli("", "ast", "-e", "x in 0..10 by 2 then 50..60");
        Assert.Equal(0, code);
        Assert.Equal("(in x (then (by (.. 0 10) 2) (.. 50 60)))\n", o);
    }

    [Fact]
    public void VersionFlagPrintsTheBanner()
    {
        var (code, o, _) = Cli("", "--version");
        Assert.Equal(0, code);
        Assert.Contains("SPLAT", o);
        Assert.Contains(Splat.Cli.Program.Version, o);
    }
}
