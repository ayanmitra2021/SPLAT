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

    public static Result Run(string code, string file = "t.splat", string stdin = "")
    {
        var diag = new DiagnosticEngine();
        var so = new StringWriter { NewLine = "\n" };
        var se = new StringWriter { NewLine = "\n" };
        var outcome = Pipeline.Run(new SourceText(file, code), diag, so, se, new StringReader(stdin));
        return new Result(outcome, so.ToString(), se.ToString(), diag);
    }

    /// <summary>Runs a program that must succeed and returns its stdout.</summary>
    public static string Ok(string code)
    {
        var r = Run(code);
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        return r.Out;
    }

    /// <summary>Evaluates one expression through print and returns what it printed.</summary>
    public static string Eval(string expr) => Ok($"print({expr})").TrimEnd('\n');

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
    [InlineData("2.0 ** 0.5 > 1.41", "true")]
    [InlineData("0.1 + 0.2 == 0.3", "true")]
    [InlineData("1.0 / 0.0", "inf")]
    [InlineData("-1.0 / 0.0", "-inf")]
    public void Expressions(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Theory]
    [InlineData("1 < 2", "true")]
    [InlineData("2 <= 2", "true")]
    [InlineData("3 > 4", "false")]
    [InlineData("3 != 4", "true")]
    [InlineData("\"abc\" < \"abd\"", "true")]
    [InlineData("\"b\" > \"a\"", "true")]
    [InlineData("\"x\" == \"x\"", "true")]
    [InlineData("c\"a\" < c\"b\"", "true")]
    [InlineData("true == false", "false")]
    [InlineData("() == ()", "true")]
    [InlineData("1 + 1 == 2 and 2 + 2 == 4", "true")]
    [InlineData("1 > 2 or 3 > 2", "true")]
    [InlineData("!(1 > 2)", "true")]
    [InlineData("true or false and false", "true")]
    public void ComparisonsAndBooleans(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Fact]
    public void AndOrShortCircuit()
    {
        // The right-hand side would divide by zero if it ran.
        Assert.Equal("false", RunHelper.Eval("false and 1 / 0 == 1"));
        Assert.Equal("true", RunHelper.Eval("true or 1 / 0 == 1"));
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
        Assert.Equal("18446744073709551615", RunHelper.Ok("x::u64 = 18446744073709551615\nprint x").Trim());
        Assert.Equal("18446744073709551615", RunHelper.Ok("x::usize = 18446744073709551615\nprint x").Trim());
    }

    [Theory]
    [InlineData("x::i8 = 127\nprint x + 1", "i8")]
    [InlineData("x::u8 = 0\nprint x - 1", "u8")]
    [InlineData("x::i32 = 2147483647\nprint x + 1", "i32")]
    [InlineData("x::i32 = 65536\nprint x * x", "i32")]
    [InlineData("x::i64 = 9223372036854775807\nprint x + 1", "i64")]
    [InlineData("x::u64 = 18446744073709551615\nprint x + 1", "u64")]
    [InlineData("x::i32 = 2\nprint x ** 31", "i32")]
    [InlineData("x::i64 = 3\nprint x ** 100", "i64")]
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
        var r = RunHelper.RuntimeError("x::i8 = -128\nprint(-x)");
        Assert.Equal(DiagnosticCategory.IntegerOverflow, r.Diag.Diagnostics[0].Category);
    }

    [Fact]
    public void PowerEdgeCases()
    {
        Assert.Equal("1", RunHelper.Eval("0 ** 0"));
        Assert.Equal("0", RunHelper.Eval("0 ** 5"));
        Assert.Equal("1", RunHelper.Eval("1 ** 1000000"));
        Assert.Equal("-8", RunHelper.Eval("(-2) ** 3"));
        Assert.Equal("-9223372036854775808", RunHelper.Ok("x::i64 = -2\nprint x ** 63").Trim());
        var r = RunHelper.RuntimeError("print 2 ** (0 - 1)");
        Assert.Equal(DiagnosticCategory.MathDomain, r.Diag.Diagnostics[0].Category);
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("5 % 0")]
    public void IntegerDivisionByZeroIsARuntimeError(string expr)
    {
        var r = RunHelper.RuntimeError($"print \"before\"\nprint {expr}");
        Assert.Equal("before\n", r.Out);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.DivideByZero, d.Category);
        Assert.Equal(2, d.Span.Line);
    }

    [Fact]
    public void FloatPrecisionFollowsTheType()
    {
        Assert.Equal("0.1", RunHelper.Eval("0.1"));                 // f32 prints its own shortest form
        Assert.Equal("0.10000000149011612", RunHelper.Eval("f64(0.1)")); // f32 widened to f64 exposes the f32 value
        Assert.Equal("0.1", RunHelper.Ok("x::f64 = 0.1\nprint x").Trim());
        Assert.Equal("1.0", RunHelper.Ok("x::f16 = 1.0\nprint x").Trim());
        Assert.Equal("2048.0", RunHelper.Ok("x::f16 = 2049.0\nprint x").Trim());
        Assert.Equal("0.30000000000000004", RunHelper.Ok("x::f64 = 0.1\ny::f64 = 0.2\nprint x + y").Trim());
        Assert.Equal("false", RunHelper.Ok("x::f64 = 0.1\ny::f64 = 0.2\nz::f64 = 0.3\nprint x + y == z").Trim());
    }
}

public class ConversionTests
{
    [Theory]
    [InlineData("f32(3)", "3.0")]
    [InlineData("f64(7) / f64(2)", "3.5")]
    [InlineData("i32(3.99)", "3")]
    [InlineData("i32(-3.99)", "-3")]
    [InlineData("i64(5) * 3000000000", "15000000000")]
    [InlineData("u8(200)", "200")]
    [InlineData("txt(42)", "42")]
    [InlineData("txt(true)", "true")]
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
    [InlineData("i32(f64(1.0) / f64(0.0))")]
    [InlineData("char(55296)")]
    [InlineData("char(0 - 5)")]
    public void OutOfRangeConversionsFailAtRuntime(string expr)
    {
        var r = RunHelper.RuntimeError($"n := 300\nm := 0 - 1\nprint({expr})");
        Assert.Equal(DiagnosticCategory.BadConversion, r.Diag.Diagnostics[0].Category);
    }

    [Theory]
    [InlineData("x := 1\ny := x + 1.0")]            // no implicit int -> float
    [InlineData("x::i64 = 1\ny::i32 = 2\nz := x + y")]
    [InlineData("a := bool(1)")]
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
        Assert.Equal("f32", Ty("1.5"));
        Assert.Equal("txt", Ty("\"s\""));
        Assert.Equal("char", Ty("c\"x\""));
        Assert.Equal("bool", Ty("true"));
        Assert.Equal("unit", Ty("()"));
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
    [InlineData("x::f64 = 1.5", "f64")]
    [InlineData("x::f16 = 1.5", "f16")]
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
        Assert.Equal("9223372036854775807\n", RunHelper.Ok("n::i64 = 9223372036854775806\nprint n + 1"));
        Assert.Equal("3000000001\n", RunHelper.Ok("n::i64 = 3000000000\nprint 1 + n"));
        Assert.Equal("true\n", RunHelper.Ok("n::u8 = 200\nprint n > 100"));
    }

    [Theory]
    [InlineData("x::i8 = 128")]
    [InlineData("x::i8 = -129")]
    [InlineData("x::u8 = 256")]
    [InlineData("x::u8 = -1")]
    [InlineData("x := 2147483648")]
    [InlineData("x::f32 = 1")]
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
    public void VarCanBeReassignedWithAllCompoundOperators()
    {
        Assert.Equal("3\n1\n12\n6\n", RunHelper.Ok("var x = 1\nx = 3\nprint x\nx -= 2\nprint x\nx *= 12\nprint x\nx /= 2\nprint x"));
        Assert.Equal("5\n", RunHelper.Ok("var x = 2\nx += 3\nprint x"));
    }

    [Fact]
    public void ReassignmentMustKeepTheType()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("var x = 1\nx = \"s\""));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("var x::i32 = 1\nx = 2.5"));
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
        Assert.Equal([DiagnosticCategory.Redeclared], RunHelper.CompileErrors("fn f(a::i32, a::i32)::i32 = a"));
    }

    [Fact]
    public void ShadowingInANestedScopeIsAllowedAndEndsWithTheScope()
    {
        Assert.Equal("inner 2\nouter 1\n", RunHelper.Ok("x := 1\nif true:\n    x := 2\n    print \"inner {x}\"\n;\nprint \"outer {x}\""));
        Assert.Equal("txt 1\n", RunHelper.Ok("x := 1\nfn f(x::txt)::txt = x\nprint f(\"txt\"), x"));
    }

    [Fact]
    public void ShadowingMayChangeTheType() =>
        Assert.Equal("s\n", RunHelper.Ok("x := 1\nif true:\n    x := \"s\"\n    print x\n;"));

    [Fact]
    public void InitializerSeesTheOuterBinding() =>
        Assert.Equal("2\n", RunHelper.Ok("x := 1\nif true:\n    x := x + 1\n    print x\n;"));

    [Fact]
    public void UseBeforeDeclarationIsUndefined() =>
        Assert.Equal([DiagnosticCategory.UndefinedName], RunHelper.CompileErrors("print y\ny := 1"));

    [Fact]
    public void LoopVariableIsImmutableAndPerIteration()
    {
        Assert.Equal([DiagnosticCategory.ImmutableAssign], RunHelper.CompileErrors("loop i in 0..3:\n    i = 5\n;"));
        Assert.Equal("1 2 3\n", RunHelper.Ok(@"
fn counter(n::i32)::fn()::i32 = | | n
var a = counter(1)
var b = counter(2)
var c = counter(3)
print a(), b(), c()"));
    }

    [Fact]
    public void ErrorsAppearBeforeAnySideEffects()
    {
        var r = RunHelper.Run("print \"hello\"\nx::i32 = \"catch\"");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal("", r.Out);
    }
}

public class ControlFlowTests
{
    [Fact]
    public void IfElifElsePicksTheRightBranch()
    {
        const string prog = @"
fn sign(n::i32)::txt:
    if n < 0:
        ret ""neg""
    elif n == 0:
        ret ""zero""
    el:
        ret ""pos""
    ;
;
print sign(0 - 5), sign(0), sign(9)";
        Assert.Equal("neg zero pos\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void IfConditionMustBeBit()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("if 1:\n    print \"x\"\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("loop while \"s\":\n    br\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("if true:\n    print \"x\"\nelif 2:\n    print \"y\"\n;"));
    }

    [Fact]
    public void LoopInRangeExclusiveInclusiveAndEmpty()
    {
        Assert.Equal("0 1 2 \n", RunHelper.Ok("var s = \"\"\nloop i in 0..3:\n    s = s + \"{i} \"\n;\nprint s"));
        Assert.Equal("0 1 2 3 \n", RunHelper.Ok("var s = \"\"\nloop i in 0..=3:\n    s = s + \"{i} \"\n;\nprint s"));
        Assert.Equal("[]\n", RunHelper.Ok("var s = \"\"\nloop i in 5..5:\n    s = s + \"{i}\"\n;\nprint \"[{s}]\""));
        Assert.Equal("[]\n", RunHelper.Ok("var s = \"\"\nloop i in 5..=4:\n    s = s + \"{i}\"\n;\nprint \"[{s}]\""));
    }

    [Fact]
    public void LoopWhileAndForeverWithBreakAndContinue()
    {
        Assert.Equal("10\n", RunHelper.Ok("var n = 0\nloop while n < 10:\n    n += 1\n;\nprint n"));
        Assert.Equal("7\n", RunHelper.Ok("var n = 0\nloop:\n    n += 1\n    if n == 7:\n        br\n    ;\n;\nprint n"));
        Assert.Equal("1 3 5 \n", RunHelper.Ok("var s = \"\"\nloop i in 1..=6:\n    if i % 2 == 0:\n        cont\n    ;\n    s = s + \"{i} \"\n;\nprint s"));
    }

    [Fact]
    public void BreakOnlyLeavesTheInnermostLoop()
    {
        const string prog = @"
var total = 0
loop i in 0..3:
    loop j in 0..10:
        if j == 2:
            br
        ;
        total += 1
    ;
;
print total";
        Assert.Equal("6\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void ReturnFromInsideALoopReturnsFromTheFunction()
    {
        const string prog = @"
fn first_square_over(limit::i32)::i32:
    loop i in 0..1000:
        if i * i > limit:
            ret i
        ;
    ;
    ret 0 - 1
;
print first_square_over(50)";
        Assert.Equal("8\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void LoopControlOutsideALoopIsRejected()
    {
        Assert.Equal([DiagnosticCategory.LoopControlOutside], RunHelper.CompileErrors("br"));
        Assert.Equal([DiagnosticCategory.LoopControlOutside], RunHelper.CompileErrors("cont"));
        // A function body inside a loop is not itself in the loop.
        Assert.Equal([DiagnosticCategory.LoopControlOutside], RunHelper.CompileErrors("loop i in 0..2:\n    fn f()::unit:\n        br\n    ;\n;"));
    }

    [Fact]
    public void RangeIterationRequiresARange() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("loop i in 5:\n    br\n;"));

    [Fact]
    public void RangeBoundsMustBeIntegersOfTheSameType()
    {
        Assert.NotEmpty(RunHelper.CompileErrors("x := 0..1.5"));
        Assert.NotEmpty(RunHelper.CompileErrors("a::i64 = 1\nb::i32 = 5\nx := a..b"));
    }

    [Fact]
    public void RangeOverTheTypeOfItsBound() =>
        Assert.Equal("3000000000\n", RunHelper.Ok("n::i64 = 3000000000\nloop i in n..=n:\n    print i\n;"));
}

public class FunctionTests
{
    [Fact]
    public void RecursionAndMutualRecursionViaHoisting()
    {
        Assert.Equal("true false\n", RunHelper.Ok(@"
print is_even(10), is_even(7)
fn is_even(n::i32)::bool:
    if n == 0:
        ret true
    ;
    ret is_odd(n - 1)
;
fn is_odd(n::i32)::bool:
    if n == 0:
        ret false
    ;
    ret is_even(n - 1)
;"));
    }

    [Fact]
    public void TrailingExpressionIsTheReturnValue()
    {
        Assert.Equal("7\n", RunHelper.Ok("fn f(a::i32)::i32:\n    t := a + 1\n    t * 1\n;\nprint f(6)"));
        Assert.Equal("big\n", RunHelper.Ok("fn f(a::i32)::txt:\n    if a > 5:\n        \"big\"\n    el:\n        \"small\"\n    ;\n;\nprint f(9)"));
    }

    [Fact]
    public void ReturnTypeInferredFromAnExpressionBody()
    {
        Assert.Equal("8\n", RunHelper.Ok("fn double(x::i32) = x * 2\nfn quad(x::i32) = double(double(x))\nprint quad(2)"));
    }

    [Fact]
    public void ArityIsChecked()
    {
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("fn f(a::i32)::i32 = a\nx := f()"));
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("fn f(a::i32)::i32 = a\nx := f(1, 2)"));
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("f := |x::i32| x\ny := f(1, 2)"));
    }

    [Fact]
    public void ArgumentTypesAreChecked() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("fn f(a::i32)::i32 = a\nx := f(\"s\")"));

    [Fact]
    public void ReturnTypeIsCheckedAgainstReturnAndTail()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("fn f()::i32:\n    ret \"s\"\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("fn f()::i32 = \"s\""));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("fn f()::i32:\n    \"s\"\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("fn f()::unit:\n    ret 1\n;"));
    }

    [Fact]
    public void MissingReturnPathIsAnError()
    {
        Assert.Equal([DiagnosticCategory.MissingReturn], RunHelper.CompileErrors("fn f(n::i32)::i32:\n    if n > 0:\n        ret 1\n    ;\n;"));
        Assert.Equal([DiagnosticCategory.MissingReturn], RunHelper.CompileErrors("fn f(n::i32)::i32:\n    n * 1\n    x := 1\n;"));
    }

    [Fact]
    public void AllPathsReturningIsAccepted()
    {
        Assert.Equal("1\n", RunHelper.Ok("fn f(n::i32)::i32:\n    if n > 0:\n        ret 1\n    el:\n        ret 2\n    ;\n;\nprint f(5)"));
        Assert.Equal("1\n", RunHelper.Ok("fn f()::i32:\n    loop:\n        ret 1\n    ;\n;\nprint f()"));
        Assert.Equal("1\n", RunHelper.Ok("fn f()::i32:\n    rip \"never\"\n;\nprint 1"));
    }

    [Fact]
    public void ReturnOutsideAFunctionIsRejected() =>
        Assert.Equal([DiagnosticCategory.ReturnOutside], RunHelper.CompileErrors("ret 1"));

    [Fact]
    public void RecursiveFunctionWithInferredReturnTypeAsksForAnAnnotation() =>
        Assert.Equal([DiagnosticCategory.CannotInfer], RunHelper.CompileErrors("fn r(n::i32) = r(n - 1)"));

    [Fact]
    public void ParametersNeedTypes() =>
        Assert.NotEmpty(RunHelper.CompileErrors("fn f(a) = a"));

    [Fact]
    public void DefaultsAndNamedArguments()
    {
        const string prog = @"
fn box(w::i32, h::i32 = 2, d::i32 = 3)::i32 = w * h * d
print box(1), box(1, 5), box(1, 5, 7), box(h = 4, w = 10), box(2, d = 10)";
        Assert.Equal("6 15 35 120 40\n", RunHelper.Ok(prog));
        Assert.Equal([DiagnosticCategory.UnexpectedToken], RunHelper.CompileErrors("fn f(a::i32)::i32 = a\nx := f(b = 1)"));
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("fn f(a::i32)::i32 = a\nx := f(1, a = 2)"));
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("fn f(a::i32, b::i32)::i32 = a\nx := f(b = 2)"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("fn f(a::i32 = \"s\")::i32 = a"));
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
        Assert.Equal("5 hi\n", RunHelper.Ok("fn ident<T>(v::T)::T = v\nprint ident(5), ident(\"hi\")"));

    [Fact]
    public void NestedFunctionsSeeTheirEnclosingScope() =>
        Assert.Equal("20\n", RunHelper.Ok("fn outer(a::i32)::i32:\n    b := 10\n    fn inner(c::i32)::i32 = a + b + c\n    inner(1) - 6 + 10\n;\nprint outer(5)"));

    [Fact]
    public void DuplicateFunctionIsRedeclared() =>
        Assert.Equal([DiagnosticCategory.Redeclared], RunHelper.CompileErrors("fn f()::i32 = 1\nfn f()::i32 = 2"));

    [Fact]
    public void RuntimeErrorInsideCallsCarriesATrace()
    {
        var r = RunHelper.RuntimeError("fn a(n::i32)::i32 = 1 / n\nfn b(n::i32)::i32 = a(n)\nprint b(0)");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.DivideByZero, d.Category);
        Assert.NotNull(d.Trace);
        Assert.StartsWith("a ", d.Trace![0]);
        Assert.StartsWith("b ", d.Trace[1]);
        Assert.Equal(2, d.Trace.Count);
    }

    [Fact]
    public void DeepButFiniteRecursionWorks() =>
        Assert.Equal("9000\n", RunHelper.Ok("fn depth(n::i32)::i32:\n    if n == 0:\n        ret 0\n    ;\n    ret 1 + depth(n - 1)\n;\nprint depth(9000)"));

    [Fact]
    public void RunawayRecursionIsAComedicErrorNotACrash()
    {
        var r = RunHelper.RuntimeError("fn f(n::i32)::i32 = f(n + 1) + 1\nprint f(0)");
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
fn make(start::i32)::fn()::i32:
    var n = start
    fn next()::i32:
        n += 1
        ret n
    ;
    ret next
;
a := make(0)
b := make(10)
print a(), a(), b(), a()";
        Assert.Equal("1 2 11 3\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void LambdaCapturesAndHigherOrderFunctions()
    {
        const string prog = @"
fn twice(f::fn(i32)::i32, x::i32)::i32 = f(f(x))
fn adder(k::i32)::fn(i32)::i32 = |x| x + k
print twice(|x| x * 3, 7)
add5 := adder(5)
print add5(10)
print adder(1)(2)";
        Assert.Equal("63\n15\n3\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void LambdaParametersTakeTheirTypeFromContextOrAnAnnotation()
    {
        Assert.Equal("42\n", RunHelper.Ok("inc := |x::i64| x + 1\nprint inc(41)"));
        Assert.Equal([DiagnosticCategory.CannotInfer], RunHelper.CompileErrors("inc := |x| x + 1"));
        Assert.Equal("5\n", RunHelper.Ok("fn apply(f::fn(i32)::i32, v::i32)::i32 = f(v)\nprint apply(|a| a + 1, 4)"));
    }

    [Fact]
    public void LambdaArgumentIsCheckedAgainstTheExpectedFunctionType()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch],
            RunHelper.CompileErrors("fn apply(f::fn(i32)::i32, v::i32)::i32 = f(v)\nprint apply(|a| \"s\", 4)"));
        Assert.Equal([DiagnosticCategory.TypeMismatch],
            RunHelper.CompileErrors("fn apply(f::fn(i32)::i32, v::i32)::i32 = f(v)\nprint apply(|a, b| a, 4)"));
    }

    [Fact]
    public void ClosuresShareTheCapturedVariable()
    {
        const string prog = @"
var total = 0
fn add(n::i32)::unit:
    total += n
;
add(5)
add(7)
print total";
        Assert.Equal("12\n", RunHelper.Ok(prog));
    }

    [Fact]
    public void FunctionValuesCanBeStoredAndPassed() =>
        Assert.Equal("9\n", RunHelper.Ok("fn sq(x::i32)::i32 = x * x\nfn run(f::fn(i32)::i32, v::i32)::i32 = f(v)\ng := sq\nprint run(g, 3)"));
}
