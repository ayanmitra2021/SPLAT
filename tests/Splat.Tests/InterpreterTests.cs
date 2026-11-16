using Splat.Core;
using Splat.Runtime;

namespace Splat.Tests;

internal static class RunHelper
{
    public sealed record Result(RunOutcome Outcome, string Out, string Err, DiagnosticEngine Diag)
    {
        public DiagnosticCategory[] Categories => Diag.Diagnostics.Select(d => d.Category).ToArray();
        public string Rendered
        {
            get
            {
                var w = new StringWriter { NewLine = "\n" };
                new ConsolePrinter(w, useColor: false).Print(Diag);
                return w.ToString();
            }
        }
    }

    public static Result Run(string code, string file = "t.splat")
    {
        var diag = new DiagnosticEngine();
        var so = new StringWriter { NewLine = "\n" };
        var se = new StringWriter { NewLine = "\n" };
        var outcome = Pipeline.Run(new SourceText(file, code), diag, so, se);
        return new Result(outcome, so.ToString(), se.ToString(), diag);
    }

    /// <summary>Runs a program that must succeed and returns its stdout.</summary>
    public static string Ok(string code)
    {
        var r = Run(code);
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        return r.Out;
    }

    /// <summary>Evaluates one expression through squish and returns what it printed.</summary>
    public static string Eval(string expr) => Ok($"squish({expr})").TrimEnd('\n');

    public static DiagnosticCategory[] CompileErrors(string code)
    {
        var r = Run(code);
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal("", r.Out); // nothing runs when the checker rejects the program
        return r.Categories;
    }

    public static Result RuntimeError(string code)
    {
        var r = Run(code);
        Assert.Equal(RunOutcome.RuntimeError, r.Outcome);
        return r;
    }
}

public class ArithmeticTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("10 - 4 - 3", "3")]
    [InlineData("7 / 2", "3")]
    [InlineData("-7 / 2", "-3")]
    [InlineData("7 % 3", "1")]
    [InlineData("-7 % 3", "-1")]
    [InlineData("2 ** 10", "1024")]
    [InlineData("2 ** 3 ** 2", "512")]
    [InlineData("-2 ** 2", "-4")]
    [InlineData("1_000 + 0xFF + 0b101", "1260")]
    [InlineData("1.5 + 2.25", "3.75")]
    [InlineData("7.0 / 2.0", "3.5")]
    [InlineData("3.0", "3.0")]
    [InlineData("2.0 ** 0.5 > 1.41", "yep")]
    [InlineData("0.1 + 0.2 == 0.3", "yep")]
    [InlineData("1.0 / 0.0", "inf")]
    [InlineData("-1.0 / 0.0", "-inf")]
    public void Expressions(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Theory]
    [InlineData("1 < 2", "yep")]
    [InlineData("2 <= 2", "yep")]
    [InlineData("3 > 4", "nop")]
    [InlineData("3 != 4", "yep")]
    [InlineData("\"abc\" < \"abd\"", "yep")]
    [InlineData("\"b\" > \"a\"", "yep")]
    [InlineData("\"x\" == \"x\"", "yep")]
    [InlineData("c\"a\" < c\"b\"", "yep")]
    [InlineData("yep == nop", "nop")]
    [InlineData("() == ()", "yep")]
    [InlineData("1 + 1 == 2 and 2 + 2 == 4", "yep")]
    [InlineData("1 > 2 or 3 > 2", "yep")]
    [InlineData("!(1 > 2)", "yep")]
    [InlineData("yep or nop and nop", "yep")]
    public void ComparisonsAndBooleans(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Fact]
    public void AndOrShortCircuit()
    {
        // The right-hand side would divide by zero if it ran.
        Assert.Equal("nop", RunHelper.Eval("nop and 1 / 0 == 1"));
        Assert.Equal("yep", RunHelper.Eval("yep or 1 / 0 == 1"));
    }

    [Fact]
    public void TextConcatenation() => Assert.Equal("ab", RunHelper.Eval("\"a\" + \"b\""));

    [Theory]
    [InlineData("i8", "127", "-128")]
    [InlineData("i16", "32767", "-32768")]
    [InlineData("i32", "2147483647", "-2147483648")]
    [InlineData("i64", "9223372036854775807", "-9223372036854775808")]
    public void SignedLimitsAreRepresentable(string type, string max, string min)
    {
        Assert.Equal(max, RunHelper.Eval($"{type}({max})"));
        Assert.Equal(min, RunHelper.Eval($"{type}({min})"));
    }

    [Fact]
    public void UnsignedLimits()
    {
        Assert.Equal("255", RunHelper.Eval("u8(255)"));
        Assert.Equal("18446744073709551615", RunHelper.Ok("x::u64 = 18446744073709551615\nsquish x").Trim());
        Assert.Equal("18446744073709551615", RunHelper.Ok("x::usz = 18446744073709551615\nsquish x").Trim());
    }

    [Theory]
    [InlineData("x::i8 = 127\nsquish x + 1", "i8")]
    [InlineData("x::u8 = 0\nsquish x - 1", "u8")]
    [InlineData("x::i32 = 2147483647\nsquish x + 1", "i32")]
    [InlineData("x::i32 = 65536\nsquish x * x", "i32")]
    [InlineData("x::i64 = 9223372036854775807\nsquish x + 1", "i64")]
    [InlineData("x::u64 = 18446744073709551615\nsquish x + 1", "u64")]
    [InlineData("x::i32 = 2\nsquish x ** 31", "i32")]
    [InlineData("x::i64 = 3\nsquish x ** 100", "i64")]
    public void OverflowIsCheckedNeverWrapping(string code, string type)
    {
        var r = RunHelper.RuntimeError(code);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.IntegerOverflow, d.Category);
        Assert.Contains($"'{type}'", d.Message);
    }

    [Fact]
    public void NegatingTheMinimumOverflows()
    {
        var r = RunHelper.RuntimeError("x::i8 = -128\nsquish(-x)");
        Assert.Equal(DiagnosticCategory.IntegerOverflow, r.Diag.Diagnostics[0].Category);
    }

    [Fact]
    public void PowerEdgeCases()
    {
        Assert.Equal("1", RunHelper.Eval("0 ** 0"));
        Assert.Equal("0", RunHelper.Eval("0 ** 5"));
        Assert.Equal("1", RunHelper.Eval("1 ** 1000000"));
        Assert.Equal("-8", RunHelper.Eval("(-2) ** 3"));
        Assert.Equal("-9223372036854775808", RunHelper.Ok("x::i64 = -2\nsquish x ** 63").Trim());
        var r = RunHelper.RuntimeError("squish 2 ** (0 - 1)");
        Assert.Equal(DiagnosticCategory.MathDomain, r.Diag.Diagnostics[0].Category);
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("5 % 0")]
    public void IntegerDivisionByZeroIsARuntimeError(string expr)
    {
        var r = RunHelper.RuntimeError($"squish \"before\"\nsquish {expr}");
        Assert.Equal("before\n", r.Out);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.DivideByZero, d.Category);
        Assert.Equal(2, d.Span.Line);
    }

    [Fact]
    public void FloatPrecisionFollowsTheType()
    {
        Assert.Equal("0.1", RunHelper.Eval("0.1"));                 // mid prints its own shortest form
        Assert.Equal("0.10000000149011612", RunHelper.Eval("chonk(0.1)")); // mid widened to chonk exposes the f32 value
        Assert.Equal("0.1", RunHelper.Ok("x::chonk = 0.1\nsquish x").Trim());
        Assert.Equal("1.0", RunHelper.Ok("x::smol = 1.0\nsquish x").Trim());
        Assert.Equal("2048.0", RunHelper.Ok("x::smol = 2049.0\nsquish x").Trim());
        Assert.Equal("0.30000000000000004", RunHelper.Ok("x::chonk = 0.1\ny::chonk = 0.2\nsquish x + y").Trim());
        Assert.Equal("nop", RunHelper.Ok("x::chonk = 0.1\ny::chonk = 0.2\nz::chonk = 0.3\nsquish x + y == z").Trim());
    }
}

public class ConversionTests
{
    [Theory]
    [InlineData("mid(3)", "3.0")]
    [InlineData("chonk(7) / chonk(2)", "3.5")]
    [InlineData("i32(3.99)", "3")]
    [InlineData("i32(-3.99)", "-3")]
    [InlineData("i64(5) * 3000000000", "15000000000")]
    [InlineData("u8(200)", "200")]
    [InlineData("txt(42)", "42")]
    [InlineData("txt(yep)", "yep")]
    [InlineData("txt(1.5)", "1.5")]
    [InlineData("u32(c\"A\")", "65")]
    [InlineData("char(97)", "a")]
    [InlineData("txt(char(9731))", "☃")]
    [InlineData("txt(1..4)", "1..4")]
    public void Valid(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Theory]
    [InlineData("i8(n)")]
    [InlineData("u8(m)")]
    [InlineData("u32(m)")]
    [InlineData("i32(1e20)")]
    [InlineData("i32(chonk(1.0) / chonk(0.0))")]
    [InlineData("char(55296)")]
    [InlineData("char(0 - 5)")]
    public void OutOfRangeConversionsFailAtRuntime(string expr)
    {
        var r = RunHelper.RuntimeError($"n := 300\nm := 0 - 1\nsquish({expr})");
        Assert.Equal(DiagnosticCategory.BadConversion, r.Diag.Diagnostics[0].Category);
    }

    [Theory]
    [InlineData("x := 1\ny := x + 1.0")]            // no implicit int -> float
    [InlineData("x::i64 = 1\ny::i32 = 2\nz := x + y")]
    [InlineData("a := bit(1)")]
    [InlineData("a := i32(\"5\")")]
    [InlineData("a := char(1.5)")]
    [InlineData("a := i32(1, 2)")]
    public void InvalidConversionsAreCompileErrors(string code) => Assert.NotEmpty(RunHelper.CompileErrors(code));

    [Fact]
    public void ImplicitMixingNamesTheExplicitFix()
    {
        var r = RunHelper.Run("a::i64 = 1\nb::i32 = 2\nc := a + b");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.BadOperator, d.Category);
        Assert.Contains("Convert explicitly", d.Fix);
    }
}

public class LiteralTypingTests
{
    [Fact]
    public void IntegerLiteralDefaultsToI32AndFloatToMid()
    {
        Assert.Equal("i32", Ty("42"));
        Assert.Equal("mid", Ty("1.5"));
        Assert.Equal("txt", Ty("\"s\""));
        Assert.Equal("char", Ty("c\"x\""));
        Assert.Equal("bit", Ty("yep"));
        Assert.Equal("zip", Ty("()"));
        Assert.Equal("range<i32>", Ty("0..3"));
    }

    private static string Ty(string expr)
    {
        var diag = new DiagnosticEngine();
        var src = new SourceText("t.splat", $"v := {expr}\nv = v");
        Pipeline.Compile(src, diag, out var program, out var info);
        // `v = v` is an error for the immutable bind, which is fine: we only want the bound type.
        var bind = (Splat.Syntax.BindStmt)program.Statements[0];
        return info.TypeOf(bind.Value)!.Name;
    }

    [Theory]
    [InlineData("x::i64 = 5", "i64")]
    [InlineData("x::u8 = 200", "u8")]
    [InlineData("x::i8 = -128", "i8")]
    [InlineData("x::chonk = 1.5", "chonk")]
    [InlineData("x::smol = 1.5", "smol")]
    public void LiteralsAdoptTheAnnotatedType(string code, string type)
    {
        var diag = new DiagnosticEngine();
        Assert.True(Pipeline.Compile(new SourceText("t.splat", code), diag, out var program, out var info),
            string.Join("\n", diag.Diagnostics.Select(d => d.Message)));
        var bind = (Splat.Syntax.BindStmt)program.Statements[0];
        Assert.Equal(type, info.TypeOf(bind.Value)!.Name);
    }

    [Fact]
    public void LiteralsAdoptTheOtherOperandsType()
    {
        // `n + 1` is an i64 addition, not an i32 literal forced into an i64 slot.
        Assert.Equal("9223372036854775807\n", RunHelper.Ok("n::i64 = 9223372036854775806\nsquish n + 1"));
        Assert.Equal("3000000001\n", RunHelper.Ok("n::i64 = 3000000000\nsquish 1 + n"));
        Assert.Equal("yep\n", RunHelper.Ok("n::u8 = 200\nsquish n > 100"));
    }

    [Theory]
    [InlineData("x::i8 = 128")]
    [InlineData("x::i8 = -129")]
    [InlineData("x::u8 = 256")]
    [InlineData("x::u8 = -1")]
    [InlineData("x := 2147483648")]
    [InlineData("x::mid = 1")]
    [InlineData("x::i32 = 1.0")]
    public void LiteralsThatDoNotFitAreCompileErrors(string code) => Assert.NotEmpty(RunHelper.CompileErrors(code));
}

public class BindingAndScopeTests
{
    [Fact]
    public void ImmutableBindCannotBeReassigned()
    {
        Assert.Equal([DiagnosticCategory.ImmutableAssign], RunHelper.CompileErrors("x := 1\nx = 2"));
        Assert.Equal([DiagnosticCategory.ImmutableAssign], RunHelper.CompileErrors("x::i32 = 1\nx += 2"));
    }

    [Fact]
    public void WagCanBeReassignedWithAllCompoundOperators()
    {
        Assert.Equal("3\n1\n12\n6\n", RunHelper.Ok("wag x = 1\nx = 3\nsquish x\nx -= 2\nsquish x\nx *= 12\nsquish x\nx /= 2\nsquish x"));
        Assert.Equal("5\n", RunHelper.Ok("wag x = 2\nx += 3\nsquish x"));
    }

    [Fact]
    public void ReassignmentMustKeepTheType()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("wag x = 1\nx = \"s\""));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("wag x::i32 = 1\nx = 2.5"));
    }

    [Fact]
    public void AnnotationIsChecked()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("x::i32 = \"hi\""));
        Assert.Equal([DiagnosticCategory.UndefinedName], RunHelper.CompileErrors("x::Wibble = 1"));
    }

    [Fact]
    public void RedeclaringInTheSameScopeIsAnError()
    {
        Assert.Equal([DiagnosticCategory.Redeclared], RunHelper.CompileErrors("x := 1\nx := 2"));
        Assert.Equal([DiagnosticCategory.Redeclared], RunHelper.CompileErrors("cook f(a::i32, a::i32)::i32 = a"));
    }

    [Fact]
    public void ShadowingInANestedScopeIsAllowedAndEndsWithTheScope()
    {
        Assert.Equal("inner 2\nouter 1\n", RunHelper.Ok("x := 1\nhmm yep:\n    x := 2\n    squish \"inner {x}\"\n;\nsquish \"outer {x}\""));
        Assert.Equal("txt 1\n", RunHelper.Ok("x := 1\ncook f(x::txt)::txt = x\nsquish f(\"txt\"), x"));
    }

    [Fact]
    public void ShadowingMayChangeTheType() =>
        Assert.Equal("s\n", RunHelper.Ok("x := 1\nhmm yep:\n    x := \"s\"\n    squish x\n;"));

    [Fact]
    public void InitializerSeesTheOuterBinding() =>
        Assert.Equal("2\n", RunHelper.Ok("x := 1\nhmm yep:\n    x := x + 1\n    squish x\n;"));

    [Fact]
    public void UseBeforeDeclarationIsUndefined() =>
        Assert.Equal([DiagnosticCategory.UndefinedName], RunHelper.CompileErrors("squish y\ny := 1"));

    [Fact]
    public void LoopVariableIsImmutableAndPerIteration()
    {
        Assert.Equal([DiagnosticCategory.ImmutableAssign], RunHelper.CompileErrors("spin i in 0..3:\n    i = 5\n;"));
        Assert.Equal("1 2 3\n", RunHelper.Ok(@"
cook counter(n::i32)::cook()::i32 = | | n
wag a = counter(1)
wag b = counter(2)
wag c = counter(3)
squish a(), b(), c()"));
    }

    [Fact]
    public void ErrorsAppearBeforeAnySideEffects()
    {
        var r = RunHelper.Run("squish \"hello\"\nx::i32 = \"oops\"");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal("", r.Out);
    }
}

public class ControlFlowTests
{
    [Fact]
    public void HmmMehNahPicksTheRightBranch()
    {
        const string prog = @"
cook sign(n::i32)::txt:
    hmm n < 0:
        yeet ""neg""
    meh n == 0:
        yeet ""zero""
    nah:
        yeet ""pos""
    ;
;
squish sign(0 - 5), sign(0), sign(9)";
        Assert.Equal("neg zero pos\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void HmmConditionMustBeBit()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("hmm 1:\n    squish \"x\"\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("spin while \"s\":\n    bail\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("hmm yep:\n    squish \"x\"\nmeh 2:\n    squish \"y\"\n;"));
    }

    [Fact]
    public void SpinInRangeExclusiveInclusiveAndEmpty()
    {
        Assert.Equal("0 1 2 \n", RunHelper.Ok("wag s = \"\"\nspin i in 0..3:\n    s = s + \"{i} \"\n;\nsquish s"));
        Assert.Equal("0 1 2 3 \n", RunHelper.Ok("wag s = \"\"\nspin i in 0..=3:\n    s = s + \"{i} \"\n;\nsquish s"));
        Assert.Equal("[]\n", RunHelper.Ok("wag s = \"\"\nspin i in 5..5:\n    s = s + \"{i}\"\n;\nsquish \"[{s}]\""));
        Assert.Equal("[]\n", RunHelper.Ok("wag s = \"\"\nspin i in 5..=4:\n    s = s + \"{i}\"\n;\nsquish \"[{s}]\""));
    }

    [Fact]
    public void SpinWhileAndForeverWithBailAndSkip()
    {
        Assert.Equal("10\n", RunHelper.Ok("wag n = 0\nspin while n < 10:\n    n += 1\n;\nsquish n"));
        Assert.Equal("7\n", RunHelper.Ok("wag n = 0\nspin:\n    n += 1\n    hmm n == 7:\n        bail\n    ;\n;\nsquish n"));
        Assert.Equal("1 3 5 \n", RunHelper.Ok("wag s = \"\"\nspin i in 1..=6:\n    hmm i % 2 == 0:\n        skip\n    ;\n    s = s + \"{i} \"\n;\nsquish s"));
    }

    [Fact]
    public void BailOnlyLeavesTheInnermostLoop()
    {
        const string prog = @"
wag total = 0
spin i in 0..3:
    spin j in 0..10:
        hmm j == 2:
            bail
        ;
        total += 1
    ;
;
squish total";
        Assert.Equal("6\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void YeetFromInsideALoopReturnsFromTheFunction()
    {
        const string prog = @"
cook first_square_over(limit::i32)::i32:
    spin i in 0..1000:
        hmm i * i > limit:
            yeet i
        ;
    ;
    yeet 0 - 1
;
squish first_square_over(50)";
        Assert.Equal("8\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void LoopControlOutsideALoopIsRejected()
    {
        Assert.Equal([DiagnosticCategory.LoopControlOutside], RunHelper.CompileErrors("bail"));
        Assert.Equal([DiagnosticCategory.LoopControlOutside], RunHelper.CompileErrors("skip"));
        // A function body inside a loop is not itself in the loop.
        Assert.Equal([DiagnosticCategory.LoopControlOutside], RunHelper.CompileErrors("spin i in 0..2:\n    cook f()::zip:\n        bail\n    ;\n;"));
    }

    [Fact]
    public void RangeIterationRequiresARange() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("spin i in 5:\n    bail\n;"));

    [Fact]
    public void RangeBoundsMustBeIntegersOfTheSameType()
    {
        Assert.NotEmpty(RunHelper.CompileErrors("x := 0..1.5"));
        Assert.NotEmpty(RunHelper.CompileErrors("a::i64 = 1\nb::i32 = 5\nx := a..b"));
    }

    [Fact]
    public void RangeOverTheTypeOfItsBound() =>
        Assert.Equal("3000000000\n", RunHelper.Ok("n::i64 = 3000000000\nspin i in n..=n:\n    squish i\n;"));
}

public class FunctionTests
{
    [Fact]
    public void RecursionAndMutualRecursionViaHoisting()
    {
        Assert.Equal("yep nop\n", RunHelper.Ok(@"
squish is_even(10), is_even(7)
cook is_even(n::i32)::bit:
    hmm n == 0:
        yeet yep
    ;
    yeet is_odd(n - 1)
;
cook is_odd(n::i32)::bit:
    hmm n == 0:
        yeet nop
    ;
    yeet is_even(n - 1)
;"));
    }

    [Fact]
    public void TrailingExpressionIsTheReturnValue()
    {
        Assert.Equal("7\n", RunHelper.Ok("cook f(a::i32)::i32:\n    t := a + 1\n    t * 1\n;\nsquish f(6)"));
        Assert.Equal("big\n", RunHelper.Ok("cook f(a::i32)::txt:\n    hmm a > 5:\n        \"big\"\n    nah:\n        \"small\"\n    ;\n;\nsquish f(9)"));
    }

    [Fact]
    public void ReturnTypeInferredFromAnExpressionBody()
    {
        Assert.Equal("8\n", RunHelper.Ok("cook double(x::i32) = x * 2\ncook quad(x::i32) = double(double(x))\nsquish quad(2)"));
    }

    [Fact]
    public void ArityIsChecked()
    {
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("cook f(a::i32)::i32 = a\nx := f()"));
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("cook f(a::i32)::i32 = a\nx := f(1, 2)"));
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("f := |x::i32| x\ny := f(1, 2)"));
    }

    [Fact]
    public void ArgumentTypesAreChecked() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("cook f(a::i32)::i32 = a\nx := f(\"s\")"));

    [Fact]
    public void ReturnTypeIsCheckedAgainstYeetAndTail()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("cook f()::i32:\n    yeet \"s\"\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("cook f()::i32 = \"s\""));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("cook f()::i32:\n    \"s\"\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("cook f()::zip:\n    yeet 1\n;"));
    }

    [Fact]
    public void MissingReturnPathIsAnError()
    {
        Assert.Equal([DiagnosticCategory.MissingReturn], RunHelper.CompileErrors("cook f(n::i32)::i32:\n    hmm n > 0:\n        yeet 1\n    ;\n;"));
        Assert.Equal([DiagnosticCategory.MissingReturn], RunHelper.CompileErrors("cook f(n::i32)::i32:\n    n * 1\n    x := 1\n;"));
    }

    [Fact]
    public void AllPathsReturningIsAccepted()
    {
        Assert.Equal("1\n", RunHelper.Ok("cook f(n::i32)::i32:\n    hmm n > 0:\n        yeet 1\n    nah:\n        yeet 2\n    ;\n;\nsquish f(5)"));
        Assert.Equal("1\n", RunHelper.Ok("cook f()::i32:\n    spin:\n        yeet 1\n    ;\n;\nsquish f()"));
        Assert.Equal("1\n", RunHelper.Ok("cook f()::i32:\n    rip \"never\"\n;\nsquish 1"));
    }

    [Fact]
    public void YeetOutsideAFunctionIsRejected() =>
        Assert.Equal([DiagnosticCategory.YeetOutside], RunHelper.CompileErrors("yeet 1"));

    [Fact]
    public void RecursiveFunctionWithInferredReturnTypeAsksForAnAnnotation() =>
        Assert.Equal([DiagnosticCategory.CannotInfer], RunHelper.CompileErrors("cook r(n::i32) = r(n - 1)"));

    [Fact]
    public void ParametersNeedTypes() =>
        Assert.NotEmpty(RunHelper.CompileErrors("cook f(a) = a"));

    [Fact]
    public void DefaultsAndNamedArguments()
    {
        const string prog = @"
cook box(w::i32, h::i32 = 2, d::i32 = 3)::i32 = w * h * d
squish box(1), box(1, 5), box(1, 5, 7), box(h = 4, w = 10), box(2, d = 10)";
        Assert.Equal("6 15 35 120 40\n", RunHelper.Ok(prog));
        Assert.Equal([DiagnosticCategory.UnexpectedToken], RunHelper.CompileErrors("cook f(a::i32)::i32 = a\nx := f(b = 1)"));
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("cook f(a::i32)::i32 = a\nx := f(1, a = 2)"));
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("cook f(a::i32, b::i32)::i32 = a\nx := f(b = 2)"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("cook f(a::i32 = \"s\")::i32 = a"));
    }

    [Fact]
    public void CallingANonFunctionIsAnError()
    {
        Assert.Equal([DiagnosticCategory.NotCallable], RunHelper.CompileErrors("x := 5\ny := x(1)"));
        Assert.Equal([DiagnosticCategory.NotCallable], RunHelper.CompileErrors("y := \"s\"(1)"));
    }

    [Fact]
    public void UndefinedFunctionIsReported() =>
        Assert.Equal([DiagnosticCategory.UndefinedName], RunHelper.CompileErrors("x := nope(1)"));

    [Fact]
    public void GenericParametersAreAcceptedLeniently() =>
        Assert.Equal("5 hi\n", RunHelper.Ok("cook ident<T>(v::T)::T = v\nsquish ident(5), ident(\"hi\")"));

    [Fact]
    public void NestedFunctionsSeeTheirEnclosingScope() =>
        Assert.Equal("20\n", RunHelper.Ok("cook outer(a::i32)::i32:\n    b := 10\n    cook inner(c::i32)::i32 = a + b + c\n    inner(1) - 6 + 10\n;\nsquish outer(5)"));

    [Fact]
    public void DuplicateFunctionIsRedeclared() =>
        Assert.Equal([DiagnosticCategory.Redeclared], RunHelper.CompileErrors("cook f()::i32 = 1\ncook f()::i32 = 2"));

    [Fact]
    public void RuntimeErrorInsideCallsCarriesATrace()
    {
        var r = RunHelper.RuntimeError("cook a(n::i32)::i32 = 1 / n\ncook b(n::i32)::i32 = a(n)\nsquish b(0)");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.DivideByZero, d.Category);
        Assert.NotNull(d.Trace);
        Assert.StartsWith("a ", d.Trace![0]);
        Assert.StartsWith("b ", d.Trace[1]);
        Assert.Equal(2, d.Trace.Count);
    }

    [Fact]
    public void DeepButFiniteRecursionWorks() =>
        Assert.Equal("9000\n", RunHelper.Ok("cook depth(n::i32)::i32:\n    hmm n == 0:\n        yeet 0\n    ;\n    yeet 1 + depth(n - 1)\n;\nsquish depth(9000)"));

    [Fact]
    public void RunawayRecursionIsAComedicErrorNotACrash()
    {
        var r = RunHelper.RuntimeError("cook f(n::i32)::i32 = f(n + 1) + 1\nsquish f(0)");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.StackOverflow, d.Category);
        Assert.True(d.Trace!.Count <= 20); // the trace is abbreviated, not 10,000 lines
    }
}

public class ClosureAndHigherOrderTests
{
    [Fact]
    public void ClosureCounterKeepsPrivateMutableState()
    {
        const string prog = @"
cook make(start::i32)::cook()::i32:
    wag n = start
    cook next()::i32:
        n += 1
        yeet n
    ;
    yeet next
;
a := make(0)
b := make(10)
squish a(), a(), b(), a()";
        Assert.Equal("1 2 11 3\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void LambdaCapturesAndHigherOrderFunctions()
    {
        const string prog = @"
cook twice(f::cook(i32)::i32, x::i32)::i32 = f(f(x))
cook adder(k::i32)::cook(i32)::i32 = |x| x + k
squish twice(|x| x * 3, 7)
add5 := adder(5)
squish add5(10)
squish adder(1)(2)";
        Assert.Equal("63\n15\n3\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void LambdaParametersTakeTheirTypeFromContextOrAnAnnotation()
    {
        Assert.Equal("42\n", RunHelper.Ok("inc := |x::i64| x + 1\nsquish inc(41)"));
        Assert.Equal([DiagnosticCategory.CannotInfer], RunHelper.CompileErrors("inc := |x| x + 1"));
        Assert.Equal("5\n", RunHelper.Ok("cook apply(f::cook(i32)::i32, v::i32)::i32 = f(v)\nsquish apply(|a| a + 1, 4)"));
    }

    [Fact]
    public void LambdaArgumentIsCheckedAgainstTheExpectedFunctionType()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch],
            RunHelper.CompileErrors("cook apply(f::cook(i32)::i32, v::i32)::i32 = f(v)\nsquish apply(|a| \"s\", 4)"));
        Assert.Equal([DiagnosticCategory.TypeMismatch],
            RunHelper.CompileErrors("cook apply(f::cook(i32)::i32, v::i32)::i32 = f(v)\nsquish apply(|a, b| a, 4)"));
    }

    [Fact]
    public void ClosuresShareTheCapturedVariable()
    {
        const string prog = @"
wag total = 0
cook add(n::i32)::zip:
    total += n
;
add(5)
add(7)
squish total";
        Assert.Equal("12\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void FunctionValuesCanBeStoredAndPassed() =>
        Assert.Equal("9\n", RunHelper.Ok("cook sq(x::i32)::i32 = x * x\ncook run(f::cook(i32)::i32, v::i32)::i32 = f(v)\ng := sq\nsquish run(g, 3)"));
}
