using Splat.Core;
using Splat.Syntax;

namespace Splat.Tests;

/// <summary>Phase 4: the full spec 8.1 operator table, lambdas, norms, calculus, reductions and regress/classify.</summary>
public class PrattPrecedenceTests
{
    // Together with ParserExpressionTests.Precedence (38 cases) this is the 60+ case precedence table.
    [Theory]
    // level 1/2: postfix binds tighter than everything
    [InlineData("A'' @ B'", "(@ ('' A) (' B))")]
    [InlineData("A[0]'", "(' (index A 0))")]
    [InlineData("a.b'", "(' (. a b))")]
    [InlineData("f(x).y[0]", "(index (. (call f x) y) 0)")]
    [InlineData("-a.b", "(- (. a b))")]
    [InlineData("&a[0]", "(& (index a 0))")]
    [InlineData("~a.b", "(~ (. a b))")]
    // level 3: ** vs unary, postfix and itself
    [InlineData("-a ** b", "(- (** a b))")]
    [InlineData("!a ** b", "(! (** a b))")]
    [InlineData("a' ** 2", "(** (' a) 2)")]
    [InlineData("a ** b ** c ** d", "(** a (** b (** c d)))")]
    [InlineData("a ** -b ** c", "(** a (- (** b c)))")]
    [InlineData("(-2) ** 2", "(** (- 2) 2)")]
    [InlineData("2 ** 3 * 4", "(* (** 2 3) 4)")]
    [InlineData("a * b ** 2", "(* a (** b 2))")]
    // level 4: unary stacking
    [InlineData("--a", "(- (- a))")]
    [InlineData("!!a", "(! (! a))")]
    [InlineData("-!a", "(- (! a))")]
    [InlineData("&~a", "(& (~ a))")]
    [InlineData("-a + b", "(+ (- a) b)")]
    [InlineData("-a * -b", "(* (- a) (- b))")]
    [InlineData("!a == b", "(== (! a) b)")]
    // level 5 vs 6 vs 7
    [InlineData("A @ B @ C", "(@ (@ A B) C)")]
    [InlineData("A @ B * c", "(* (@ A B) c)")]
    [InlineData("a * B @ C", "(* a (@ B C))")]
    [InlineData("a + B @ C", "(+ a (@ B C))")]
    [InlineData("A .* B ./ C", "(./ (.* A B) C)")]
    [InlineData("A ./ B .* C", "(.* (./ A B) C)")]
    [InlineData("u · v * 2", "(* (· u v) 2)")]
    [InlineData("A \\ b + c", "(+ (\\ A b) c)")]
    [InlineData("A \\ B @ C", "(@ (\\ A B) C)")]
    [InlineData("a .+ b .- c", "(.- (.+ a b) c)")]
    [InlineData("a .+ b + c", "(+ (.+ a b) c)")]
    [InlineData("a - b .- c", "(- a (.- b c))")]
    [InlineData("a / b * c % d", "(% (* (/ a b) c) d)")]
    [InlineData("a + b - c + d", "(+ (- (+ a b) c) d)")]
    // level 8: ranges sit between additive and comparison
    [InlineData("a..b + 1", "(.. a (+ b 1))")]
    [InlineData("0..n * 2", "(.. 0 (* n 2))")]
    [InlineData("0..n == r", "(== (.. 0 n) r)")]
    [InlineData("1..=n |> f", "(|> (..= 1 n) f)")]
    // level 9: comparisons are left-associative and tighter than and/or
    [InlineData("a < b < c", "(< (< a b) c)")]
    [InlineData("a <= b or c >= d", "(or (<= a b) (>= c d))")]
    [InlineData("a != b and c == d or e", "(or (and (!= a b) (== c d)) e)")]
    [InlineData("a + 1 < b * 2", "(< (+ a 1) (* b 2))")]
    // level 10 and 11
    [InlineData("a or b or c", "(or (or a b) c)")]
    [InlineData("a and b and c", "(and (and a b) c)")]
    [InlineData("a or b |> f", "(|> (or a b) f)")]
    [InlineData("f >> g >> h", "(>> (>> f g) h)")]
    [InlineData("lin(1, 2) >> gelu >> lin(2, 3)", "(>> (>> (call lin 1 2) gelu) (call lin 2 3))")]
    [InlineData("x |> f(1) |> g", "(|> (|> x (call f 1)) g)")]
    // calls, named arguments, lambda-valued arguments
    [InlineData("f()", "(call f)")]
    [InlineData("f(a, b = 2, c = |x| x)", "(call f a (arg b 2) (arg c (lambda (params x) x)))")]
    [InlineData("map(xs, |v| v + 1)", "(call map xs (lambda (params v) (+ v 1)))")]
    [InlineData("f(lr = 1e-3)", "(call f (arg lr 0.001))")]
    [InlineData("f(a == b)", "(call f (== a b))")]
    [InlineData("f(&x, ~y)", "(call f (& x) (~ y))")]
    // slices
    [InlineData("A[i]", "(index A i)")]
    [InlineData("A[:, 0]", "(index A : 0)")]
    [InlineData("A[0, :]", "(index A 0 :)")]
    [InlineData("A[:, :]", "(index A : :)")]
    [InlineData("A[i + 1, j * 2]", "(index A (+ i 1) (* j 2))")]
    [InlineData("A[0..2, 1..=3]", "(index A (.. 0 2) (..= 1 3))")]
    [InlineData("A[:, 0..3] @ B", "(@ (index A : (.. 0 3)) B)")]
    public void Precedence(string src, string expected) => Assert.Equal(expected, ParseHelper.Expr(src));

    [Fact]
    public void TableHasAtLeastSixtyCases()
    {
        int Count(Type t, string method) => t.GetMethod(method)!
            .GetCustomAttributes(typeof(Xunit.InlineDataAttribute), false).Length;
        int total = Count(typeof(PrattPrecedenceTests), nameof(Precedence))
                    + Count(typeof(ParserExpressionTests), nameof(ParserExpressionTests.Precedence))
                    + Count(typeof(LambdaAndNormTests), nameof(LambdaAndNormTests.Forms))
                    + Count(typeof(CalculusAndAiTests), nameof(CalculusAndAiTests.Forms));
        Assert.True(total >= 60, $"only {total} cases");
    }
}

public class LambdaAndNormTests
{
    [Theory]
    [InlineData("|x| x", "(lambda (params x) x)")]
    [InlineData("|a, b| a + b", "(lambda (params a b) (+ a b))")]
    [InlineData("| | 1", "(lambda (params) 1)")]
    [InlineData("|x| x |> f", "(lambda (params x) (|> x f))")]
    [InlineData("|x| |y| x + y", "(lambda (params x) (lambda (params y) (+ x y)))")]
    [InlineData("|x| x or y", "(lambda (params x) (or x y))")]
    [InlineData("|x| ||x||", "(lambda (params x) (norm 2 x))")]
    [InlineData("|v| v ** 2", "(lambda (params v) (** v 2))")]
    [InlineData("x |> |a| a + 1", "(|> x (lambda (params a) (+ a 1)))")]
    [InlineData("||v||", "(norm 2 v)")]
    [InlineData("||v||_1", "(norm 1 v)")]
    [InlineData("||v||_2", "(norm 2 v)")]
    [InlineData("||v||_inf", "(norm inf v)")]
    [InlineData("||A||_F", "(norm F A)")]
    [InlineData("||A||_F ** 2", "(** (norm F A) 2)")]
    [InlineData("||u - v||", "(norm 2 (- u v))")]
    [InlineData("||a @ b|| + 1", "(+ (norm 2 (@ a b)) 1)")]
    [InlineData("||a|| or ||b||", "(or (norm 2 a) (norm 2 b))")]
    [InlineData("||a|| * ||b||", "(* (norm 2 a) (norm 2 b))")]
    [InlineData("||a|| < ||b||_1", "(< (norm 2 a) (norm 1 b))")]
    [InlineData("||f(x)||", "(norm 2 (call f x))")]
    [InlineData("||A'||", "(norm 2 (' A))")]
    [InlineData("||A[0, :]||_inf", "(norm inf (index A 0 :))")]
    [InlineData("-||v||", "(- (norm 2 v))")]
    [InlineData("||||x||||", "(norm 2 (norm 2 x))")]
    [InlineData("|| ||x|| ||", "(norm 2 (norm 2 x))")]
    public void Forms(string src, string expected) => Assert.Equal(expected, ParseHelper.Expr(src));

    [Fact]
    public void LambdaBarsAreNotNormBarsWhenSeparatedByASpace()
    {
        // `| | e` is the zero-parameter lambda; `||e||` is a norm. The only difference is the space.
        Assert.StartsWith("(lambda", ParseHelper.Expr("| | v"));
        Assert.StartsWith("(norm", ParseHelper.Expr("||v||"));
    }

    [Fact]
    public void NormSuffixMustBeGluedToTheClosingBars()
    {
        // With a space, `_1` is just a name that follows a complete norm, which is not valid here.
        var diag = new DiagnosticEngine();
        var src = new SourceText("t.splat", "||v|| _1");
        Parser.ParseExpression(src, diag);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void NormsAndLambdasKeepTheirSpans()
    {
        var src = new SourceText("t.splat", "x := ||u - v||_inf");
        var diag = new DiagnosticEngine();
        var p = Parser.ParseProgram(src, diag);
        var norm = (NormExpr)((BindStmt)p.Statements[0]).Value;
        Assert.Equal(6, norm.Span.Col);
        Assert.Equal("||u - v||_inf".Length, norm.Span.Length);
    }
}

public class CalculusAndAiTests
{
    [Theory]
    [InlineData("∂(f, x)", "(grad f x)")]
    [InlineData("grad(f, x)", "(grad f x)")]
    [InlineData("grad(|t| t ** 3, w)", "(grad (lambda (params t) (** t 3)) w)")]
    [InlineData("∂(f, x) * 2", "(* (grad f x) 2)")]
    [InlineData("-∂(f, x)", "(- (grad f x))")]
    [InlineData("∂(f, x)[0]", "(index (grad f x) 0)")]
    [InlineData("∫[0..1] |x| x ** 2", "(intg (ranges (.. 0 1)) (lambda (params x) (** x 2)))")]
    [InlineData("intg[0..3] |x| sin(x)", "(intg (ranges (.. 0 3)) (lambda (params x) (call sin x)))")]
    [InlineData("∫[0..1, 0..2] |x, y| x * y", "(intg (ranges (.. 0 1) (.. 0 2)) (lambda (params x y) (* x y)))")]
    [InlineData("∫[0..1, tol = 1e-6] |x| x", "(intg (ranges (.. 0 1)) (opts (arg tol 1E-06)) (lambda (params x) x))")]
    [InlineData("∫[a..=b] |x| x + 1", "(intg (ranges (..= a b)) (lambda (params x) (+ x 1)))")]
    [InlineData("∑[i in 0..N] i", "(sum i (.. 0 N) i)")]
    [InlineData("sum[i in 0..N] i * i", "(sum i (.. 0 N) (* i i))")]
    [InlineData("∏[k in 1..=n] k", "(prod k (..= 1 n) k)")]
    [InlineData("prod[k in 1..=n] k", "(prod k (..= 1 n) k)")]
    [InlineData("∑[i in 0..N] A[i] * B[i]", "(sum i (.. 0 N) (* (index A i) (index B i)))")]
    [InlineData("∑[i in 0..N] i + 1", "(sum i (.. 0 N) (+ i 1))")]
    [InlineData("∑[i in 0..N] i == 6", "(== (sum i (.. 0 N) i) 6)")]
    [InlineData("2 * (∑[i in 0..3] i) + 1", "(+ (* 2 (sum i (.. 0 3) i)) 1)")]
    [InlineData("∑[i in 0..n] ∑[j in 0..m] A[i, j]", "(sum i (.. 0 n) (sum j (.. 0 m) (index A i j)))")]
    [InlineData("∑[i in xs] i", "(sum i xs i)")]
    [InlineData("sum(v) + prod(w)", "(+ (call sum v) (call prod w))")]
    [InlineData("map(xs, sum)", "(call map xs sum)")]
    [InlineData("regress(ridge = 0.05) fit (X, y)", "(regress (config (arg ridge 0.05)) X y)")]
    [InlineData("regress(linear) fit (X, y)", "(regress (config linear) X y)")]
    [InlineData("regress() fit (X, y)", "(regress (config) X y)")]
    [InlineData("classify(svm(kernel = \"rbf\")) fit (X, y)", "(classify (config (call svm (arg kernel \"rbf\"))) X y)")]
    [InlineData("classify(knn(k = 5)) fit (X[:, 0..2], y)", "(classify (config (call knn (arg k 5))) (index X : (.. 0 2)) y)")]
    [InlineData("model(&X_test)", "(call model (& X_test))")]
    [InlineData("regress(ridge = 0.1) fit (X, y)(&Z)", "(call (regress (config (arg ridge 0.1)) X y) (& Z))")]
    [InlineData("clf.acc(&X_test, &y_test)", "(call (. clf acc) (& X_test) (& y_test))")]
    [InlineData("\"{a + b:.3} and {f(x)}\"",
        "(str (interp (+ a b) \".3\") \" and \" (interp (call f x)))")]
    [InlineData("\"{||v||:.2}\"", "(str (interp (norm 2 v) \".2\"))")]
    public void Forms(string src, string expected) => Assert.Equal(expected, ParseHelper.Expr(src));

    [Fact]
    public void KeywordSpellingsAreInterchangeable()
    {
        Assert.Equal(ParseHelper.Expr("∑[i in 0..N] i"), ParseHelper.Expr("sum[i in 0..N] i"));
        Assert.Equal(ParseHelper.Expr("∏[i in 0..N] i"), ParseHelper.Expr("prod[i in 0..N] i"));
        Assert.Equal(ParseHelper.Expr("∫[0..1] |x| x"), ParseHelper.Expr("intg[0..1] |x| x"));
        Assert.Equal(ParseHelper.Expr("∂(f, x)"), ParseHelper.Expr("grad(f, x)"));
    }

    [Fact]
    public void TemplateInterpolationSpansPointIntoTheFile()
    {
        var src = new SourceText("t.splat", "s := \"ab {x + ||v||} cd\"");
        var diag = new DiagnosticEngine();
        var p = Parser.ParseProgram(src, diag);
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));
        var t = (StringTemplate)((BindStmt)p.Statements[0]).Value;
        var inner = (BinaryExpr)((TemplateExpr)t.Parts[1]).Value;
        Assert.Equal(1, inner.Left.Span.Line);
        Assert.Equal(11, inner.Left.Span.Col); // the 'x'
        Assert.Equal(15, inner.Right.Span.Col); // the opening '||'
    }

    [Fact]
    public void ReservedCalculusWordsStillWorkAsPlainNames()
    {
        Assert.Equal("(bind s (call sum xs))", ParseHelper.Single("s := sum(xs)"));
        Assert.Equal("(bind g grad)", ParseHelper.Single("g := grad"));
    }
}

public class ExpressionErrorTests
{
    private static DiagnosticEngine ParseExprDiag(string text)
    {
        var diag = new DiagnosticEngine();
        var src = new SourceText("t.splat", text);
        diag.AddSource(src);
        Parser.ParseExpression(src, diag);
        return diag;
    }

    [Theory]
    [InlineData("||x")]               // missing closing bars
    [InlineData("|x")]                // unterminated lambda parameter list
    [InlineData("||x||_zz")]          // unknown norm kind
    [InlineData("∑[i 0..3] i")]       // missing 'in'
    [InlineData("∑[i in 0..3]")]      // missing body
    [InlineData("∫[0..1] x")]         // missing lambda
    [InlineData("∫[x] |x| x")]        // not a range
    [InlineData("∫[] |x| x")]         // no ranges
    [InlineData("∂(f)")]              // missing wrt
    [InlineData("grad(f, x")]         // missing paren
    [InlineData("regress(ridge = 1) (X, y)")] // missing 'fit'
    [InlineData("regress(ridge = 1) fit (X)")] // missing target
    [InlineData("classify fit (X, y)")]  // missing config
    [InlineData("1..2..3")]           // ranges do not chain
    public void MalformedFormsReportAnError(string text)
    {
        var diag = ParseExprDiag(text);
        Assert.True(diag.HasErrors, $"expected an error for: {text}");
    }

    [Fact]
    public void BadExpressionsGoldenProducesOneDiagnosticPerBrokenStatement()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "bad_exprs.splat"));
        var (_, diag, _) = ParseHelper.Parse(text, "bad_exprs.splat");
        Assert.True(diag.Diagnostics.Count >= 6, ParseHelper.Render(diag));
    }
}

public class Phase4RoundTripTests
{
    private static void AssertRoundTrips(string text)
    {
        var (p1, d1, _) = ParseHelper.Parse(text);
        Assert.False(d1.HasErrors, ParseHelper.Messages(d1));
        var formatted = SourceFormatter.Format(p1);
        var (p2, d2, _) = ParseHelper.Parse(formatted);
        Assert.False(d2.HasErrors, $"formatted output did not parse:\n{formatted}\n{ParseHelper.Messages(d2)}");
        Assert.Equal(AstPrinter.ProgramText(p1), AstPrinter.ProgramText(p2));
        Assert.Equal(formatted, SourceFormatter.Format(p2));
    }

    [Theory]
    [InlineData("x := (|a| a + 1) + 2")]
    [InlineData("x := f(|a, b| a * b, 3) |> g")]
    [InlineData("x := (|v| v) |> (|v| v)")]
    [InlineData("x := ||||v|| + 1|| + ||v||_1 * ||A||_F")]
    [InlineData("x := (||a|| or ||b||) and ||c||_inf < 2")]
    [InlineData("x := 2 * (∑[i in 0..3] i) + 1")]
    [InlineData("x := (∑[i in 0..3] i) * 2")]
    [InlineData("x := ∑[i in 0..3] (i == 2)")]
    [InlineData("x := ∑[i in 0..n] ∏[j in 0..m] i + j")]
    [InlineData("x := -(∑[i in 0..3] i) ** 2")]
    [InlineData("x := ∫[0..1, 0..2, tol = 1e-6] |x, y| x * y")]
    [InlineData("x := (∫[0..1] |t| t) + 1")]
    [InlineData("x := ∂(|t| t ** 3, w)[0]")]
    [InlineData("x := regress(ridge = 0.05) fit (X[:, 0..3], y)")]
    [InlineData("x := classify(svm(kernel = \"rbf\")) fit (X, y)(&Z)")]
    [InlineData("x := \"{a + ||v||:.3} {sum[i in 0..3] i}\"")]
    [InlineData("x := (a..b)(1)")]
    public void FormatsAndReparsesToTheSameTree(string text) => AssertRoundTrips(text);
}

public class Phase4GoldenTests
{
    private static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "golden");

    [Theory]
    [InlineData("lambdas_pipes.splat")]
    [InlineData("calculus.splat")]
    [InlineData("ai_fit.splat")]
    [InlineData("tensors.splat")]
    [InlineData("strings.splat")]
    public void ProgramsParseCleanlyAndMatchTheirAst(string file)
    {
        var (p, diag, _) = ParseHelper.Parse(File.ReadAllText(Path.Combine(GoldenDir, file)), file);
        Assert.False(diag.HasErrors, ParseHelper.Messages(diag));

        var actual = AstPrinter.ProgramText(p);
        var expectedPath = Path.Combine(GoldenDir, Path.ChangeExtension(file, ".ast"));
        if (Environment.GetEnvironmentVariable("SPLAT_UPDATE_GOLDEN") == "1")
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "golden"))) dir = dir.Parent;
            File.WriteAllText(Path.Combine(dir!.FullName, "tests", "golden", Path.ChangeExtension(file, ".ast")), actual);
            return;
        }
        Assert.True(File.Exists(expectedPath), $"missing expectation file {Path.GetFileName(expectedPath)}");
        Assert.Equal(File.ReadAllText(expectedPath).Replace("\r\n", "\n"), actual.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("lambdas_pipes.splat")]
    [InlineData("calculus.splat")]
    [InlineData("ai_fit.splat")]
    public void ProgramsRoundTripThroughTheFormatter(string file)
    {
        var text = File.ReadAllText(Path.Combine(GoldenDir, file));
        var (p1, d1, _) = ParseHelper.Parse(text);
        Assert.False(d1.HasErrors, ParseHelper.Messages(d1));
        var formatted = SourceFormatter.Format(p1);
        var (p2, d2, _) = ParseHelper.Parse(formatted);
        Assert.False(d2.HasErrors, formatted + "\n" + ParseHelper.Messages(d2));
        Assert.Equal(AstPrinter.ProgramText(p1), AstPrinter.ProgramText(p2));
    }

    [Fact]
    public void BadExpressionsMatchTheirDiagnostics()
    {
        const string file = "bad_exprs.splat";
        var (_, diag, _) = ParseHelper.Parse(File.ReadAllText(Path.Combine(GoldenDir, file)), file);
        Assert.True(diag.HasErrors);
        var actual = ParseHelper.Render(diag);
        var expectedPath = Path.Combine(GoldenDir, "bad_exprs.err");
        if (Environment.GetEnvironmentVariable("SPLAT_UPDATE_GOLDEN") == "1")
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "golden"))) dir = dir.Parent;
            File.WriteAllText(Path.Combine(dir!.FullName, "tests", "golden", "bad_exprs.err"), actual);
            return;
        }
        Assert.True(File.Exists(expectedPath), "missing expectation file bad_exprs.err");
        Assert.Equal(File.ReadAllText(expectedPath).Replace("\r\n", "\n"), actual.Replace("\r\n", "\n"));
    }
}
