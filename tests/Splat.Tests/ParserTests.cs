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
    [InlineData("x::f32 = 42.0", "(bind x (: f32) 42.0)")]
    [InlineData("var x = 42", "(var x 42)")]
    [InlineData("var x::f32 = 42.0", "(var x (: f32) 42.0)")]
    [InlineData("A::f32[3, 4] = zeros(3, 4)", "(bind A (: (tensor f32 3 4)) (call zeros 3 4))")]
    [InlineData("v::f32[$B, ?] = w", "(bind v (: (tensor f32 $B ?)) w)")]
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
    public void ExpressionBodiedFn() =>
        Assert.Equal("(fn add (params (a i32) (b i32)) (-> i32) (= (+ a b)))",
            ParseHelper.Single("fn add(a::i32, b::i32)::i32 = a + b"));

    [Fact]
    public void BlockBodiedFnWithDefaultsAndNoReturnType() =>
        Assert.Equal("(fn greet (params (who txt \"you\")) (block (call print (str \"hi \" (interp who)))))",
            ParseHelper.Single("fn greet(who::txt = \"you\"):\n    print \"hi {who}\"\n;"));

    [Fact]
    public void GenericsAndReceivers() =>
        Assert.Equal("(fn f (generics T $N U:Trait) (params &self ~self (x T)) (-> (tensor f32 $N)) (= x))",
            ParseHelper.Single("fn f<T, $N, U: Trait>(&self, ~self, x::T)::f32[$N] = x"));

    [Fact]
    public void OperatorOverloads()
    {
        Assert.Equal("(fn op + (params self (o V)) (-> V) (= o))", ParseHelper.Single("fn op +(self, o::V)::V = o"));
        Assert.Equal("(fn op [] (params self (i usize)) (-> f32) (= 0.0))", ParseHelper.Single("fn op [](self, i::usize)::f32 = 0.0"));
        // 'op' followed by '(' is just a function with that name.
        Assert.Equal("(fn op (params (x i32)) (= x))", ParseHelper.Single("fn op(x::i32) = x"));
    }

    [Fact]
    public void FunctionTypeParameter() =>
        Assert.Equal("(fn ap (params (f (fn-type (params i32 i32) i32))) (= f))",
            ParseHelper.Single("fn ap(f::fn(i32, i32)::i32) = f"));

    [Fact]
    public void ClosingNestedGenericsSplitsGreaterGreater() =>
        Assert.Equal("(bind m (: (Maybe (Maybe (Maybe i32)))) x)", ParseHelper.Single("m::Maybe<Maybe<Maybe<i32>>> = x"));

    [Fact]
    public void ClosingGenericsRightBeforeAssignSplitsGreaterEqual() =>
        Assert.Equal("(var m (: (Maybe i32)) (call f))", ParseHelper.Single("var m::Maybe<i32>= f()"));

    [Fact]
    public void LoopInAllThreeForms()
    {
        Assert.Equal("(loop-in i (..= 1 15) (block (call print i)))", ParseHelper.Single("loop i in 1..=15:\n print i\n;"));
        Assert.Equal("(loop-while (< n 3) (block (+= n 1)))", ParseHelper.Single("loop while n < 3:\n n += 1\n;"));
        Assert.Equal("(loop (block (br)))", ParseHelper.Single("loop:\n br\n;"));
    }

    [Fact]
    public void BreakAndContinue() =>
        Assert.Equal("(loop (block (cont) (br)))", ParseHelper.Single("loop:\n cont\n br\n;"));

    [Fact]
    public void NestedBlocksEachCloseWithTheirOwnSemicolon()
    {
        var (p, diag, _) = ParseHelper.Parse("loop:\n loop:\n  br\n ;\n br\n;\nx := 1");
        Assert.False(diag.HasErrors);
        Assert.Equal(2, p.Statements.Count);
        var outer = Assert.IsType<LoopStmt>(p.Statements[0]);
        Assert.Equal(2, outer.Body.Statements.Count);
    }

    [Fact]
    public void DoubleSemicolonClosesTwoBlocks()
    {
        var (p, diag, _) = ParseHelper.Parse("loop:\n loop:\n  br\n;;\nx := 1");
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        Assert.Equal(2, p.Statements.Count);
        var outer = Assert.IsType<LoopStmt>(p.Statements[0]);
        Assert.Single(outer.Body.Statements);
        Assert.IsType<LoopStmt>(outer.Body.Statements[0]);
        Assert.IsType<BindStmt>(p.Statements[1]);
    }

    [Fact]
    public void TripleSemicolonClosesThreeBlocks()
    {
        var (p, diag, _) = ParseHelper.Parse("fn f():\n loop:\n  loop:\n   br\n;;;\nx := 1");
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        Assert.Equal(2, p.Statements.Count);
    }

    [Fact]
    public void IfChainSharesOneClosingSemicolon()
    {
        var (p, diag, _) = ParseHelper.Parse("if a:\n x := 1\nelif b:\n x := 2\nelif c:\n x := 3\nel:\n x := 4\n;\ny := 5");
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        Assert.Equal(2, p.Statements.Count);
        var h = Assert.IsType<IfStmt>(p.Statements[0]);
        Assert.Equal(3, h.Branches.Count);
        Assert.NotNull(h.Else);
        Assert.IsType<BindStmt>(p.Statements[1]); // 'y' is outside the chain
    }

    [Fact]
    public void IfWithoutElse() =>
        Assert.Equal("(if (if (> x 0) (block (ret 1))))", ParseHelper.Single("if x > 0:\n ret 1\n;"));

    [Fact]
    public void NestedIfEachNeedItsOwnCloser()
    {
        Assert.Equal("(if (if a (block (if (if b (block (ret 1))) (else (block (ret 2)))))))",
            ParseHelper.Single("if a:\n if b:\n  ret 1\n el:\n  ret 2\n ;\n;"));
    }

    [Fact]
    public void InnerIfTakesTheElifThatFollowsIt()
    {
        // Without an inner ';', 'elif' binds to the inner chain (dangling-else rule); the file then runs out of ';'.
        var (_, diag, _) = ParseHelper.Parse("if a:\n if b:\n  x := 1\nelif c:\n  x := 2\n;");
        Assert.Equal([DiagnosticCategory.MissingSemicolon], ParseHelper.Categories(diag));
    }

    [Fact]
    public void ReturnWithAndWithoutValue()
    {
        Assert.Equal("(ret (+ 1 2))", ParseHelper.Single("ret 1 + 2"));
        Assert.Equal("(fn f (params) (block (if (if true (block (ret))))))", ParseHelper.Single("fn f():\n if true:\n  ret\n ;\n;"));
    }

    [Fact]
    public void ReturnValueMustStartOnTheSameLine() =>
        Assert.Equal("(fn f (params) (block (ret) (bind x 1)))", ParseHelper.Single("fn f():\n ret\n x := 1\n;"));

    [Fact]
    public void RipPanics()
    {
        Assert.Equal("(rip \"boom\")", ParseHelper.Single("rip \"boom\""));
        Assert.Equal("(rip)", ParseHelper.Single("rip"));
    }

    [Fact]
    public void CommandCallWithoutParentheses()
    {
        Assert.Equal("(call print (str \"Got \" (interp val)))", ParseHelper.Single("print \"Got {val}\""));
        Assert.Equal("(call print a b (+ c 1))", ParseHelper.Single("print a, b, c + 1"));
        Assert.Equal("(call eprint \"bad\")", ParseHelper.Single("eprint \"bad\""));
        Assert.Equal("(bind name (call readl \"name? \"))", ParseHelper.Single("name := readl(\"name? \")"));
    }

    [Fact]
    public void ParenthesisedCallsAlwaysWork()
    {
        Assert.Equal("(call print \"hi\")", ParseHelper.Single("print(\"hi\")"));
        Assert.Equal("(call print \"hi\")", ParseHelper.Single("print (\"hi\")"));
    }

    [Fact]
    public void CommandCallArgumentMustBeOnTheSameLine() =>
        Assert.Equal(["print", "(bind x 1)"], ParseHelper.Statements("print\nx := 1"));

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
        Assert.Equal("(call print (str \"[\" (interp (call f (str \"n \" (interp name)))) \"]\"))",
            ParseHelper.Single("print \"[{f(\"n {name}\")}]\""));

    [Fact]
    public void ConstructsOfLaterMilestonesAreSkippedWithoutDerailingTheParse()
    {
        var src = "class Dog from Animal with Talkable:\n fn bark(self):\n  if a:\n   x := 1\n  elif b:\n   x := 2\n  ;\n ;\n;\nafter := 1";
        var (p, diag, _) = ParseHelper.Parse(src);
        Assert.Equal([DiagnosticCategory.NotYetBuilt], ParseHelper.Categories(diag));
        Assert.Equal(2, p.Statements.Count);
        Assert.IsType<ReservedStmt>(p.Statements[0]);
        Assert.IsType<BindStmt>(p.Statements[1]);
    }

    [Fact]
    public void ReservedConstructWithTraitBoundInGenerics()
    {
        var (p, diag, _) = ParseHelper.Parse("network Net<$In, T: Trait>:\n lin(In, 4)\n;\nok := 1");
        Assert.Equal([DiagnosticCategory.NotYetBuilt], ParseHelper.Categories(diag));
        Assert.Equal(2, p.Statements.Count);
    }

    [Fact]
    public void ReservedLineStatementsSkipOnlyTheirLine()
    {
        var (p, diag, _) = ParseHelper.Parse("drop x\ny := 1");
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
        var (_, diag, _) = ParseHelper.Parse("fn f(n::i32)::i32:\n    ret n\n");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.MissingSemicolon, d.Category);
        Assert.Equal(1, d.Span.Line);
        Assert.Equal(18, d.Span.Col);
        Assert.Equal(1, d.Span.Length);
        Assert.Contains("💀 OOF [Line 1]", d.Message);
    }

    [Fact]
    public void EveryUnclosedNestedBlockIsReported()
    {
        var (_, diag, _) = ParseHelper.Parse("loop:\n    loop:\n        br\n");
        Assert.Equal([DiagnosticCategory.MissingSemicolon, DiagnosticCategory.MissingSemicolon], ParseHelper.Categories(diag));
        Assert.Equal([2, 1], diag.Diagnostics.Select(d => d.Span.Line).ToArray()); // innermost first
    }

    [Fact]
    public void UnclosedIfChainPointsAtTheLastOpenBranch()
    {
        var (_, diag, _) = ParseHelper.Parse("if a:\n x := 1\nel:\n x := 2\n");
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
    public void OrphanElifIsReportedOnceWithoutCascade()
    {
        var (p, diag, _) = ParseHelper.Parse("elif x > 1:\n    print \"hi\"\n;\ny := 2");
        Assert.Equal([DiagnosticCategory.OrphanBranch], ParseHelper.Categories(diag));
        Assert.IsType<BindStmt>(p.Statements[^1]);
    }

    [Fact]
    public void MissingColonAfterIfConditionStillParsesTheBody()
    {
        var (p, diag, _) = ParseHelper.Parse("if x\n print \"a\"\n;\ny := 1");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.UnexpectedToken, d.Category);
        Assert.Equal(2, p.Statements.Count);
    }

    [Fact]
    public void VarWithColonAssignGetsATargetedMessage()
    {
        var (_, diag, _) = ParseHelper.Parse("var x := 1");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("'var' variables use '='", d.Message);
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
    public void FnWithoutBody()
    {
        var (_, diag, _) = ParseHelper.Parse("fn f(x::i32)::i32\ny := 1");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("body of 'f'", d.Message + d.Fix);
    }

    [Fact]
    public void ParameterWithoutType()
    {
        var (_, diag, _) = ParseHelper.Parse("fn f(x) = x");
        Assert.Contains("'::Type' after parameter 'x'", diag.Diagnostics[0].Message);
    }

    [Fact]
    public void BadLoopHeader()
    {
        var (_, diag, _) = ParseHelper.Parse("loop 5:\n br\n;");
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
        var src = "a := + 1\nb := 2\nc := (3\nd := 4\nelif d > 1:\n;\nloop:\n br\n";
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

        var (_, diag2, _) = ParseHelper.Parse(string.Concat(Enumerable.Repeat("loop:\n", 3000)));
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
        string[] pieces = [":", ";", ";;", "if", "elif", "el", "loop", "fn", "(", ")", "[", "]", "x", "1", "\"s{", "}", "::", ":=", "=", "ret", "var", "|", "'", ",", "class", "pick", ".", "..", "@", "->"];
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
    [InlineData("true and false", "(and true false)")]
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
    [InlineData("fn f<T: Q, $N>(&self, ~self, x::f32[$N, ?] = zeros(3), g::fn(T)::T)::Maybe<Maybe<T>> = x")]
    [InlineData("if a:\n    if b:\n        ret 1\n    el:\n        ret 2\n    ;\nelif c:\n    br\n;")]
    [InlineData("fn op ==(self, o::V)::bool = true\nfn op <(self, o::V)::bool = false\nfn op [](self, i::usize)::f32 = 0.0")]
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
