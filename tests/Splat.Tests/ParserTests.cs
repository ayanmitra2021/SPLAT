using Splat.Core;
using Splat.Syntax;

namespace Splat.Tests;

internal static class ParseHelper
{
    public static (Program Program, DiagnosticEngine Diag, SourceText Source) Parse(string text, string file = "t.splat")
    {
        var diag = new DiagnosticEngine();
        var src = new SourceText(file, text);
        diag.AddSource(src);
        return (Parser.ParseProgram(src, diag), diag, src);
    }

    /// <summary>Parses clean input and returns one flat s-expression per statement.</summary>
    public static string[] Statements(string text)
    {
        var (program, diag, _) = Parse(text);
        Assert.False(diag.HasErrors, Messages(diag));
        return program.Statements.Select(s => AstPrinter.Stmt(s).Flat()).ToArray();
    }

    public static string Single(string text)
    {
        var all = Statements(text);
        Assert.Single(all);
        return all[0];
    }

    public static string Expr(string text)
    {
        var diag = new DiagnosticEngine();
        var src = new SourceText("t.splat", text);
        var e = Parser.ParseExpression(src, diag);
        Assert.False(diag.HasErrors, Messages(diag));
        return AstPrinter.Expr(e).Flat();
    }

    public static string Messages(DiagnosticEngine d) => string.Join("\n", d.Diagnostics.Select(x => x.Message));

    public static DiagnosticCategory[] Categories(DiagnosticEngine d) => d.Diagnostics.Select(x => x.Category).ToArray();

    public static string Render(DiagnosticEngine d)
    {
        var w = new StringWriter { NewLine = "\n" };
        new ConsolePrinter(w, useColor: false).Print(d);
        return w.ToString();
    }
}

public class ParserStatementTests
{
    [Theory]
    [InlineData("x := 42", "(bind x 42)")]
    [InlineData("x::mid = 42.0", "(bind x (: mid) 42.0)")]
    [InlineData("wag x = 42", "(wag x 42)")]
    [InlineData("wag x::mid = 42.0", "(wag x (: mid) 42.0)")]
    [InlineData("A::mid[3, 4] = zeros(3, 4)", "(bind A (: (tensor mid 3 4)) (call zeros 3 4))")]
    [InlineData("v::mid[$B, ?] = w", "(bind v (: (tensor mid $B ?)) w)")]
    [InlineData("r::&txt = s", "(bind r (: (& txt)) s)")]
    public void Bindings(string src, string expected) => Assert.Equal(expected, ParseHelper.Single(src));

    [Theory]
    [InlineData("x = 1", "(= x 1)")]
    [InlineData("x += 1", "(+= x 1)")]
    [InlineData("x -= 1", "(-= x 1)")]
    [InlineData("x *= 2", "(*= x 2)")]
    [InlineData("x /= 2", "(/= x 2)")]
    [InlineData("A @= B", "(@= A B)")]
    [InlineData("A .*= B", "(.*= A B)")]
    [InlineData("A ./= B", "(./= A B)")]
    [InlineData("a[i, j] = 0", "(= (index a i j) 0)")]
    [InlineData("p.x = 3", "(= (. p x) 3)")]
    public void Assignments(string src, string expected) => Assert.Equal(expected, ParseHelper.Single(src));

    [Fact]
    public void ExpressionBodiedCook() =>
        Assert.Equal("(cook add (params (a i32) (b i32)) (-> i32) (= (+ a b)))",
            ParseHelper.Single("cook add(a::i32, b::i32)::i32 = a + b"));

    [Fact]
    public void BlockBodiedCookWithDefaultsAndNoReturnType() =>
        Assert.Equal("(cook greet (params (who txt \"you\")) (block (call squish (str \"hi \" (interp who)))))",
            ParseHelper.Single("cook greet(who::txt = \"you\"):\n    squish \"hi {who}\"\n;"));

    [Fact]
    public void GenericsAndReceivers() =>
        Assert.Equal("(cook f (generics T $N U:Quirk) (params &me ~me (x T)) (-> (tensor mid $N)) (= x))",
            ParseHelper.Single("cook f<T, $N, U: Quirk>(&me, ~me, x::T)::mid[$N] = x"));

    [Fact]
    public void OperatorOverloads()
    {
        Assert.Equal("(cook op + (params me (o V)) (-> V) (= o))", ParseHelper.Single("cook op +(me, o::V)::V = o"));
        Assert.Equal("(cook op [] (params me (i usz)) (-> mid) (= 0.0))", ParseHelper.Single("cook op [](me, i::usz)::mid = 0.0"));
        // 'op' followed by '(' is just a function with that name.
        Assert.Equal("(cook op (params (x i32)) (= x))", ParseHelper.Single("cook op(x::i32) = x"));
    }

    [Fact]
    public void FunctionTypeParameter() =>
        Assert.Equal("(cook ap (params (f (cook-type (params i32 i32) i32))) (= f))",
            ParseHelper.Single("cook ap(f::cook(i32, i32)::i32) = f"));

    [Fact]
    public void ClosingNestedGenericsSplitsGreaterGreater() =>
        Assert.Equal("(bind m (: (Maybe (Maybe (Maybe i32)))) x)", ParseHelper.Single("m::Maybe<Maybe<Maybe<i32>>> = x"));

    [Fact]
    public void ClosingGenericsRightBeforeAssignSplitsGreaterEqual() =>
        Assert.Equal("(wag m (: (Maybe i32)) (call f))", ParseHelper.Single("wag m::Maybe<i32>= f()"));

    [Fact]
    public void SpinInAllThreeForms()
    {
        Assert.Equal("(spin-in i (..= 1 15) (block (call squish i)))", ParseHelper.Single("spin i in 1..=15:\n squish i\n;"));
        Assert.Equal("(spin-while (< n 3) (block (+= n 1)))", ParseHelper.Single("spin while n < 3:\n n += 1\n;"));
        Assert.Equal("(spin (block (bail)))", ParseHelper.Single("spin:\n bail\n;"));
    }

    [Fact]
    public void BailAndSkip() =>
        Assert.Equal("(spin (block (skip) (bail)))", ParseHelper.Single("spin:\n skip\n bail\n;"));

    [Fact]
    public void NestedBlocksEachCloseWithTheirOwnSemicolon()
    {
        var (p, diag, _) = ParseHelper.Parse("spin:\n spin:\n  bail\n ;\n bail\n;\nx := 1");
        Assert.False(diag.HasErrors);
        Assert.Equal(2, p.Statements.Count);
        var outer = Assert.IsType<SpinStmt>(p.Statements[0]);
        Assert.Equal(2, outer.Body.Statements.Count);
    }

    [Fact]
    public void DoubleSemicolonClosesTwoBlocks()
    {
        var (p, diag, _) = ParseHelper.Parse("spin:\n spin:\n  bail\n;;\nx := 1");
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        Assert.Equal(2, p.Statements.Count);
        var outer = Assert.IsType<SpinStmt>(p.Statements[0]);
        Assert.Single(outer.Body.Statements);
        Assert.IsType<SpinStmt>(outer.Body.Statements[0]);
        Assert.IsType<BindStmt>(p.Statements[1]);
    }

    [Fact]
    public void TripleSemicolonClosesThreeBlocks()
    {
        var (p, diag, _) = ParseHelper.Parse("cook f():\n spin:\n  spin:\n   bail\n;;;\nx := 1");
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        Assert.Equal(2, p.Statements.Count);
    }

    [Fact]
    public void HmmChainSharesOneClosingSemicolon()
    {
        var (p, diag, _) = ParseHelper.Parse("hmm a:\n x := 1\nmeh b:\n x := 2\nmeh c:\n x := 3\nnah:\n x := 4\n;\ny := 5");
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        Assert.Equal(2, p.Statements.Count);
        var h = Assert.IsType<HmmStmt>(p.Statements[0]);
        Assert.Equal(3, h.Branches.Count);
        Assert.NotNull(h.Else);
        Assert.IsType<BindStmt>(p.Statements[1]); // 'y' is outside the chain
    }

    [Fact]
    public void HmmWithoutElse() =>
        Assert.Equal("(hmm (if (> x 0) (block (yeet 1))))", ParseHelper.Single("hmm x > 0:\n yeet 1\n;"));

    [Fact]
    public void NestedHmmEachNeedItsOwnCloser()
    {
        Assert.Equal("(hmm (if a (block (hmm (if b (block (yeet 1))) (else (block (yeet 2)))))))",
            ParseHelper.Single("hmm a:\n hmm b:\n  yeet 1\n nah:\n  yeet 2\n ;\n;"));
    }

    [Fact]
    public void InnerHmmTakesTheMehThatFollowsIt()
    {
        // Without an inner ';', 'meh' binds to the inner chain (dangling-else rule); the file then runs out of ';'.
        var (_, diag, _) = ParseHelper.Parse("hmm a:\n hmm b:\n  x := 1\nmeh c:\n  x := 2\n;");
        Assert.Equal([DiagnosticCategory.MissingSemicolon], ParseHelper.Categories(diag));
    }

    [Fact]
    public void YeetWithAndWithoutValue()
    {
        Assert.Equal("(yeet (+ 1 2))", ParseHelper.Single("yeet 1 + 2"));
        Assert.Equal("(cook f (params) (block (hmm (if yep (block (yeet))))))", ParseHelper.Single("cook f():\n hmm yep:\n  yeet\n ;\n;"));
    }

    [Fact]
    public void YeetValueMustStartOnTheSameLine() =>
        Assert.Equal("(cook f (params) (block (yeet) (bind x 1)))", ParseHelper.Single("cook f():\n yeet\n x := 1\n;"));

    [Fact]
    public void RipPanics()
    {
        Assert.Equal("(rip \"boom\")", ParseHelper.Single("rip \"boom\""));
        Assert.Equal("(rip)", ParseHelper.Single("rip"));
    }

    [Fact]
    public void CommandCallWithoutParentheses()
    {
        Assert.Equal("(call squish (str \"Got \" (interp val)))", ParseHelper.Single("squish \"Got {val}\""));
        Assert.Equal("(call squish a b (+ c 1))", ParseHelper.Single("squish a, b, c + 1"));
        Assert.Equal("(call yell \"bad\")", ParseHelper.Single("yell \"bad\""));
        Assert.Equal("(bind name (call slurp \"name? \"))", ParseHelper.Single("name := slurp(\"name? \")"));
    }

    [Fact]
    public void ParenthesisedCallsAlwaysWork()
    {
        Assert.Equal("(call squish \"hi\")", ParseHelper.Single("squish(\"hi\")"));
        Assert.Equal("(call squish \"hi\")", ParseHelper.Single("squish (\"hi\")"));
    }

    [Fact]
    public void CommandCallArgumentMustBeOnTheSameLine() =>
        Assert.Equal(["squish", "(bind x 1)"], ParseHelper.Statements("squish\nx := 1"));

    [Fact]
    public void OnlyPreludeNamesTakeCommandForm() =>
        // Not a call: two expression statements in a row (whitespace is insignificant).
        Assert.Equal(["frobnicate", "\"x\""], ParseHelper.Statements("frobnicate \"x\""));

    [Fact]
    public void StringInterpolationIsParsedWithRealSpans()
    {
        var (p, diag, _) = ParseHelper.Parse("s := \"a {x + 1:.3} b\"");
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        var bind = Assert.IsType<BindStmt>(p.Statements[0]);
        var tpl = Assert.IsType<StringTemplate>(bind.Value);
        var interp = tpl.Parts.OfType<TemplateExpr>().Single();
        Assert.Equal(".3", interp.Format);
        var add = Assert.IsType<BinaryExpr>(interp.Value);
        Assert.Equal(1, add.Span.Line);
        Assert.Equal(10, add.Span.Col); // 'x' sits at column 10 of the line, not column 1 of the interpolation
    }

    [Fact]
    public void NestedInterpolationAndStringsInsideBraces() =>
        Assert.Equal("(call squish (str \"[\" (interp (call f (str \"n \" (interp name)))) \"]\"))",
            ParseHelper.Single("squish \"[{f(\"n {name}\")}]\""));

    [Fact]
    public void ConstructsOfLaterMilestonesAreSkippedWithoutDerailingTheParse()
    {
        var src = "mold Dog from Animal with Talkable:\n cook bark(me):\n  hmm a:\n   x := 1\n  meh b:\n   x := 2\n  ;\n ;\n;\nafter := 1";
        var (p, diag, _) = ParseHelper.Parse(src);
        Assert.Equal([DiagnosticCategory.NotYetBuilt], ParseHelper.Categories(diag));
        Assert.Equal(2, p.Statements.Count);
        Assert.IsType<ReservedStmt>(p.Statements[0]);
        Assert.IsType<BindStmt>(p.Statements[1]);
    }

    [Fact]
    public void ReservedConstructWithQuirkBoundInGenerics()
    {
        var (p, diag, _) = ParseHelper.Parse("brain Net<$In, T: Quirk>:\n lin(In, 4)\n;\nok := 1");
        Assert.Equal([DiagnosticCategory.NotYetBuilt], ParseHelper.Categories(diag));
        Assert.Equal(2, p.Statements.Count);
    }

    [Fact]
    public void ReservedLineStatementsSkipOnlyTheirLine()
    {
        var (p, diag, _) = ParseHelper.Parse("nuke x\ny := 1");
        Assert.Single(diag.Diagnostics);
        Assert.Equal(2, p.Statements.Count);
    }

    [Fact]
    public void CommentsAndBlankLinesAreInvisible() =>
        Assert.Equal(["(bind a 1)", "(bind b 2)"], ParseHelper.Statements("# c\na := 1 #- x -#\n\nb := 2 # end"));

    [Fact]
    public void EmptyProgram() => Assert.Empty(ParseHelper.Statements("  # nothing\n"));

    [Fact]
    public void StatementSpansCoverTheWholeStatement()
    {
        var (p, _, src) = ParseHelper.Parse("x := 1 +\n    2");
        var s = p.Statements[0];
        Assert.Equal(1, s.Span.Line);
        Assert.Equal(1, s.Span.Col);
        Assert.Equal(src.Text.Length, src.OffsetOf(s.Span.Line, s.Span.Col) + s.Span.Length);
    }
}

public class ParserErrorTests
{
    [Fact]
    public void EndOfFileInsideABlockIsOofAtTheOpeningColon()
    {
        var (_, diag, _) = ParseHelper.Parse("cook f(n::i32)::i32:\n    yeet n\n");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.MissingSemicolon, d.Category);
        Assert.Equal(1, d.Span.Line);
        Assert.Equal(20, d.Span.Col);
        Assert.Equal(1, d.Span.Length);
        Assert.Contains("💀 OOF [Line 1]", d.Message);
    }

    [Fact]
    public void EveryUnclosedNestedBlockIsReported()
    {
        var (_, diag, _) = ParseHelper.Parse("spin:\n    spin:\n        bail\n");
        Assert.Equal([DiagnosticCategory.MissingSemicolon, DiagnosticCategory.MissingSemicolon], ParseHelper.Categories(diag));
        Assert.Equal([2, 1], diag.Diagnostics.Select(d => d.Span.Line).ToArray()); // innermost first
    }

    [Fact]
    public void UnclosedHmmChainPointsAtTheLastOpenBranch()
    {
        var (_, diag, _) = ParseHelper.Parse("hmm a:\n x := 1\nnah:\n x := 2\n");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.MissingSemicolon, d.Category);
        Assert.Equal(3, d.Span.Line);
    }

    [Fact]
    public void StraySemicolonAtTopLevel()
    {
        var (p, diag, _) = ParseHelper.Parse("x := 1\n;\ny := 2");
        Assert.Equal([DiagnosticCategory.StraySemicolon], ParseHelper.Categories(diag));
        Assert.Equal(2, p.Statements.Count);
    }

    [Fact]
    public void OrphanMehIsReportedOnceWithoutCascade()
    {
        var (p, diag, _) = ParseHelper.Parse("meh x > 1:\n    squish \"hi\"\n;\ny := 2");
        Assert.Equal([DiagnosticCategory.OrphanBranch], ParseHelper.Categories(diag));
        Assert.IsType<BindStmt>(p.Statements[^1]);
    }

    [Fact]
    public void MissingColonAfterHmmConditionStillParsesTheBody()
    {
        var (p, diag, _) = ParseHelper.Parse("hmm x\n squish \"a\"\n;\ny := 1");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.UnexpectedToken, d.Category);
        Assert.Equal(2, p.Statements.Count);
    }

    [Fact]
    public void WagWithColonAssignGetsATargetedMessage()
    {
        var (_, diag, _) = ParseHelper.Parse("wag x := 1");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("'wag' variables use '='", d.Message);
    }

    [Fact]
    public void AssigningToAValueIsRejected()
    {
        var (_, diag, _) = ParseHelper.Parse("5 = 4\nf(x) = 1");
        Assert.Equal([DiagnosticCategory.NotAssignable, DiagnosticCategory.NotAssignable], ParseHelper.Categories(diag));
    }

    [Fact]
    public void BindWithoutValue()
    {
        var (_, diag, _) = ParseHelper.Parse("x := ;");
        Assert.Equal([DiagnosticCategory.UnexpectedToken], ParseHelper.Categories(diag));
    }

    [Fact]
    public void TypedBindWithoutEquals()
    {
        var (_, diag, _) = ParseHelper.Parse("x::i32 5");
        Assert.Equal([DiagnosticCategory.UnexpectedToken], ParseHelper.Categories(diag));
    }

    [Fact]
    public void UnclosedParenthesis()
    {
        var (_, diag, _) = ParseHelper.Parse("x := (1 + 2\ny := 3");
        Assert.True(diag.HasErrors);
        Assert.All(diag.Diagnostics, d => Assert.Equal(DiagnosticCategory.UnexpectedToken, d.Category));
    }

    [Fact]
    public void CookWithoutBody()
    {
        var (_, diag, _) = ParseHelper.Parse("cook f(x::i32)::i32\ny := 1");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("body of 'f'", d.Message + d.Fix);
    }

    [Fact]
    public void ParameterWithoutType()
    {
        var (_, diag, _) = ParseHelper.Parse("cook f(x) = x");
        Assert.Contains("'::Type' after parameter 'x'", diag.Diagnostics[0].Message);
    }

    [Fact]
    public void BadSpinHeader()
    {
        var (_, diag, _) = ParseHelper.Parse("spin 5:\n bail\n;");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void ChainedRangesAreRejected()
    {
        var (_, diag, _) = ParseHelper.Parse("r := 1..2..3");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void SeveralErrorsSurfaceInOneRun()
    {
        var src = "a := + 1\nb := 2\nc := (3\nd := 4\nmeh d > 1:\n;\nspin:\n bail\n";
        var (p, diag, _) = ParseHelper.Parse(src);
        Assert.True(diag.ErrorCount >= 4, ParseHelper.Messages(diag));
        // Recovery kept the good statement between the errors.
        Assert.Contains(p.Statements, s => s is BindStmt { Name: "b" });
    }

    [Fact]
    public void LexerErrorsAndParserErrorsShareTheEngine()
    {
        var (_, diag, _) = ParseHelper.Parse("x := \"unterminated\ny := 1");
        Assert.Contains(DiagnosticCategory.UnterminatedString, ParseHelper.Categories(diag));
    }

    [Fact]
    public void PathologicalNestingReportsInsteadOfOverflowingTheStack()
    {
        var (_, diag, _) = ParseHelper.Parse("x := " + new string('(', 5000) + "1" + new string(')', 5000));
        Assert.Contains(DiagnosticCategory.TooDeep, ParseHelper.Categories(diag));

        var (_, diag2, _) = ParseHelper.Parse(string.Concat(Enumerable.Repeat("spin:\n", 3000)));
        Assert.Contains(DiagnosticCategory.TooDeep, ParseHelper.Categories(diag2));
    }

    [Fact]
    public void EmptyInterpolationIsAnError()
    {
        var (_, diag, _) = ParseHelper.Parse("s := \"a {} b\"");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void ParserNeverThrowsOnGarbage()
    {
        var rng = new Random(1234);
        string[] pieces = [":", ";", ";;", "hmm", "meh", "nah", "spin", "cook", "(", ")", "[", "]", "x", "1", "\"s{", "}", "::", ":=", "=", "yeet", "wag", "|", "'", ",", "mold", "pick", ".", "..", "@", "->"];
        for (int i = 0; i < 400; i++)
        {
            var text = string.Join(" ", Enumerable.Range(0, rng.Next(1, 30)).Select(_ => pieces[rng.Next(pieces.Length)]));
            var exception = Record.Exception(() => ParseHelper.Parse(text));
            Assert.True(exception is null, $"input: {text}\n{exception}");
        }
    }
}

public class ParserExpressionTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "(+ 1 (* 2 3))")]
    [InlineData("(1 + 2) * 3", "(* (+ 1 2) 3)")]
    [InlineData("1 - 2 - 3", "(- (- 1 2) 3)")]
    [InlineData("-2 ** 2", "(- (** 2 2))")]
    [InlineData("2 ** 3 ** 2", "(** 2 (** 3 2))")]
    [InlineData("2 ** -1", "(** 2 (- 1))")]
    [InlineData("-a @ b", "(@ (- a) b)")]
    [InlineData("a or b and c", "(or a (and b c))")]
    [InlineData("a and b or c", "(or (and a b) c)")]
    [InlineData("a < b and c == d", "(and (< a b) (== c d))")]
    [InlineData("!a and b", "(and (! a) b)")]
    [InlineData("a + 1 .. b * 2", "(.. (+ a 1) (* b 2))")]
    [InlineData("0..=n", "(..= 0 n)")]
    [InlineData("x |> f |> g", "(|> (|> x f) g)")]
    [InlineData("a and b |> f", "(|> (and a b) f)")]
    [InlineData("x |> f >> g", "(>> (|> x f) g)")]
    [InlineData("A @ B + C", "(+ (@ A B) C)")]
    [InlineData("A .* B * 2", "(* (.* A B) 2)")]
    [InlineData("a * b .+ c", "(.+ (* a b) c)")]
    [InlineData("u · v + 1", "(+ (· u v) 1)")]
    [InlineData("A \\ b", "(\\ A b)")]
    [InlineData("f(1, lr = 2)(3)", "(call (call f 1 (arg lr 2)) 3)")]
    [InlineData("a.b.c(1)", "(call (. (. a b) c) 1)")]
    [InlineData("A[1, 2]", "(index A 1 2)")]
    [InlineData("A[:, 0..3]", "(index A : (.. 0 3))")]
    [InlineData("A'", "(' A)")]
    [InlineData("A''", "('' A)")]
    [InlineData("A' @ B", "(@ (' A) B)")]
    [InlineData("f(x)'", "(' (call f x))")]
    [InlineData("-A'", "(- (' A))")]
    [InlineData("&x", "(& x)")]
    [InlineData("~x", "(~ x)")]
    [InlineData("()", "()")]
    [InlineData("[1, 2, 3]", "(list 1 2 3)")]
    [InlineData("[[1, 2], [3, 4]]", "(list (list 1 2) (list 3 4))")]
    [InlineData("yep and nop", "(and yep nop)")]
    [InlineData("c\"a\"", "c\"a\"")]
    [InlineData("a % b == 0", "(== (% a b) 0)")]
    [InlineData("a >> b", "(>> a b)")]
    public void Precedence(string src, string expected) => Assert.Equal(expected, ParseHelper.Expr(src));
}

public class ParserRoundTripTests
{
    private static string Sx(Program p) => AstPrinter.ProgramText(p);

    private static void AssertRoundTrips(string text)
    {
        var (p1, d1, _) = ParseHelper.Parse(text);
        Assert.False(d1.HasErrors, ParseHelper.Messages(d1));
        var formatted = SourceFormatter.Format(p1);
        var (p2, d2, _) = ParseHelper.Parse(formatted);
        Assert.False(d2.HasErrors, $"formatted output did not parse:\n{formatted}\n{ParseHelper.Messages(d2)}");
        Assert.Equal(Sx(p1), Sx(p2));
        // The formatter is idempotent.
        Assert.Equal(formatted, SourceFormatter.Format(p2));
    }

    [Theory]
    [InlineData("fizzbuzz.splat")]
    [InlineData("blocks.splat")]
    [InlineData("control_flow.splat")]
    [InlineData("functions.splat")]
    public void GoldenProgramsRoundTrip(string file) =>
        AssertRoundTrips(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", file)));

    [Theory]
    [InlineData("x := (1 + 2) * 3 - (4 - 5) - (6 - (7 - 8))")]
    [InlineData("x := -(2 ** 2) + (-2) ** 2 + 2 ** (3 ** 2) + (2 ** 3) ** 2")]
    [InlineData("x := (a or b) and c or (d and e)")]
    [InlineData("x := (a |> f) >> (g |> h)")]
    [InlineData("x := (1..2) == r and (a + b)..(c * d) != s")]
    [InlineData("x := -(a @ b)' + (-a)' + (a + b)'' + f(x)[1].y(2)")]
    [InlineData("x := (a + b)[0] + (a + b).c + (f)(1)")]
    [InlineData("s := \"quote \\\" brace \\{ back \\\\ nl \\n {x + 1:.2} end\"")]
    [InlineData("s := \"[{f(\"in {name}\")}]\"")]
    [InlineData("c := c\"\\n\" + c\"x\"")]
    [InlineData("f := 3.0 + 1e-4 + 2.5e10 + 0xFF + 0b101 + 1_000")]
    [InlineData("A[:, 1] = B[0, :]")]
    [InlineData("cook f<T: Q, $N>(&me, ~me, x::mid[$N, ?] = zeros(3), g::cook(T)::T)::Maybe<Maybe<T>> = x")]
    [InlineData("hmm a:\n    hmm b:\n        yeet 1\n    nah:\n        yeet 2\n    ;\nmeh c:\n    bail\n;")]
    [InlineData("cook op ==(me, o::V)::bit = yep\ncook op <(me, o::V)::bit = nop\ncook op [](me, i::usz)::mid = 0.0")]
    public void ExpressionAndDeclarationRoundTrips(string text) => AssertRoundTrips(text);
}

public class ParserGoldenFileTests
{
    private static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "golden");

    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    // Set SPLAT_UPDATE_GOLDEN=1 to rewrite the expectation files in the source tree, then review the diff.
    private static void Check(string file, string actual, string ext)
    {
        var expectedName = Path.ChangeExtension(file, ext);
        if (Environment.GetEnvironmentVariable("SPLAT_UPDATE_GOLDEN") == "1")
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "golden"))) dir = dir.Parent;
            File.WriteAllText(Path.Combine(dir!.FullName, "tests", "golden", expectedName), actual);
            return;
        }
        var path = Path.Combine(GoldenDir, expectedName);
        Assert.True(File.Exists(path), $"missing expectation file {expectedName}");
        Assert.Equal(Normalize(File.ReadAllText(path)), Normalize(actual));
    }

    [Theory]
    [InlineData("fizzbuzz.splat")]
    [InlineData("blocks.splat")]
    [InlineData("control_flow.splat")]
    [InlineData("functions.splat")]
    public void ValidProgramsMatchTheirAst(string file)
    {
        var (p, diag, _) = ParseHelper.Parse(File.ReadAllText(Path.Combine(GoldenDir, file)), file);
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        Check(file, AstPrinter.ProgramText(p), ".ast");
    }

    [Theory]
    [InlineData("bad_missing_semicolon.splat")]
    [InlineData("bad_assorted.splat")]
    public void MalformedProgramsMatchTheirDiagnostics(string file)
    {
        var (_, diag, _) = ParseHelper.Parse(File.ReadAllText(Path.Combine(GoldenDir, file)), file);
        Assert.True(diag.HasErrors);
        Check(file, ParseHelper.Render(diag), ".err");
    }
}
