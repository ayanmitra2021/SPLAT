using Splat.Core;
using Splat.Runtime;

namespace Splat.Tests;

public class PipeTests
{
    [Fact]
    public void PipeInsertsTheLeftSideAsTheFirstArgument()
    {
        const string prog = @"
cook double(x::i32)::i32 = x * 2
cook add(a::i32, b::i32)::i32 = a + b
cook sub(a::i32, b::i32)::i32 = a - b
squish 3 |> double |> double
squish 10 |> sub(4)
squish 1 |> add(2) |> double
squish 5 |> |v| v + 1";
        Assert.Equal("12\n6\n6\n6\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void PipeIntoANonFunctionIsAnError() =>
        Assert.Equal([DiagnosticCategory.NotCallable], RunHelper.CompileErrors("x := 5\ny := 3 |> x"));

    [Fact]
    public void PipeChecksArgumentTypes() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("cook f(x::i32)::i32 = x\ny := \"s\" |> f"));
}

public class TemplateAndPreludeTests
{
    [Theory]
    [InlineData("\"{3.14159:.3}\"", "3.142")]
    [InlineData("\"{2.0:.0}\"", "2")]
    [InlineData("\"[{42:6}]\"", "[    42]")]
    [InlineData("\"[{42:<6}]\"", "[42    ]")]
    [InlineData("\"[{42:^6}]\"", "[  42  ]")]
    [InlineData("\"[{42:06}]\"", "[000042]")]
    [InlineData("\"[{0 - 42:06}]\"", "[-00042]")]
    [InlineData("\"{255:x} {255:X} {5:b} {8:o}\"", "ff FF 101 10")]
    [InlineData("\"[{1.5:8.2}]\"", "[    1.50]")]
    [InlineData("\"{yep} {nop} {()}\"", "yep nop ()")]
    [InlineData("\"{1 + 2}-{3 * 3}\"", "3-9")]
    public void FormatSpecs(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Fact]
    public void TextAndCharFormatting()
    {
        Assert.Equal("[ab   ]", RunHelper.Ok("s := \"ab\"\nsquish \"[{s:5}]\"").Trim());
        Assert.Equal("[   ab]", RunHelper.Ok("s := \"ab\"\nsquish \"[{s:>5}]\"").Trim());
        Assert.Equal("z", RunHelper.Ok("c := c\"z\"\nsquish \"{c}\"").Trim());
    }

    [Fact]
    public void EscapedBracesStayLiteral() => Assert.Equal("{x} 5", RunHelper.Eval("\"\\{x\\} {5}\""));

    [Theory]
    [InlineData("x := 1\ny := \"{x:.2}\"")]      // precision on an int
    [InlineData("x := 1.5\ny := \"{x:x}\"")]     // radix on a float
    [InlineData("x := 1\ny := \"{x:q}\"")]       // unknown spec
    [InlineData("x := 1\ny := \"{x:.}\"")]       // precision without digits
    public void BadFormatSpecsAreCompileErrors(string code) =>
        Assert.Equal([DiagnosticCategory.BadFormat], RunHelper.CompileErrors(code));

    [Fact]
    public void UndefinedNameInsideATemplateIsReported() =>
        Assert.Equal([DiagnosticCategory.UndefinedName], RunHelper.CompileErrors("squish \"hi {nobody}\""));

    [Fact]
    public void SquishJoinsArgumentsWithSpacesAndYellGoesToStderr()
    {
        var r = RunHelper.Run("squish 1, \"two\", 3.5, yep\nyell \"oops\"\nsquish()");
        Assert.Equal(RunOutcome.Ok, r.Outcome);
        Assert.Equal("1 two 3.5 yep\n\n", r.Out);
        Assert.Equal("oops\n", r.Err);
    }

    [Fact]
    public void RipPanicsWithTheMessage()
    {
        var r = RunHelper.RuntimeError("squish \"a\"\nrip \"boom {1 + 1}\"\nsquish \"b\"");
        Assert.Equal("a\n", r.Out);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.Panic, d.Category);
        Assert.Contains("boom 2", d.Message);
    }

    [Fact]
    public void RipMessageMustBeTxt() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("rip 5"));

    [Fact]
    public void NotYetBuiltConstructsAreReportedAsHardHatErrors()
    {
        var cats = RunHelper.CompileErrors("x := [1, 2]\ny := x[0]\nz := 1 @ 2");
        Assert.All(cats, c => Assert.Equal(DiagnosticCategory.NotYetBuilt, c));
        Assert.Equal(3, cats.Length);
    }
}

public class Phase5GoldenTests
{
    private static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "golden");

    private static void Compare(string expectedName, string actual)
    {
        if (Environment.GetEnvironmentVariable("SPLAT_UPDATE_GOLDEN") == "1")
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "golden"))) dir = dir.Parent;
            File.WriteAllText(Path.Combine(dir!.FullName, "tests", "golden", expectedName), actual);
            return;
        }
        Assert.Equal(File.ReadAllText(Path.Combine(GoldenDir, expectedName)).Replace("\r\n", "\n"), actual);
    }

    [Theory]
    [InlineData("fizzbuzz.splat")]
    [InlineData("factorial.splat")]
    [InlineData("fibonacci.splat")]
    [InlineData("primes.splat")]
    [InlineData("closure_counter.splat")]
    public void ProgramsRunAndMatchTheirExpectedOutput(string file)
    {
        var r = RunHelper.Run(File.ReadAllText(Path.Combine(GoldenDir, file)), file);
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Compare(Path.ChangeExtension(file, ".out"), r.Out);
    }

    private static readonly string[] BadFiles =
    [
        "bad_types.splat", "bad_runtime_div.splat", "bad_runtime_overflow.splat", "bad_runtime_rip.splat", "bad_runtime_depth.splat",
    ];

    [Theory]
    [InlineData("bad_types.splat", RunOutcome.CompileError)]
    [InlineData("bad_runtime_div.splat", RunOutcome.RuntimeError)]
    [InlineData("bad_runtime_overflow.splat", RunOutcome.RuntimeError)]
    [InlineData("bad_runtime_rip.splat", RunOutcome.RuntimeError)]
    [InlineData("bad_runtime_depth.splat", RunOutcome.RuntimeError)]
    public void BadProgramsProduceTheExpectedDiagnostics(string file, RunOutcome expected)
    {
        var r = RunHelper.Run(File.ReadAllText(Path.Combine(GoldenDir, file)), file);
        Assert.Equal(expected, r.Outcome);
        Compare(Path.ChangeExtension(file, ".err"), r.Rendered);
    }

    [Fact]
    public void EveryDiagnosticHasARegisteredCode()
    {
        foreach (var file in BadFiles)
        {
            var r = RunHelper.Run(File.ReadAllText(Path.Combine(GoldenDir, file)), file);
            Assert.All(r.Diag.Diagnostics, d => Assert.Matches("^SE[0-9]{5}$", d.Code));
            Assert.All(r.Diag.Diagnostics, d => Assert.NotEqual("SE99999", d.Code));
        }
    }
}

// Both CLI test classes redirect the process-wide Console, so they must not run in parallel with each other.
[Collection("Console")]
public class CliRunTests
{
    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var oldOut = Console.Out; var oldErr = Console.Error;
        var so = new StringWriter { NewLine = "\n" }; var se = new StringWriter { NewLine = "\n" };
        Console.SetOut(so); Console.SetError(se);
        try { return (Splat.Cli.Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    private static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "golden", name);

    [Fact]
    public void RunPrintsProgramOutputAndExitsZero()
    {
        var (code, o, _) = Cli("run", Golden("fizzbuzz.splat"));
        Assert.Equal(0, code);
        Assert.StartsWith("1\n2\nFizz\n", o);
    }

    [Fact]
    public void CompileErrorExits65AndPrintsNothingToStdout()
    {
        var (code, o, e) = Cli("run", Golden("bad_types.splat"));
        Assert.Equal(65, code);
        Assert.Equal("", o);
        Assert.Contains("[SE00100]", e);
    }

    [Fact]
    public void RuntimeErrorExits70() => Assert.Equal(70, Cli("run", Golden("bad_runtime_div.splat")).Code);

    [Fact]
    public void CheckReportsDiagnosticsWithoutRunning()
    {
        var (ok, o, _) = Cli("check", Golden("fizzbuzz.splat"));
        Assert.Equal(0, ok);
        Assert.Equal("", o);
        Assert.Equal(65, Cli("check", Golden("bad_types.splat")).Code);
        Assert.Equal(0, Cli("check", Golden("bad_runtime_div.splat")).Code); // runtime errors are not check errors
    }

    [Fact]
    public void MissingFileExits66() => Assert.Equal(66, Cli("run", Golden("does_not_exist.splat")).Code);
}

public class AnnotatedLambdaParserTests
{
    [Fact]
    public void LambdaParametersMayCarryTypeAnnotations()
    {
        Assert.Equal("(lambda (params (x (: i64)) y) (+ x y))", ParseHelper.Expr("|x::i64, y| x + y"));
    }

    [Fact]
    public void AnnotatedLambdaRoundTripsThroughTheFormatter()
    {
        var (p, diag, _) = ParseHelper.Parse("f := |x::i64, y::mid| x\n");
        Assert.False(diag.HasErrors);
        var text = Splat.Syntax.SourceFormatter.Format(p);
        Assert.Contains("|x::i64, y::mid|", text);
    }
}

public class TextIterationTests
{
    [Fact]
    public void SpinOverTxtYieldsChars()
    {
        const string prog = @"
cook count_vowels(text::txt)::i32:
    wag n::i32 = 0
    spin c in text:
        hmm c == c""a"" or c == c""e"" or c == c""i"" or c == c""o"" or c == c""u"":
            n += 1
        ;
    ;
    n
;
squish count_vowels(""hello its haloween!""), count_vowels(""""), count_vowels(""rhythm"")";
        Assert.Equal("7 0 0\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void ComparingACharWithATxtIsStillAnError() =>
        Assert.NotEmpty(RunHelper.CompileErrors("spin c in \"ab\":\n    hmm c == \"a\":\n        bail\n    ;\n;"));
}
