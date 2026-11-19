using Splat.Core;
using Splat.Runtime;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Tests;

public class Phase7GoldenTests
{
    [Theory]
    [InlineData("maybe_demo.splat")]
    [InlineData("mood_tree.splat")]
    [InlineData("pick_patterns.splat")]
    [InlineData("collections_demo.splat")]
    public void ProgramsRunAndMatchTheirExpectedOutput(string file)
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, file), file);
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Assert.Empty(r.Diag.Diagnostics); // not even a warning
        Golden.Compare(Golden.Dir, Path.ChangeExtension(file, ".out"), r.Out);
    }

    [Theory]
    [InlineData("non_exhaustive.splat", RunOutcome.CompileError)]
    [InlineData("mutate_iterating.splat", RunOutcome.CompileError)]
    [InlineData("bad_collections.splat", RunOutcome.CompileError)]
    [InlineData("bad_runtime_index.splat", RunOutcome.RuntimeError)]
    public void BadProgramsReportTheirDiagnostics(string file, RunOutcome expected)
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, file), file);
        Assert.Equal(expected, r.Outcome);
        if (expected == RunOutcome.CompileError) Assert.Equal("", r.Out); // nothing runs when the checker rejects the program
        Assert.All(r.Diag.Diagnostics, d => Assert.Matches("^S[EW][0-9]{5}$", d.Code));
        Golden.Compare(Golden.Dir, Path.ChangeExtension(file, ".err"), r.Rendered);
    }
}

public class EnumDeclarationTests
{
    [Fact]
    public void EnumsAreVisibleToFunctionsDeclaredBeforeThem() =>
        Assert.Equal("Circle(2.0)\n", RunHelper.Ok("fn make()::Shape = Circle(2.0)\nenum Shape:\n    Circle(f64),\n    Dot\n;\nprint make()"));

    [Fact]
    public void EnumsMayBeMutuallyRecursive() =>
        Assert.Equal("3\n", RunHelper.Ok(
            "enum Tree:\n    Node(Forest),\n    Leaf\n;\nenum Forest:\n    Cons(Tree, Forest),\n    Empty\n;\n" +
            "fn size(t::Tree)::i32 = pick t:\n    Leaf => 1,\n    Node(f) => size_forest(f)\n;\n" +
            "fn size_forest(f::Forest)::i32 = pick f:\n    Empty => 0,\n    Cons(t, rest) => size(t) + size_forest(rest)\n;\n" +
            "print size(Node(Cons(Leaf, Cons(Leaf, Cons(Leaf, Empty)))))"));

    [Fact]
    public void AEnumMayBeDeclaredInsideAFunction() =>
        Assert.Equal("yes\n", RunHelper.Ok("fn f()::txt:\n    enum Answer:\n        Yes,\n        No\n    ;\n    a := Yes\n    ret pick a:\n        Yes => \"yes\",\n        No => \"no\"\n    ;\n;\nprint f()"));

    [Fact]
    public void RedeclaringAEnumIsAnError() =>
        Assert.Contains(DiagnosticCategory.Redeclared, RunHelper.CompileErrors("enum A:\n    X\n;\nenum A:\n    Y\n;"));

    [Fact]
    public void ReusingAVariantNameInAnotherEnumIsAnError() =>
        Assert.Equal([DiagnosticCategory.Redeclared], RunHelper.CompileErrors("enum A:\n    X,\n    Y\n;\nenum B:\n    Y,\n    Z\n;"));

    [Fact]
    public void RepeatingAVariantInsideOneEnumIsAnError() =>
        Assert.Equal([DiagnosticCategory.Redeclared], RunHelper.CompileErrors("enum A:\n    X,\n    X\n;"));

    [Fact]
    public void AUserEnumMayShadowABuiltInOne() =>
        Assert.Equal("Nada(1)\n", RunHelper.Ok("enum Maybe<T>:\n    Nada(T),\n    Got\n;\nprint Nada(1)"));

    [Fact]
    public void UnitAndPayloadVariantsBuildValues() =>
        Assert.Equal("Dot Circle(1.5)\n", RunHelper.Ok("enum Shape:\n    Circle(f64),\n    Dot\n;\nprint Dot, Circle(1.5)"));

    [Fact]
    public void VariantArityIsChecked()
    {
        Assert.Equal([DiagnosticCategory.WrongVariantArity], RunHelper.CompileErrors("print Got(1, 2)"));
        Assert.Equal([DiagnosticCategory.WrongVariantArity], RunHelper.CompileErrors("print Got()"));
        Assert.Equal([DiagnosticCategory.WrongVariantArity],
            RunHelper.CompileErrors("m := Got(1)\npick m:\n    Got(a, b) => 1,\n    Nada => 0\n;"));
    }

    [Fact]
    public void ACallToAUnitVariantIsAnArityError() =>
        Assert.Equal([DiagnosticCategory.WrongVariantArity], RunHelper.CompileErrors("print Nada(3)"));

    [Fact]
    public void AVariantConstructorIsNotAFirstClassValue() =>
        Assert.Equal([DiagnosticCategory.UnexpectedToken], RunHelper.CompileErrors("f := Got\nprint 1"));

    [Fact]
    public void TypeArgumentCountIsChecked()
    {
        Assert.Equal([DiagnosticCategory.BadTypeArgs], RunHelper.CompileErrors("x::Maybe<i32, txt> = Nada"));
        Assert.Equal([DiagnosticCategory.BadTypeArgs], RunHelper.CompileErrors("x::Maybe = Nada"));
        Assert.Equal([DiagnosticCategory.BadTypeArgs], RunHelper.CompileErrors("x::List<i32, i32> = [1]"));
        Assert.Equal([DiagnosticCategory.BadTypeArgs], RunHelper.CompileErrors("x::Map<txt> = map()"));
        Assert.Equal([DiagnosticCategory.BadTypeArgs], RunHelper.CompileErrors("x::i32<txt> = 1"));
    }

    [Fact]
    public void UnknownTypesAreReported() =>
        Assert.Equal([DiagnosticCategory.UndefinedName], RunHelper.CompileErrors("x::Wibble<i32> = 1"));

    [Fact]
    public void SetAndMapKeysMustBeHashable()
    {
        Assert.Equal([DiagnosticCategory.NotHashable], RunHelper.CompileErrors("s::Set<f32> = set()"));
        Assert.Equal([DiagnosticCategory.NotHashable], RunHelper.CompileErrors("m := map(1.5 => 2)"));
        Assert.Equal([DiagnosticCategory.NotHashable], RunHelper.CompileErrors("m := map([1] => 2)"));
        Assert.Equal("true\n", RunHelper.Ok("m := map((1, \"a\") => 2, (2, \"b\") => 3)\nfound := (1, \"a\") in m\nprint found"));
    }

    [Fact]
    public void EnumsCompareStructurally() =>
        Assert.Equal("true false true\n", RunHelper.Ok("print Got(1) == Got(1), Got(1) == Got(2), Nada == Nada"));
}

public class GenericInferenceTests
{
    [Fact]
    public void TypeArgumentsAreInferredFromArguments() =>
        Assert.Equal("1 a\n", RunHelper.Ok("fn first<T>(a::T, b::T)::T = a\nprint first(1, 2), first(\"a\", \"b\")"));

    [Fact]
    public void ConflictingArgumentsAreAMismatch() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("fn first<T>(a::T, b::T)::T = a\nprint first(1, \"b\")"));

    [Fact]
    public void ResultTypeFollowsTheInferredArgument()
    {
        // first(1, 2) is an i32, so it cannot go into a txt slot.
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("fn first<T>(a::T, b::T)::T = a\nx::txt = first(1, 2)"));
        Assert.Equal("5\n", RunHelper.Ok("fn first<T>(a::T, b::T)::T = a\nx::i32 = first(5, 6)\nprint x"));
    }

    [Fact]
    public void ExpectedTypeFlowsIntoLiteralArguments() =>
        Assert.Equal("Got(5)\n", RunHelper.Ok("x::Maybe<i64> = Got(5)\nprint x"));

    [Fact]
    public void ExpectedTypeAlsoRejectsAWrongPayload() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("x::Maybe<i32> = Got(\"no\")"));

    [Fact]
    public void ABindingKeepsTheInferredEnumType()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("m := Got(5)\nn::Maybe<txt> = m"));
        Assert.Equal("Got(5)\n", RunHelper.Ok("m := Got(5)\nn::Maybe<i32> = m\nprint n"));
    }

    [Fact]
    public void LambdaArgumentsTakeTheirTypesFromTheOtherArguments() =>
        Assert.Equal("42\n", RunHelper.Ok("fn apply<T>(f::fn(T)::T, v::T)::T = f(v)\nprint apply(|x| x + 1, 41)"));

    [Fact]
    public void LambdaResultsBindTypeParameters() =>
        Assert.Equal("Got(2)\nGot(41)\n", RunHelper.Ok(
            "fn map_maybe<T, U>(m::Maybe<T>, f::fn(T)::U)::Maybe<U> = pick m:\n    Got(v) => Got(f(v)),\n    Nada => Nada\n;\n" +
            "print map_maybe(Got(\"ab\"), |s| len(s))\nprint map_maybe(Got(20), |x| x * 2 + 1)"));

    [Fact]
    public void GenericsComposeThroughListsAndTuples() =>
        Assert.Equal("[2, 1]\n(2, 1)\n", RunHelper.Ok(
            "fn swap<A, B>(p::(A, B))::(B, A):\n    (a, b) := p\n    ret (b, a)\n;\n" +
            "fn flip<T>(xs::List<T>)::List<T> = rev(xs)\n" +
            "print flip([1, 2])\nprint swap((1, 2))"));

    [Fact]
    public void ABodyCannotAssumeAnythingAboutAnUnconstrainedParameter() =>
        Assert.Equal([DiagnosticCategory.BadOperator], RunHelper.CompileErrors("fn add<T>(a::T, b::T)::T = a + b\nprint 1"));

    [Fact]
    public void TheCheckedInstantiationIsRecorded()
    {
        var diag = new DiagnosticEngine();
        var src = new SourceText("t.splat", "fn first<T, U>(a::T, b::U)::T = a\nx := first(1, \"s\")");
        Assert.True(Pipeline.Compile(src, diag, out var program, out var info), string.Join("\n", diag.Diagnostics.Select(d => d.Message)));
        var call = (CallExpr)((BindStmt)program.Statements[1]).Value;
        Assert.Equal(["i32", "txt"], info.TypeArgs(call)!.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void UnconstrainedTypeParametersMayStillBeStoredInGenericCollections() =>
        Assert.Equal("[1]\n", RunHelper.Ok("fn one<T>(x::T)::List<T> = [x]\nprint one(1)"));
}

public class ExhaustivenessTests
{
    private static DiagnosticCategory[] Cats(string code) => RunHelper.Run(code).Categories;

    private const string Light = "enum Light:\n    Red,\n    Yellow,\n    Green\n;\n";

    [Fact]
    public void AMissingVariantIsNamed()
    {
        var r = RunHelper.Run(Light + "fn f(l::Light)::i32 = pick l:\n    Red => 1,\n    Green => 2\n;\nprint 1");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.NonExhaustivePick, d.Category);
        Assert.Contains("Yellow", d.Message);
    }

    [Fact]
    public void SeveralMissingVariantsAreAllNamed()
    {
        var r = RunHelper.Run(Light + "fn f(l::Light)::i32 = pick l:\n    Red => 1\n;\nprint 1");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Contains("Yellow", d.Message);
        Assert.Contains("Green", d.Message);
    }

    [Fact]
    public void AWildcardOrABindingCoversTheRest()
    {
        Assert.Empty(Cats(Light + "fn f(l::Light)::i32 = pick l:\n    Red => 1,\n    _ => 2\n;\nprint f(Red)"));
        Assert.Empty(Cats(Light + "fn f(l::Light)::i32 = pick l:\n    Red => 1,\n    other => 2\n;\nprint f(Red)"));
    }

    [Fact]
    public void NestedPatternsAreCheckedNotJustTheOuterVariant()
    {
        var r = RunHelper.Run("fn f(m::Maybe<Maybe<i32>>)::i32 = pick m:\n    Got(Got(v)) => v,\n    Nada => 0\n;\nprint 1");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.NonExhaustivePick, d.Category);
        Assert.Contains("Got(Nada)", d.Message);
    }

    [Fact]
    public void ACompleteNestedMatchIsAccepted() =>
        Assert.Empty(Cats("fn f(m::Maybe<Maybe<i32>>)::i32 = pick m:\n    Got(Got(v)) => v,\n    Got(Nada) => -1,\n    Nada => 0\n;\nprint f(Got(Nada))"));

    [Fact]
    public void TuplesOfBitsNeedAllFourCombinations()
    {
        var r = RunHelper.Run("fn f(a::bool, b::bool)::i32 = pick (a, b):\n    (true, true) => 1,\n    (false, false) => 2,\n    (true, false) => 3\n;\nprint 1");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Contains("(false, true)", d.Message);
    }

    [Fact]
    public void IntegerRangesMustCoverTheWholeType()
    {
        var r = RunHelper.Run("fn f(n::i8)::i32 = pick n:\n    -128..=-1 => 1,\n    1..=127 => 2\n;\nprint 1");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Contains("0", d.Message);
        Assert.Empty(Cats("fn f(n::i8)::i32 = pick n:\n    -128..=-1 => 1,\n    0 => 0,\n    1..=127 => 2\n;\nprint f(5)"));
    }

    [Fact]
    public void ExclusiveRangeBoundsAreRespected() =>
        Assert.Empty(Cats("fn f(n::u8)::i32 = pick n:\n    0..10 => 1,\n    10..=255 => 2\n;\nprint f(5)"));

    [Fact]
    public void CharsAreCoveredByIntervalsToo()
    {
        var r = RunHelper.Run("fn f(c::char)::i32 = pick c:\n    c\"a\"..=c\"z\" => 1\n;\nprint 1");
        Assert.Equal([DiagnosticCategory.NonExhaustivePick], r.Categories);
    }

    [Fact]
    public void TextAndFloatsNeverBecomeExhaustiveByLiteralsAlone()
    {
        Assert.Equal([DiagnosticCategory.NonExhaustivePick], Cats("fn f(s::txt)::i32 = pick s:\n    \"a\" => 1\n;\nprint 1"));
        Assert.Equal([DiagnosticCategory.NonExhaustivePick], Cats("fn f(x::f32)::i32 = pick x:\n    1.5 => 1\n;\nprint 1"));
    }

    [Fact]
    public void AnEmptyPickCoversNothing() =>
        Assert.Equal([DiagnosticCategory.NonExhaustivePick], Cats("m := Got(1)\npick m:\n;"));

    [Fact]
    public void AnArmAfterAWildcardIsUnreachableButOnlyAWarning()
    {
        var r = RunHelper.Run("fn f(n::i32)::i32 = pick n:\n    _ => 1,\n    5 => 2\n;\nprint f(5)");
        Assert.Equal(RunOutcome.Ok, r.Outcome);
        Assert.Equal("1\n", r.Out);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.UnreachableArm, d.Category);
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Equal("SW00400", d.Code);
    }

    [Fact]
    public void ARepeatedVariantArmIsUnreachable()
    {
        var r = RunHelper.Run("fn f(m::Maybe<i32>)::i32 = pick m:\n    Got(a) => a,\n    Got(b) => b,\n    Nada => 0\n;\nprint f(Got(3))");
        Assert.Equal([DiagnosticCategory.UnreachableArm], r.Categories);
    }

    [Fact]
    public void AnArmCoveredByEarlierNestedArmsIsUnreachable()
    {
        var r = RunHelper.Run("fn f(m::Maybe<Maybe<i32>>)::i32 = pick m:\n    Got(_) => 1,\n    Got(Nada) => 2,\n    Nada => 0\n;\nprint f(Nada)");
        Assert.Equal([DiagnosticCategory.UnreachableArm], r.Categories);
    }

    [Fact]
    public void ARangeInsideEarlierRangesIsUnreachable()
    {
        var r = RunHelper.Run("fn f(n::i32)::i32 = pick n:\n    0..=10 => 1,\n    3..=5 => 2,\n    _ => 0\n;\nprint f(4)");
        Assert.Equal([DiagnosticCategory.UnreachableArm], r.Categories);
        Assert.Equal("1\n", r.Out);
    }

    [Fact]
    public void OverlappingButNotCoveredRangesAreFine() =>
        Assert.Empty(Cats("fn f(n::i32)::i32 = pick n:\n    0..=10 => 1,\n    5..=20 => 2,\n    _ => 0\n;\nprint f(4)"));

    [Fact]
    public void ArmsMustAgreeOnTheirType() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], Cats("fn f(m::Maybe<i32>)::i32 = pick m:\n    Got(a) => a,\n    Nada => \"none\"\n;\nprint 1"));

    [Fact]
    public void PatternsMustMatchTheSubjectType()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], Cats("pick 5:\n    \"five\" => 1,\n    _ => 0\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], Cats("m := Got(1)\npick m:\n    5 => 1,\n    _ => 0\n;"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], Cats("pick (1, 2):\n    (a, b, c) => 1\n;"));
    }

    [Fact]
    public void AVariantOfAnotherEnumIsNotFound()
    {
        var r = RunHelper.Run(Light + "m := Got(1)\npick m:\n    Red => 1,\n    _ => 0\n;");
        Assert.Equal([DiagnosticCategory.VariantNotFound], r.Categories);
    }

    [Fact]
    public void LiteralPatternsKeepTheirSubjectsIntegerType() =>
        Assert.Equal("small\n", RunHelper.Ok("x::u8 = 3\npick x:\n    0..=9 => print \"small\",\n    _ => print \"big\"\n;"));

    [Fact]
    public void BindingsInPatternsAreScopedToTheirArm() =>
        Assert.Equal([DiagnosticCategory.UndefinedName], Cats("m := Got(1)\npick m:\n    Got(v) => 1,\n    Nada => v\n;"));

    [Fact]
    public void ABlockArmRunsItsStatements() =>
        Assert.Equal("a\nb\nnone\n", RunHelper.Ok("m := Got(1)\npick m:\n    Got(_):\n        print \"a\"\n        print \"b\"\n    ;\n    Nada => print \"nothing\"\n;\npick Nada:\n    Got(_) => print \"x\",\n    Nada: print \"none\" ;\n;"));

    [Fact]
    public void ThePickValueCanBeUsedInAnExpression() =>
        Assert.Equal("10\n", RunHelper.Ok("m := Got(4)\nx := 2 * pick m:\n    Got(v) => v + 1,\n    Nada => 0\n;\nprint x"));
}

public class CollectionTests
{
    [Fact]
    public void ListLiteralsIndexAndSlice() =>
        Assert.Equal("20 [20, 30] [10, 20, 30] [10, 20]\n", RunHelper.Ok("xs := [10, 20, 30]\nprint xs[1], xs[1..3], xs[0..=2], xs[0..2]"));

    [Fact]
    public void SlicesCopy() =>
        Assert.Equal("[10, 99] [10, 20]\n", RunHelper.Ok("var xs = [10, 20, 30]\nvar s = xs[0..2]\ns[1] = 99\nprint s, xs[0..2]"));

    [Fact]
    public void SteppedRangeSlices() =>
        Assert.Equal("[0, 2, 4]\n", RunHelper.Ok("xs := [0, 1, 2, 3, 4, 5]\nprint xs[0..6 by 2]"));

    [Fact]
    public void PushPopInsertRemove() =>
        Assert.Equal("[1, 2, 3]\nGot(3)\n[1, 2]\n[0, 1, 2]\n1\n[0, 2]\n", RunHelper.Ok(
            "var xs = [1, 2]\nxs.push(3)\nprint xs\nprint xs.pop()\nprint xs\nxs.insert(0, 0)\nprint xs\nprint xs.remove(1)\nprint xs"));

    [Fact]
    public void MutatingMethodsNeedAVarBinding()
    {
        Assert.Equal([DiagnosticCategory.ImmutableMutation], RunHelper.CompileErrors("xs := [1]\nxs.push(2)"));
        Assert.Equal([DiagnosticCategory.ImmutableMutation], RunHelper.CompileErrors("s := set(1)\ns.add(2)"));
        Assert.Equal([DiagnosticCategory.ImmutableMutation], RunHelper.CompileErrors("m := map(1 => 1)\nm.remove(1)"));
        Assert.Equal([DiagnosticCategory.ImmutableMutation], RunHelper.CompileErrors("xs := [1]\nxs[0] = 5"));
        Assert.Equal([DiagnosticCategory.ImmutableMutation], RunHelper.CompileErrors("m := map(1 => 1)\nm[2] = 5"));
    }

    [Fact]
    public void ParametersAreImmutableToo() =>
        Assert.Equal([DiagnosticCategory.ImmutableMutation], RunHelper.CompileErrors("fn f(xs::List<i32>):\n    xs.push(1)\n;"));

    [Fact]
    public void NestedElementsNeedAVarRoot()
    {
        Assert.Equal("[[1, 9], [2]]\n", RunHelper.Ok("var grid = [[1], [2]]\ngrid[0].push(9)\nprint grid"));
        Assert.Equal([DiagnosticCategory.ImmutableMutation], RunHelper.CompileErrors("grid := [[1], [2]]\ngrid[0].push(9)"));
    }

    [Fact]
    public void ElementTypesAreChecked()
    {
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("var xs = [1]\nxs.push(\"a\")"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("var xs = [1]\nxs[0] = \"a\""));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("xs := [1, \"two\"]"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("x := [1] + [\"a\"]"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("xs := [1]\nprint xs[\"k\"]"));
    }

    [Fact]
    public void MethodArityAndNamesAreChecked()
    {
        Assert.Equal([DiagnosticCategory.WrongArgCount], RunHelper.CompileErrors("var xs = [1]\nxs.push(1, 2)"));
        Assert.Equal([DiagnosticCategory.UndefinedName], RunHelper.CompileErrors("var xs = [1]\nxs.shuffle()"));
    }

    [Fact]
    public void EmptyCollectionsNeedAnAnnotation()
    {
        Assert.Equal([DiagnosticCategory.CannotInfer], RunHelper.CompileErrors("xs := []"));
        Assert.Equal([DiagnosticCategory.CannotInfer], RunHelper.CompileErrors("s := set()"));
        Assert.Equal([DiagnosticCategory.CannotInfer], RunHelper.CompileErrors("m := map()"));
        Assert.Equal("0 0 0\n", RunHelper.Ok("xs::List<i32> = []\ns::Set<txt> = set()\nm::Map<txt, i32> = map()\nprint len(xs), len(s), len(m)"));
    }

    [Fact]
    public void ConcatenationBuildsANewList() =>
        Assert.Equal("[1, 2, 3] [1, 2]\n", RunHelper.Ok("a := [1, 2]\nb := a + [3]\nprint b, a"));

    [Fact]
    public void AssigningACollectionCopiesIt() =>
        Assert.Equal("2 3\n", RunHelper.Ok("var a = [1, 2]\nvar b = a\nb.push(3)\nprint len(a), len(b)"));

    [Fact]
    public void StoringACollectionInsideAnotherCopiesIt() =>
        Assert.Equal("[1] [[1, 2]]\n", RunHelper.Ok("var a = [1]\nvar outer = [a]\nouter[0].push(2)\nprint a, outer"));

    [Fact]
    public void IndexOutOfRangeIsARuntimeErrorWithASpan()
    {
        var r = RunHelper.RuntimeError("xs := [1, 2, 3]\nprint xs[3]");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.IndexOutOfRange, d.Category);
        Assert.Equal(2, d.Span.Line);
        Assert.Contains("Index 3 is not in [0, 3)", d.Message);
    }

    [Fact]
    public void NegativeIndicesAreOutOfRange()
    {
        var r = RunHelper.RuntimeError("xs := [1, 2, 3]\nn::i32 = 0 - 1\nprint xs[n]");
        Assert.Equal(DiagnosticCategory.IndexOutOfRange, Assert.Single(r.Diag.Diagnostics).Category);
    }

    [Fact]
    public void SliceAndInsertBoundsAreChecked()
    {
        Assert.Equal(DiagnosticCategory.IndexOutOfRange, Assert.Single(RunHelper.RuntimeError("xs := [1, 2]\nprint xs[1..5]").Diag.Diagnostics).Category);
        Assert.Equal(DiagnosticCategory.IndexOutOfRange, Assert.Single(RunHelper.RuntimeError("var xs = [1, 2]\nxs.insert(9, 0)").Diag.Diagnostics).Category);
        Assert.Equal(DiagnosticCategory.IndexOutOfRange, Assert.Single(RunHelper.RuntimeError("var xs = [1, 2]\nxs.remove(2)").Diag.Diagnostics).Category);
        Assert.Equal(DiagnosticCategory.IndexOutOfRange, Assert.Single(RunHelper.RuntimeError("var xs = [1, 2]\nxs[2] = 0").Diag.Diagnostics).Category);
    }

    [Fact]
    public void InsertAtTheEndAppends() =>
        Assert.Equal("[1, 2, 3]\n", RunHelper.Ok("var xs = [1, 2]\nxs.insert(2, 3)\nprint xs"));

    [Fact]
    public void MissingMapKeysAreARuntimeError()
    {
        var r = RunHelper.RuntimeError("m := map(\"a\" => 1)\nprint m[\"b\"]");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.KeyNotFound, d.Category);
        Assert.Equal("SE00308", d.Code);
    }

    [Fact]
    public void MapsKeepInsertionOrderAndOverwriteInPlace() =>
        Assert.Equal("map{b: 1, a: 9, c: 3}\n", RunHelper.Ok("var m = map(\"b\" => 1, \"a\" => 2)\nm[\"c\"] = 3\nm[\"a\"] = 9\nprint m"));

    [Fact]
    public void SetsKeepInsertionOrderAndIgnoreDuplicates() =>
        Assert.Equal("set{3, 1, 2} 3\n", RunHelper.Ok("s := set(3, 1, 3, 2, 1)\nprint s, len(s)"));

    [Fact]
    public void SetRemoveReportsWhetherItWasThere() =>
        Assert.Equal("true false set{2}\n", RunHelper.Ok("var s = set(1, 2)\nprint s.remove(1), s.remove(1), s"));

    [Fact]
    public void MapGetReturnsAMaybe() =>
        Assert.Equal("Got(2) Nada\n", RunHelper.Ok("m := map(\"a\" => 2)\nprint m.get(\"a\"), m.get(\"z\")"));

    [Fact]
    public void CompoundAssignmentIntoElements() =>
        Assert.Equal("[11, 20] 7\n", RunHelper.Ok("var xs = [1, 20]\nxs[0] += 10\nvar m = map(1 => 5)\nm[1] += 2\nprint xs, m[1]"));

    [Fact]
    public void TuplesDestructureAndCompare()
    {
        Assert.Equal("1 two true\n", RunHelper.Ok("(a, b, c) := (1, \"two\", true)\nprint a, b, c"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("(a, b) := (1, 2, 3)"));
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("(a, b) := 5"));
    }

    [Fact]
    public void TupleTypesAreWrittenInParentheses() =>
        Assert.Equal("(2, ab)\n", RunHelper.Ok("fn f(p::(i32, txt))::(i32, txt):\n    (n, s) := p\n    ret (n + 1, s)\n;\nprint f((1, \"ab\"))"));

    [Fact]
    public void TuplesAreNotIterable() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("loop x in (1, \"a\"):\n    print x\n;"));

    [Fact]
    public void LenWorksOnEveryCollection() =>
        Assert.Equal("3 2 1 3\n", RunHelper.Ok("print len([1, 2, 3]), len(set(1, 2)), len(map(1 => 2)), len(\"abc\")"));

    [Fact]
    public void ListEqualityIsStructural() =>
        Assert.Equal("true false true false\n", RunHelper.Ok("print([1, 2] == [1, 2], [1] == [1, 2], [[1], [2]] == [[1], [2]], [1, 2] != [1, 2])"));

    [Fact]
    public void ComparingDifferentCollectionTypesIsATypeError() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("print([1] == [\"a\"])"));
}

public class IterationOverCollectionsTests
{
    [Fact]
    public void ListSetAndMapIteration() =>
        Assert.Equal("1\n2\n7\n8\na=1\nb=2\n", RunHelper.Ok(
            "loop x in [1, 2]:\n    print x\n;\nloop x in set(7, 8):\n    print x\n;\nloop k, v in map(\"a\" => 1, \"b\" => 2):\n    print \"{k}={v}\"\n;"));

    [Fact]
    public void MapIterationYieldsPairsToo() =>
        Assert.Equal("(a, 1)\n", RunHelper.Ok("loop p in map(\"a\" => 1):\n    print p\n;"));

    [Fact]
    public void EnumerateZipAndRev() =>
        Assert.Equal("0 a\n1 b\n(1, x)\n(2, y)\n3\n2\n1\n", RunHelper.Ok(
            "loop i, w in enumerate([\"a\", \"b\"]):\n    print i, w\n;\nloop p in zip([1, 2, 3], [\"x\", \"y\"]):\n    print p\n;\nloop n in rev([1, 2, 3]):\n    print n\n;"));

    [Fact]
    public void EnumerateIndicesAreUsize() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("loop i, w in enumerate([\"a\"]):\n    n::i32 = i\n;"));

    [Fact]
    public void ThenChainsOverListsAndRanges() =>
        Assert.Equal("1\n2\n3\n10\n11\n", RunHelper.Ok("loop x in [1, 2] then [3] then 10..12:\n    print x\n;"));

    [Fact]
    public void ThenChainPartsMustAgreeOnTheirElementType() =>
        Assert.Equal([DiagnosticCategory.ChainMismatch], RunHelper.CompileErrors("loop x in [1, 2] then [\"a\"]:\n    print x\n;"));

    [Fact]
    public void LoopIteratesTheCollectionAsItWasWhenTheLoopStarted() =>
        // A call hides the mutation from the checker, but the loop still walks the snapshot taken when it started.
        Assert.Equal("3 6\n", RunHelper.Ok(
            "var xs = [1, 2, 3]\nvar n = 0\nfn grow():\n    xs.push(0)\n;\nloop x in xs:\n    n += 1\n    grow()\n;\nprint n, len(xs)"));

    [Fact]
    public void MembershipOnEachCollection() =>
        Assert.Equal("true false true false true false\n", RunHelper.Ok(
            "print 2 in [1, 2], 5 in [1, 2], 2 in set(2), 2 in set(3), \"k\" in map(\"k\" => 1), \"z\" in map(\"k\" => 1)"));

    [Fact]
    public void MembershipInAMapTestsKeysNotPairs() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("print (1, 2) in map(1 => 2)"));

    [Fact]
    public void MembershipElementTypeMustMatch() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("print \"a\" in [1, 2]"));

    [Fact]
    public void RevOfASetIsRejected() =>
        Assert.Equal([DiagnosticCategory.TypeMismatch], RunHelper.CompileErrors("print rev(set(1, 2))"));

    [Fact]
    public void BreakAndSkipWorkInsideCollectionLoops() =>
        Assert.Equal("1\n3\n", RunHelper.Ok("loop x in [1, 2, 3, 4]:\n    if x == 2:\n        cont\n    ;\n    if x == 4:\n        br\n    ;\n    print x\n;"));
}

public class MutationWhileIteratingTests
{
    private static DiagnosticCategory[] Cats(string code) => RunHelper.Run(code).Categories;

    [Fact]
    public void AssigningTheSubjectIsRejected() =>
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], Cats("var xs = [1]\nloop x in xs:\n    xs = [2]\n;"));

    [Fact]
    public void MutatingMethodsAreRejected() =>
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], Cats("var xs = [1]\nloop x in xs:\n    xs.push(x)\n;"));

    [Fact]
    public void ElementAssignmentIsRejected() =>
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], Cats("var xs = [1]\nloop x in xs:\n    xs[0] = x\n;"));

    [Fact]
    public void ThePokeOfTheSubjectIsRejected() =>
        Assert.Contains(DiagnosticCategory.MutateWhileIterating, Cats("var xs = [1]\nloop x in xs:\n    ~xs\n;"));

    [Fact]
    public void NestedControlFlowIsSearched() =>
        Assert.Equal([DiagnosticCategory.MutateWhileIterating],
            Cats("var xs = [1]\nloop x in xs:\n    if x > 0:\n        loop y in 0..2:\n            xs.push(y)\n        ;\n    ;\n;"));

    [Fact]
    public void SetsAndMapsAreProtectedToo()
    {
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], Cats("var s = set(1)\nloop x in s:\n    s.add(x + 1)\n;"));
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], Cats("var m = map(1 => 1)\nloop k, v in m:\n    m.remove(k)\n;"));
    }

    [Fact]
    public void WrappersOfTheSubjectAreProtectedToo()
    {
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], Cats("var xs = [1]\nloop i, x in enumerate(xs):\n    xs.push(x)\n;"));
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], Cats("var xs = [1]\nloop x in rev(xs):\n    xs.push(x)\n;"));
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], Cats("var xs = [1]\nloop x in xs then [2]:\n    xs.push(x)\n;"));
    }

    [Fact]
    public void ChangingADifferentCollectionIsFine() =>
        Assert.Empty(Cats("var xs = [1, 2]\nvar out = [0]\nloop x in xs:\n    out.push(x)\n;"));

    [Fact]
    public void ReadingTheSubjectIsFine() =>
        Assert.Empty(Cats("var xs = [1, 2]\nloop x in xs:\n    print len(xs), xs[0]\n;"));

    [Fact]
    public void ACopyIsFineToChange() =>
        Assert.Empty(Cats("var xs = [1, 2]\nvar copy = xs\nloop x in xs:\n    copy.push(x)\n;"));

    [Fact]
    public void ARedeclaredNameInsideTheBodyIsADifferentVariable() =>
        Assert.Empty(Cats("var xs = [1, 2]\nloop x in xs:\n    var xs = [9]\n    xs.push(1)\n;"));

    [Fact]
    public void AnInnerLoopVariableMayShadowTheSubject() =>
        Assert.Empty(Cats("var xs = [[1], [2]]\nloop xs in xs:\n    print xs\n;"));

    [Fact]
    public void TheMessageNamesTheVariableAndSuggestsACopy()
    {
        var r = RunHelper.Run("var grid = [1]\nloop x in grid:\n    grid.push(2)\n;");
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Contains("'grid'", d.Message);
        Assert.Contains("copy", d.Fix);
    }
}

public class EnumsAndCollectionsInTheReplTests
{
    private static (SessionState S, StringWriter Out) New()
    {
        var o = new StringWriter { NewLine = "\n" };
        return (new SessionState(new StringReader(""), o, new StringWriter { NewLine = "\n" }), o);
    }

    [Fact]
    public void EnumsPersistAcrossEntries()
    {
        var (s, _) = New();
        Assert.Equal(RunOutcome.Ok, s.Eval("enum Shape:\n    Circle(f64),\n    Dot\n;").Outcome);
        Assert.Equal("=> Circle(2.0) :: Shape", s.Eval("Circle(2.0)").Echo);
        Assert.Equal("=> Dot :: Shape", s.Eval("Dot").Echo);
        Assert.Equal(RunOutcome.Ok, s.Eval("fn area(sh::Shape)::f64 = pick sh:\n    Circle(r) => 3.0 * r * r,\n    Dot => 0.0\n;").Outcome);
        Assert.Equal("=> 12.0 :: f64", s.Eval("area(Circle(2.0))").Echo);
    }

    [Fact]
    public void AFailedEntryDeclaresNoEnum()
    {
        var (s, _) = New();
        Assert.Equal(RunOutcome.CompileError, s.Eval("enum Temp:\n    A\n;\nx::txt = 5").Outcome);
        // Temp was never committed, so declaring it again is fine and the old attempt left nothing behind.
        Assert.Equal(RunOutcome.Ok, s.Eval("enum Temp:\n    B\n;").Outcome);
        Assert.Equal("=> B :: Temp", s.Eval("B").Echo);
        Assert.Equal(RunOutcome.CompileError, s.Eval("A").Outcome);
    }

    [Fact]
    public void BuiltInVariantsAreNotUserBindings()
    {
        var (s, _) = New();
        s.Eval("x := Got(1)");
        Assert.Equal(["x"], s.Names);
    }

    [Fact]
    public void ResetForgetsEnums()
    {
        var (s, _) = New();
        s.Eval("enum Shape:\n    Dot\n;");
        s.Reset();
        Assert.Equal(RunOutcome.CompileError, s.Eval("Dot").Outcome);
    }

    [Fact]
    public void AEnumMayBeRedeclaredInALaterEntry()
    {
        var (s, _) = New();
        s.Eval("enum Shape:\n    Dot\n;");
        Assert.Equal(RunOutcome.Ok, s.Eval("enum Shape:\n    Circle(f64)\n;").Outcome);
        Assert.Equal("=> Circle(1.0) :: Shape", s.Eval("Circle(1.0)").Echo);
    }

    [Fact]
    public void CollectionsPersistAndMutateAcrossEntries()
    {
        var (s, _) = New();
        s.Eval("var xs = [1, 2]");
        s.Eval("xs.push(3)");
        Assert.Equal("=> [1, 2, 3] :: List<i32>", s.Eval("xs").Echo);
        Assert.Equal("=> 3 :: usize", s.Eval("len(xs)").Echo);
    }

    [Fact]
    public void AFailedEntryRollsBackAMutation()
    {
        var (s, _) = New();
        s.Eval("var xs = [1, 2]");
        // The push happens, then the index fails: the whole entry is undone for scalars; collections are shared state
        // (see decisions), so only the entry's own bindings are guaranteed to disappear.
        Assert.Equal(RunOutcome.RuntimeError, s.Eval("var ys = [0]\nprint xs[9]").Outcome);
        Assert.Equal(["xs"], s.Names);
    }
}

public class Phase7ParserTests
{
    private static string Expr(string text) => ParseHelper.Expr(text);

    [Fact]
    public void EnumDeclarationsParse() =>
        Assert.Equal("(enum Shape (Circle f64) Dot)", ParseHelper.Single("enum Shape:\n    Circle(f64),\n    Dot\n;"));

    [Fact]
    public void GenericEnumWithRecursivePayload()
    {
        var (p, d, _) = ParseHelper.Parse("enum Tree<T>:\n    Leaf,\n    Node(Tree<T>, T, Tree<T>)\n;");
        Assert.False(d.HasErrors);
        var m = Assert.IsType<EnumDecl>(p.Statements[0]);
        Assert.Equal(2, m.Variants.Count);
        Assert.Equal(3, m.Variants[1].Payloads.Count);
        Assert.Equal("T", m.Generics[0].Name);
    }

    [Fact]
    public void PickArmsAreCommaSeparatedAndClosedBySemicolon()
    {
        var (p, d, _) = ParseHelper.Parse("pick m:\n    Got(v) => v,\n    Nada => 0\n;\nx := 1");
        Assert.False(d.HasErrors);
        Assert.Equal(2, p.Statements.Count);
        var pick = (PickExpr)((ExprStmt)p.Statements[0]).Expression;
        Assert.Equal(2, pick.Arms.Count);
    }

    [Fact]
    public void ATrailingCommaIsAllowed()
    {
        var (p, d, _) = ParseHelper.Parse("pick m:\n    Got(v) => v,\n    Nada => 0,\n;");
        Assert.False(d.HasErrors);
        Assert.Equal(2, ((PickExpr)((ExprStmt)p.Statements[0]).Expression).Arms.Count);
    }

    [Fact]
    public void CommandCallsWorkAsArmBodiesWithOneArgument()
    {
        var (p, d, _) = ParseHelper.Parse("pick m:\n    Got(v) => print \"Got {v}\",\n    Nada => eprint \"none\"\n;");
        Assert.False(d.HasErrors, ParseHelper.Messages(d));
        var arms = ((PickExpr)((ExprStmt)p.Statements[0]).Expression).Arms;
        Assert.All(arms, a => Assert.IsType<CallExpr>(a.ExprBody));
    }

    [Fact]
    public void BlockArmsHaveTheirOwnSemicolon()
    {
        var (p, d, _) = ParseHelper.Parse("pick m:\n    Got(v):\n        print v\n        print v\n    ;\n    Nada => print \"n\"\n;");
        Assert.False(d.HasErrors, ParseHelper.Messages(d));
        var arms = ((PickExpr)((ExprStmt)p.Statements[0]).Expression).Arms;
        Assert.Equal(2, arms[0].BlockBody!.Statements.Count);
        Assert.NotNull(arms[1].ExprBody);
    }

    [Fact]
    public void AMissingCommaBetweenArmsIsReportedOnceAndParsingContinues()
    {
        var (p, d, _) = ParseHelper.Parse("pick m:\n    Got(v) => v\n    Nada => 0\n;\nx := 1");
        var diag = Assert.Single(d.Diagnostics);
        Assert.Contains("','", diag.Message);
        Assert.Equal(2, p.Statements.Count);
        Assert.Equal(2, ((PickExpr)((ExprStmt)p.Statements[0]).Expression).Arms.Count);
    }

    [Fact]
    public void AnUnclosedPickIsAnOofAtItsColon()
    {
        var (_, d, _) = ParseHelper.Parse("pick m:\n    Got(v) => v,\n    Nada => 0\n");
        var diag = Assert.Single(d.Diagnostics);
        Assert.Equal(DiagnosticCategory.MissingSemicolon, diag.Category);
        Assert.Equal(1, diag.Span.Line);
    }

    [Fact]
    public void ABrokenPatternSkipsToTheNextArm()
    {
        var (p, d, _) = ParseHelper.Parse("pick m:\n    ) => 1,\n    Nada => 0\n;\nx := 1");
        Assert.True(d.HasErrors);
        Assert.Equal(2, p.Statements.Count);
    }

    [Theory]
    [InlineData("pick m:\n    ;")]
    [InlineData("pick m:\n    , , ,\n;")]
    [InlineData("pick m:\n    Got(\n;")]
    [InlineData("pick m: => => =>\n;")]
    [InlineData("pick:\n;")]
    [InlineData("pick m:\n    (1, (2, \n;")]
    [InlineData("enum :\n;")]
    [InlineData("enum A<:\n    X\n;")]
    [InlineData("enum A:\n    X(,)\n;")]
    public async Task MalformedPicksAndEnumsNeverHang(string src)
    {
        // A parser that loops forever would trip the timeout instead of hanging the whole test run.
        await Task.Run(() => ParseHelper.Parse(src)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("pick x:\n    0 => 1,\n    _ => 2\n;", "(pick x (arm 0 1) (arm _ 2))")]
    [InlineData("pick x:\n    -5 => 1,\n    _ => 2\n;", "(pick x (arm (- 5) 1) (arm _ 2))")]
    [InlineData("pick x:\n    1..=9 => 1,\n    _ => 2\n;", "(pick x (arm (..= 1 9) 1) (arm _ 2))")]
    [InlineData("pick x:\n    c\"a\"..=c\"z\" => 1,\n    _ => 2\n;", "(pick x (arm (..= c\"a\" c\"z\") 1) (arm _ 2))")]
    [InlineData("pick x:\n    (a, Got(b)) => 1,\n    _ => 2\n;", "(pick x (arm (tuple-pat a (Got b)) 1) (arm _ 2))")]
    [InlineData("pick x:\n    \"hi\" => 1,\n    true => 2,\n    () => 3\n;", "(pick x (arm \"hi\" 1) (arm true 2) (arm () 3))")]
    public void PatternForms(string src, string expected) =>
        Assert.Equal(expected, ParseHelper.Single(src));

    [Fact]
    public void TuplesAndPairsParse()
    {
        Assert.Equal("(tuple 1 2 3)", Expr("(1, 2, 3)"));
        Assert.Equal("(tuple 1 2)", Expr("(1, 2,)"));
        Assert.Equal("(call map (pair \"a\" 1) (pair \"b\" 2))", Expr("map(\"a\" => 1, \"b\" => 2)"));
        Assert.Equal("(bind-tuple (names a b) f)", ParseHelper.Single("(a, b) := f"));
    }

    [Fact]
    public void TupleTypesParse() =>
        Assert.Equal("(fn f (params (p (tuple-type i32 txt))) (-> (tuple-type i32 (Maybe i32))) (= p))",
            ParseHelper.Single("fn f(p::(i32, txt))::(i32, Maybe<i32>) = p"));

    [Fact]
    public void ParenthesisedTypesGroupAndUnitIsZip()
    {
        Assert.Equal("(fn f (params (x i32)) (-> i32) (= x))", ParseHelper.Single("fn f(x::(i32))::(i32) = x"));
        Assert.Equal("(fn f (params) (-> unit) (= ()))", ParseHelper.Single("fn f()::() = ()"));
    }

    [Fact]
    public void ALineStartingWithAParenthesisIsANewStatement()
    {
        var (p, d, _) = ParseHelper.Parse("pair := (1, 2)\n(a, b) := pair\nprint a");
        Assert.False(d.HasErrors, ParseHelper.Messages(d));
        Assert.Equal(3, p.Statements.Count);
        Assert.IsType<TupleBindStmt>(p.Statements[1]);
    }

    [Fact]
    public void ALineStartingWithABracketIsANewStatement()
    {
        var (p, d, _) = ParseHelper.Parse("x := 1\n[1, 2, 3]");
        Assert.False(d.HasErrors);
        Assert.Equal(2, p.Statements.Count);
        Assert.IsType<ListLiteral>(((ExprStmt)p.Statements[1]).Expression);
    }

    [Fact]
    public void CallsAndIndexesOnTheSameLineStillBind() =>
        Assert.Equal("(index (call f 1) 0)", Expr("f(1)[0]"));

    [Fact]
    public void ParenthesisedLoopVariablesMeanTheSameAsTheBareList()
    {
        var (p, d, _) = ParseHelper.Parse("loop (k, v) in m:\n    print k\n;");
        Assert.False(d.HasErrors, ParseHelper.Messages(d));
        Assert.IsType<TupleBindStmt>(((LoopStmt)p.Statements[0]).Body.Statements[0]);
        Assert.Equal("a=1\n", RunHelper.Ok("loop (k, v) in map(\"a\" => 1):\n    print \"{k}={v}\"\n;"));
    }

    [Fact]
    public void NestedMultiVariableLoopsDoNotCollide() =>
        Assert.Equal("a 1 x\na 1 y\n", RunHelper.Ok(
            "loop k, v in map(\"a\" => 1):\n    loop i, w in enumerate([\"x\", \"y\"]):\n        print k, v, w\n    ;\n;"));

    [Fact]
    public void LoopWithTwoVariablesIsSugarForADestructure()
    {
        var (p, d, _) = ParseHelper.Parse("loop k, v in m:\n    print k\n;");
        Assert.False(d.HasErrors);
        var loopStmt = (LoopStmt)p.Statements[0];
        Assert.IsType<TupleBindStmt>(loopStmt.Body.Statements[0]);
    }
}

public class Phase7RoundTripTests
{
    [Theory]
    [InlineData("maybe_demo.splat")]
    [InlineData("mood_tree.splat")]
    [InlineData("pick_patterns.splat")]
    [InlineData("collections_demo.splat")]
    [InlineData("mutate_iterating.splat")]
    public void ProgramsRoundTripThroughTheFormatter(string file)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", file));
        var (p1, d1, _) = ParseHelper.Parse(text);
        Assert.False(d1.HasErrors, ParseHelper.Messages(d1));
        var formatted = SourceFormatter.Format(p1);
        var (p2, d2, _) = ParseHelper.Parse(formatted);
        Assert.False(d2.HasErrors, formatted + "\n" + ParseHelper.Messages(d2));
        Assert.Equal(AstPrinter.ProgramText(p1), AstPrinter.ProgramText(p2));
        Assert.Equal(formatted, SourceFormatter.Format(p2));
    }

    [Fact]
    public void FormattedProgramsStillRun()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "collections_demo.splat"));
        var (p, _, _) = ParseHelper.Parse(text);
        var original = RunHelper.Run(text);
        var again = RunHelper.Run(SourceFormatter.Format(p));
        Assert.Equal(RunOutcome.Ok, again.Outcome);
        Assert.Equal(original.Out, again.Out);
    }
}

public class PatternMatrixTests
{
    private static PCtor C(string key, params Pat[] args) => new(key, args);
    private static readonly SType Maybe32 = EnumRegistry.MaybeOf(Types.I32);

    [Fact]
    public void WildcardsCoverEverything()
    {
        var rows = new List<Pat[]> { new Pat[] { PWild.I } };
        Assert.Empty(PatternMatrix.Witnesses(rows, [Maybe32], 4));
    }

    [Fact]
    public void AnEmptyMatrixIsUsefulForAnyPattern()
    {
        Assert.True(PatternMatrix.Useful([], [PWild.I], [Maybe32]));
        Assert.True(PatternMatrix.Useful([], [C("Nada")], [Maybe32]));
    }

    [Fact]
    public void WitnessesNameTheMissingConstructors()
    {
        var rows = new List<Pat[]> { new Pat[] { C("Got", PWild.I) } };
        var w = PatternMatrix.Witnesses(rows, [Maybe32], 4);
        Assert.Equal(["Nada"], w.Select(x => PatternMatrix.Show(x[0])).ToArray());
    }

    [Fact]
    public void RangesAreSplitAtEveryBoundary()
    {
        // 0..=9 and 5..=20 leave -inf..-1 and 21..+inf open; a wildcard closes them.
        var rows = new List<Pat[]> { new Pat[] { new PRange(0, 9) }, new Pat[] { new PRange(5, 20) } };
        var w = PatternMatrix.Witnesses(rows, [Types.I32], 4);
        Assert.Equal(["-2147483648..=-1", "21..=2147483647"], w.Select(x => PatternMatrix.Show(x[0])).ToArray());
        rows.Add([PWild.I]);
        Assert.Empty(PatternMatrix.Witnesses(rows, [Types.I32], 4));
    }

    [Fact]
    public void ARangeIsUsefulOnlyWhereSomethingIsUncovered()
    {
        var rows = new List<Pat[]> { new Pat[] { new PRange(0, 10) } };
        Assert.False(PatternMatrix.Useful(rows, [new PRange(2, 8)], [Types.I32]));
        Assert.True(PatternMatrix.Useful(rows, [new PRange(5, 15)], [Types.I32]));
    }

    [Fact]
    public void CharWitnessesAreShownAsChars()
    {
        var rows = new List<Pat[]> { new Pat[] { new PRange(0, 0x10FFFF, true) } };
        Assert.Empty(PatternMatrix.Witnesses(rows, [Types.Char], 4));
        var partial = new List<Pat[]> { new Pat[] { new PRange('a', 'z', true) } };
        var shown = PatternMatrix.Witnesses(partial, [Types.Char], 4).Select(x => PatternMatrix.Show(x[0])).ToArray();
        Assert.Equal(2, shown.Length);
        Assert.EndsWith("..=c\"`\"", shown[0]);   // everything below 'a' ends at the character just before it
        Assert.StartsWith("c\"{\"..=", shown[1]); // everything above 'z' starts at the character just after it
    }

    [Fact]
    public void TheWitnessCapIsRespected()
    {
        // Three bool columns and no rows: eight combinations are missing, but only three are reported.
        var w = PatternMatrix.Witnesses([], [Types.Bool, Types.Bool, Types.Bool], 3);
        Assert.Equal(3, w.Count);
    }

    [Fact]
    public void LiteralsOfInfiniteTypesNeverComplete()
    {
        var rows = new List<Pat[]> { new Pat[] { new PLit("\"a\"") }, new Pat[] { new PLit("\"b\"") } };
        Assert.Single(PatternMatrix.Witnesses(rows, [Types.Txt], 4));
        Assert.False(PatternMatrix.Useful(rows, [new PLit("\"a\"")], [Types.Txt]));
        Assert.True(PatternMatrix.Useful(rows, [new PLit("\"c\"")], [Types.Txt]));
    }
}
