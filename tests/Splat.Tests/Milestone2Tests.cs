using System.Runtime.CompilerServices;
using Splat.Core;
using Splat.Runtime;

namespace Splat.Tests;

/// <summary>Every golden date test pins the locale, because the default follows the machine.</summary>
internal static class PinLocale
{
    [ModuleInitializer]
    internal static void Init() => Temporal.Locale.Default = Temporal.Locale.EnUs;
}

public class Milestone2GoldenTests
{
    [Theory]
    [InlineData("oop_animals.splat")]
    [InlineData("oop_vec2.splat")]
    [InlineData("oop_drop.splat")]
    [InlineData("traits_extend.splat")]
    [InlineData("iterable_class.splat")]
    [InlineData("try_catch.splat")]
    [InlineData("files_json.splat")]
    [InlineData("datetime.splat")]
    [InlineData("net_local.splat")]
    public void ProgramsRunAndMatchTheirExpectedOutput(string file)
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, file), file);
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Assert.Empty(r.Diag.Diagnostics);
        Golden.Compare(Golden.Dir, Path.ChangeExtension(file, ".out"), r.Out);
    }

    [Theory]
    [InlineData("bad_secret.splat", RunOutcome.CompileError)]
    [InlineData("bad_oop.splat", RunOutcome.CompileError)]
    [InlineData("bad_traits.splat", RunOutcome.CompileError)]
    [InlineData("bad_time.splat", RunOutcome.CompileError)]
    [InlineData("bad_date_literals.splat", RunOutcome.CompileError)]
    [InlineData("bad_file_uncaught.splat", RunOutcome.RuntimeError)]
    public void BadProgramsReportTheirDiagnostics(string file, RunOutcome expected)
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, file), file);
        Assert.Equal(expected, r.Outcome);
        if (expected == RunOutcome.CompileError) Assert.Equal("", r.Out);
        Assert.All(r.Diag.Diagnostics, d => Assert.Matches("^S[EW][0-9]{5}$", d.Code));
        Golden.Compare(Golden.Dir, Path.ChangeExtension(file, ".err"), r.Rendered);
    }
}

public class ClassTests
{
    [Fact]
    public void InitRunsParentFirstWhenNotCalledExplicitly() =>
        Assert.Equal("A\nB\n", RunHelper.Ok(
            "class A:\n fn init(self):\n  print \"A\"\n ;\n;\nclass B from A:\n fn init(self):\n  print \"B\"\n ;\n;\nb := B()"));

    [Fact]
    public void ChildInheritsParentInitWhenItHasNone() =>
        Assert.Equal("7\n", RunHelper.Ok(
            "class A:\n n::i32\n fn init(self, n::i32):\n  self.n = n\n ;\n;\nclass B from A:\n;\nb := B(7)\nprint b.n"));

    [Fact]
    public void VirtualDispatchPicksTheObjectsOwnMethod() =>
        Assert.Equal("woof\nsound\n", RunHelper.Ok(
            "class A:\n fn s(self)::txt = \"sound\"\n;\nclass D from A:\n fn s(self)::txt = \"woof\"\n;\n" +
            "fn go(a::A)::txt = a.s()\nprint go(D())\nprint go(A())"));

    [Fact]
    public void PlainSelfCannotChangeFields() =>
        Assert.Contains(DiagnosticCategory.ImmutableMutation, RunHelper.CompileErrors(
            "class C:\n n::i32 = 0\n fn bump(self):\n  self.n += 1\n ;\n;"));

    [Fact]
    public void PokeMethodNeedsAVarReceiver() =>
        Assert.Contains(DiagnosticCategory.ImmutableMutation, RunHelper.CompileErrors(
            "class C:\n n::i32 = 0\n fn bump(~self):\n  self.n += 1\n ;\n;\nc := C()\nc.bump()"));

    [Fact]
    public void FieldsHaveValueSemanticsForCollectionsButObjectsAreHandles() =>
        Assert.Equal("1\n", RunHelper.Ok(
            "class C:\n xs::List<i32> = []\n fn add(~self, v::i32):\n  self.xs.push(v)\n ;\n;\nvar c = C()\nc.add(1)\nprint len(c.xs)"));

    [Fact]
    public void InheritanceCycleIsAnError() =>
        Assert.Contains(DiagnosticCategory.InheritanceCycle, RunHelper.CompileErrors("class A from B:\n;\nclass B from A:\n;"));

    [Fact]
    public void UnknownParentIsAnError() =>
        Assert.Contains(DiagnosticCategory.UndefinedName, RunHelper.CompileErrors("class A from Nope:\n;"));

    [Fact]
    public void DuplicateClassIsAnError() =>
        Assert.Contains(DiagnosticCategory.Redeclared, RunHelper.CompileErrors("class A:\n;\nclass A:\n;"));

    [Fact]
    public void GenericClassNeedsInferableArguments() =>
        Assert.Contains(DiagnosticCategory.CannotInfer, RunHelper.CompileErrors(
            "class B<T>:\n v::List<T> = []\n;\nb := B()"));

    [Fact]
    public void GenericClassInferredFromAnnotation() =>
        Assert.Equal("0\n", RunHelper.Ok("class B<T>:\n v::List<T> = []\n;\nb::B<i32> = B()\nprint len(b.v)"));

    [Fact]
    public void OnlyTheListedOperatorsMayBeOverloaded() =>
        Assert.Contains(DiagnosticCategory.BadOverload, RunHelper.CompileErrors("class C:\n fn op **(self, o::C)::C = self\n;"));

    [Fact]
    public void NotEqualDefaultsToNegatedEqual() =>
        Assert.Equal("true\nfalse\n", RunHelper.Ok(
            "class C:\n n::i32\n fn init(self, n::i32):\n  self.n = n\n ;\n fn op ==(self, o::C)::bool = self.n == o.n\n;\nprint C(1) != C(2)\nprint C(1) != C(1)"));

    [Fact]
    public void ComparingObjectsWithoutAnOperatorIsAnError() =>
        Assert.Contains(DiagnosticCategory.BadOperator, RunHelper.CompileErrors("class C:\n;\nprint C() == C()"));

    [Fact]
    public void SelfOutsideAMethodIsAnError() =>
        Assert.Contains(DiagnosticCategory.SelfOutside, RunHelper.CompileErrors("x := self"));

    [Fact]
    public void ComparisonOperatorsMustReturnBool() =>
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors(
            "class C:\n fn op <(self, o::C)::i32 = 1\n;"));

    [Fact]
    public void SecretMembersAreVisibleInsideTheirClassOnly() =>
        Assert.Equal("2\n", RunHelper.Ok(
            "class C:\n secret n::i32 = 2\n fn same(self, o::C)::i32 = o.n\n;\nprint C().same(C())"));

    [Fact]
    public void SubclassCannotSeeParentsSecret() =>
        Assert.Contains(DiagnosticCategory.SecretAccess, RunHelper.CompileErrors(
            "class A:\n secret n::i32 = 1\n;\nclass B from A:\n fn peek(self)::i32 = self.n\n;"));

    [Fact]
    public void ClassNamesAreNotValues() =>
        Assert.Contains(DiagnosticCategory.UnexpectedToken, RunHelper.CompileErrors("class A:\n;\nx := A"));
}

public class OwnershipTests
{
    private const string Res = "class R:\n id::txt\n fn init(self, id::txt):\n  self.id = id\n ;\n fn deinit(self):\n  print \"close {self.id}\"\n ;\n;\n";

    [Fact]
    public void DeinitRunsInReverseDeclarationOrderAtScopeExit() =>
        Assert.Equal("close b\nclose a\n", RunHelper.Ok(Res + "fn f():\n a := R(\"a\")\n b := R(\"b\")\n;\nf()"));

    [Fact]
    public void DropRunsDeinitAtOnceAndNotAgain() =>
        Assert.Equal("close a\nafter\n", RunHelper.Ok(Res + "a := R(\"a\")\ndrop a\nprint \"after\""));

    [Fact]
    public void MovedObjectGetsOneFuneralNotTwo() =>
        Assert.Equal("close a\n", RunHelper.Ok(Res + "a := R(\"a\")\nb := a"));

    [Fact]
    public void UsingADroppedVariableIsAGhostHunt()
    {
        var r = RunHelper.RuntimeError(Res + "a := R(\"a\")\ndrop a\nprint a.id");
        Assert.Equal(DiagnosticCategory.UseAfterMove, r.Categories.Single());
    }

    [Fact]
    public void UsingAMovedVariableIsAGhostHunt()
    {
        var r = RunHelper.RuntimeError(Res + "a := R(\"a\")\nb := a\nprint a.id");
        Assert.Equal(DiagnosticCategory.UseAfterMove, r.Categories.Single());
    }

    [Fact]
    public void ReturnedObjectSurvivesItsFunction() =>
        Assert.Equal("made\nuse x\nclose x\n", RunHelper.Ok(
            Res + "fn make()::R:\n r := R(\"x\")\n print \"made\"\n ret r\n;\nx := make()\nprint \"use {x.id}\""));

    [Fact]
    public void UnusedTemporaryDiesAtTheEndOfItsStatement() =>
        Assert.Equal("close t\nnext\n", RunHelper.Ok(Res + "R(\"t\")\nprint \"next\""));

    [Fact]
    public void FieldObjectsAreDestroyedWithTheirParent() =>
        Assert.Equal("close f\n", RunHelper.Ok(
            Res + "class H:\n f::R = R(\"f\")\n;\nfn go():\n h := H()\n;\ngo()").Replace("close f\n", "close f\n"));

    [Fact]
    public void ReplacingAFieldDestroysTheOldObject() =>
        Assert.Equal("close one\nclose two\n", RunHelper.Ok(
            Res + "class H:\n f::R = R(\"one\")\n fn swap(~self):\n  self.f = R(\"two\")\n ;\n;\nfn go():\n var h = H()\n h.swap()\n;\ngo()"));

    [Fact]
    public void DeinitRunsWhileAnErrorUnwinds() =>
        Assert.Equal("close g\ncaught\n", RunHelper.Ok(
            Res + "fn f():\n g := R(\"g\")\n x := 1 / 0\n;\ntry:\n f()\ncatch e:\n print \"caught\"\n;"));

    [Fact]
    public void RipDoesNotRunDeinit()
    {
        var r = RunHelper.RuntimeError(Res + "a := R(\"a\")\nrip \"boom\"");
        Assert.Equal("", r.Out);
    }
}

public class TraitAndExtendTests
{
    [Fact]
    public void ExtendAddsMethodsToBuiltins() =>
        Assert.Equal("HI!!!\n", RunHelper.Ok("extend txt:\n fn shout(self)::txt = self.upper() + \"!!!\"\n;\nprint \"hi\".shout()"));

    [Fact]
    public void OwnMethodsBeatExtendWhichBeatsTraitDefaults() =>
        Assert.Equal("own\next\n", RunHelper.Ok(
            "trait T:\n fn a(self)::txt = \"trait\"\n fn b(self)::txt = \"trait\"\n;\n" +
            "class C with T:\n fn a(self)::txt = \"own\"\n;\nextend C:\n fn a(self)::txt = \"ext\"\n fn b(self)::txt = \"ext\"\n;\n" +
            "c := C()\nprint c.a()\nprint c.b()"));

    [Fact]
    public void TraitDefaultIsUsedWhenNothingElseDefinesTheMethod() =>
        Assert.Equal("trait\n", RunHelper.Ok("trait T:\n fn a(self)::txt = \"trait\"\n;\nclass C with T:\n;\nprint C().a()"));

    [Fact]
    public void MissingRequiredMethodNamesIt() =>
        Assert.Contains(DiagnosticCategory.MissingTraitMethods, RunHelper.CompileErrors("trait T:\n fn a(self)::txt\n;\nclass C with T:\n;"));

    [Fact]
    public void WrongSignatureCountsAsMissing() =>
        Assert.Contains(DiagnosticCategory.MissingTraitMethods, RunHelper.CompileErrors(
            "trait T:\n fn a(self)::txt\n;\nclass C with T:\n fn a(self)::i32 = 1\n;"));

    [Fact]
    public void DiamondOfDefaultsNeedsAnOverride() =>
        Assert.Contains(DiagnosticCategory.AmbiguousMember, RunHelper.CompileErrors(
            "trait A:\n fn m(self)::txt = \"a\"\n;\ntrait B:\n fn m(self)::txt = \"b\"\n;\nclass C with A, B:\n;"));

    [Fact]
    public void OverrideResolvesTheDiamond() =>
        Assert.Equal("c\n", RunHelper.Ok(
            "trait A:\n fn m(self)::txt = \"a\"\n;\ntrait B:\n fn m(self)::txt = \"b\"\n;\nclass C with A, B:\n fn m(self)::txt = \"c\"\n;\nprint C().m()"));

    [Fact]
    public void BoundedGenericAcceptsImplementersAndRejectsOthers()
    {
        var prelude = "trait T:\n fn a(self)::txt\n;\nclass Y with T:\n fn a(self)::txt = \"y\"\n;\nclass N:\n;\nfn f<X: T>(x::X)::txt = x.a()\n";
        Assert.Equal("y\n", RunHelper.Ok(prelude + "print f(Y())"));
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors(prelude + "print f(N())"));
    }

    [Fact]
    public void TraitsWorkAsParameterTypes() =>
        Assert.Equal("y\n", RunHelper.Ok(
            "trait T:\n fn a(self)::txt\n;\nclass Y with T:\n fn a(self)::txt = \"y\"\n;\nfn f(x::T)::txt = x.a()\nprint f(Y())"));

    [Fact]
    public void ExtendWithImplementsATraitForABuiltin() =>
        Assert.Equal("LOUD\n", RunHelper.Ok("trait S:\n fn l(self)::txt = \"LOUD\"\n;\nextend i32 with S:\n;\nprint 5.l()"));

    [Fact]
    public void ExtendWithMustSupplyRequiredMethods() =>
        Assert.Contains(DiagnosticCategory.MissingTraitMethods, RunHelper.CompileErrors(
            "trait S:\n fn l(self)::txt\n;\nextend i32 with S:\n;"));

    [Fact]
    public void TwoExtendsDefiningTheSameMethodAreAmbiguous() =>
        Assert.Contains(DiagnosticCategory.AmbiguousMember, RunHelper.CompileErrors(
            "extend txt:\n fn d(self)::txt = self\n;\nextend txt:\n fn d(self)::txt = self\n;"));

    [Fact]
    public void ExtendOnATensorPatternParsesButIsNotBoundYet() =>
        Assert.Contains(DiagnosticCategory.NotYetBuilt, RunHelper.CompileErrors("extend f32[$R, $C]:\n fn f(self)::i32 = 1\n;"));

    [Fact]
    public void ClassesAndExtendsPersistAcrossReplEntries()
    {
        var o = new StringWriter { NewLine = "\n" };
        var session = new SessionState(new StringReader(""), o, new StringWriter());
        Assert.Equal(RunOutcome.Ok, session.Eval("class P:\n n::i32 = 4\n;").Outcome);
        Assert.Equal(RunOutcome.Ok, session.Eval("extend P:\n fn twice(self)::i32 = self.n * 2\n;").Outcome);
        var r = session.Eval("P().twice()");
        Assert.Equal("=> 8 :: i32", r.Echo);
    }

    [Fact]
    public void ExtendOnAClassWorks() =>
        Assert.Equal("6\n", RunHelper.Ok("class P:\n n::i32 = 3\n;\nextend P:\n fn twice(self)::i32 = self.n * 2\n;\nprint P().twice()"));
}

public class IterableTraitTests
{
    private const string Count = "class Up with Iterable<i32>:\n n::i32 = 0\n lim::i32\n fn init(self, lim::i32):\n  self.lim = lim\n ;\n" +
        " fn next(~self)::i32?:\n  if self.n >= self.lim:\n   ret Nada\n  ;\n  self.n += 1\n  ret Got(self.n)\n ;\n;\n";

    [Fact]
    public void LoopsOverAUserIterable() =>
        Assert.Equal("1\n2\n3\n", RunHelper.Ok(Count + "loop x in Up(3):\n print x\n;"));

    [Fact]
    public void WorksInChainsEnumerateZipAndIn() =>
        Assert.Equal("[1, 2, 9]\n[(0, 1), (1, 2)]\n[(1, a), (2, b)]\ntrue\n", RunHelper.Ok(
            Count + "var out::List<i32> = []\nloop x in Up(2) then [9]:\n out.push(x)\n;\nprint out\nprint enumerate(Up(2))\nprint zip(Up(5), \"ab\")\nprint 2 in Up(3)"));

    [Fact]
    public void IterableWithTheWrongNextSignatureIsRejected() =>
        Assert.Contains(DiagnosticCategory.MissingTraitMethods, RunHelper.CompileErrors(
            "class C with Iterable<i32>:\n fn next(~self)::i32 = 1\n;"));

    [Fact]
    public void ElementTypeComesFromTheTraitArgument() =>
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors(Count + "loop x in Up(2):\n s::txt = x\n;"));

    [Fact]
    public void BuiltInIterablesSatisfyTheBound() =>
        Assert.Equal("6\n", RunHelper.Ok("fn s<I: Iterable<i32>>(it::I)::i32:\n var t = 0\n loop v in it:\n  t += v\n ;\n ret t\n;\nprint s([1, 2, 3])"));
}

public class TryCatchTests
{
    [Fact]
    public void CaughtErrorCarriesKindCodeAndLine()
    {
        var o = RunHelper.Ok("try:\n x := 1 / 0\ncatch e:\n print e.kind, e.code, e.line\n;");
        Assert.Equal("DivideByZero SE00300 2\n", o);
    }

    [Fact]
    public void ErrorIsUsableInTemplates() =>
        Assert.Contains("Divided by zero", RunHelper.Ok("try:\n x := 1 / 0\ncatch e:\n print \"{e}\"\n;"));

    [Fact]
    public void RipIsNotCatchable()
    {
        var r = RunHelper.RuntimeError("try:\n rip \"boom\"\ncatch e:\n print \"caught\"\n;");
        Assert.Equal("", r.Out);
        Assert.Equal(DiagnosticCategory.Panic, r.Categories.Single());
    }

    [Fact]
    public void TryNeedsACatch() =>
        Assert.NotEmpty(RunHelper.Run("try:\n x := 1\n;").Categories);

    [Fact]
    public void ReturnInsideTryReturnsFromTheFunction() =>
        Assert.Equal("1\n", RunHelper.Ok("fn f()::i32:\n try:\n  ret 1\n catch e:\n  ret 2\n ;\n;\nprint f()"));

    [Fact]
    public void FunctionEndingInTryThatReturnsOnBothPathsNeedsNoTrailingReturn() =>
        Assert.Equal("2\n", RunHelper.Ok("fn f()::i32:\n try:\n  ret 1 / 0\n catch e:\n  ret 2\n ;\n;\nprint f()"));

    [Fact]
    public void CatchVariableDoesNotLeak() =>
        Assert.Contains(DiagnosticCategory.UndefinedName, RunHelper.CompileErrors("try:\n x := 1\ncatch e:\n y := 2\n;\nprint e"));

    [Fact]
    public void CatchBindsACatchValueWithAMessage() =>
        Assert.Equal("true\n", RunHelper.Ok("try:\n x := 1 / 0\ncatch e:\n print len(e.message) > 5\n;"));

    [Fact]
    public void ErrorsInsideCalledFunctionsAreCaughtAtTheCaller() =>
        Assert.Equal("caught IndexOutOfRange\n", RunHelper.Ok(
            "fn f():\n xs := [1]\n print xs[3]\n;\ntry:\n f()\ncatch e:\n print \"caught {e.kind}\"\n;"));

    [Fact]
    public void StackOverflowIsCatchable() =>
        Assert.Equal("StackOverflow\n", RunHelper.Ok("fn f(n::i64)::i64 = f(n + 1)\ntry:\n print f(0)\ncatch e:\n print e.kind\n;"));
}

public class FileAndDataTests
{
    private static string Tmp(string name) => Path.Combine(Path.GetTempPath(), "splat_" + Guid.NewGuid().ToString("N") + name).Replace("\\", "/");

    [Fact]
    public void ReadWriteAppendRoundTrip()
    {
        var p = Tmp(".txt");
        try
        {
            Assert.Equal("ab\n", RunHelper.Ok($"write(\"{p}\", \"a\")\nappend(\"{p}\", \"b\")\nprint read(\"{p}\")"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void MissingFileIsDetectiveFailAndCatchable()
    {
        var r = RunHelper.RuntimeError("print read(\"nope_nope.txt\")");
        Assert.Equal(DiagnosticCategory.FileNotFound, r.Categories.Single());
        Assert.Contains("try:", r.Diag.Diagnostics[0].Message);
        Assert.Equal("FileNotFound\n", RunHelper.Ok("try:\n print read(\"nope_nope.txt\")\ncatch e:\n print e.kind\n;"));
    }

    [Fact]
    public void FilesAutoCloseAtScopeExit()
    {
        var p = Tmp(".txt");
        try
        {
            // If the writer were left open, the second open for writing would hit a sharing violation.
            var code = $"fn w(s::txt):\n f := open(\"{p}\", \"w\")\n f.write(s)\n;\nw(\"one\")\nw(\"two\")\nprint read(\"{p}\")";
            Assert.Equal("two\n", RunHelper.Ok(code));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void DropClosesAFileAndLaterUseFails()
    {
        var p = Tmp(".txt");
        try
        {
            var r = RunHelper.RuntimeError($"f := open(\"{p}\", \"w\")\ndrop f\nf.write(\"x\")");
            Assert.Equal(DiagnosticCategory.UseAfterMove, r.Categories.Single());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void LinesAreReadLazilyFromALargeFile()
    {
        var p = Tmp(".txt");
        try
        {
            using (var w = new StreamWriter(p)) for (int i = 0; i < 300_000; i++) w.WriteLine("line " + i);
            var before = GC.GetTotalAllocatedBytes(true);
            var o = RunHelper.Ok($"var n = 0\nloop line in open(\"{p}\", \"r\").lines():\n n += 1\n if n == 3:\n  br\n ;\n;\nprint n");
            var allocated = GC.GetTotalAllocatedBytes(true) - before;
            Assert.Equal("3\n", o);
            Assert.True(allocated < 20_000_000, $"reading 3 lines allocated {allocated} bytes; the file must not be read whole");
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void LinesVisitEveryLineOfALargeFile()
    {
        var p = Tmp(".txt");
        try
        {
            using (var w = new StreamWriter(p)) for (int i = 0; i < 50_000; i++) w.WriteLine("x");
            Assert.Equal("50000\n", RunHelper.Ok($"var n = 0\nloop line in open(\"{p}\", \"r\").lines():\n n += 1\n;\nprint n"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void ReadingAWriteOnlyFileIsAnError()
    {
        var p = Tmp(".txt");
        try
        {
            var r = RunHelper.RuntimeError($"f := open(\"{p}\", \"w\")\nprint f.read_all()");
            Assert.Equal(DiagnosticCategory.IoError, r.Categories.Single());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void JsonRoundTripAndTypedExtraction()
    {
        var o = RunHelper.Ok(
            "j := load_json(\"\\{\\\"a\\\": [1, 2.5], \\\"b\\\": \\\"x\\\"}\")\nprint j[\"a\"][0].as_i64()\nprint j[\"a\"][1].as_i64()\nprint j[\"b\"].as_txt()\nprint to_json(j)");
        Assert.Equal("Got(1)\nNada\nGot(x)\n{\"a\":[1,2.5],\"b\":\"x\"}\n", o);
    }

    [Fact]
    public void ToJsonHandlesEveryShape()
    {
        var o = RunHelper.Ok("class P:\n x::i32 = 1\n secret s::i32 = 2\n;\nprint to_json(P())\nprint to_json((1, \"a\"))\nprint to_json(Nada)\nprint to_json(set(1, 2))\nprint to_json(d\"2024-03-01\")");
        Assert.Equal("{\"x\":1}\n[1,\"a\"]\nnull\n[1,2]\n\"2024-03-01\"\n", o);
    }

    [Fact]
    public void BadJsonIsACatchableError() =>
        Assert.Equal("BadJson\n", RunHelper.Ok("try:\n load_json(\"\\{x\")\ncatch e:\n print e.kind\n;"));

    [Fact]
    public void MissingJsonKeyIsAKeyError() =>
        Assert.Equal(DiagnosticCategory.KeyNotFound, RunHelper.RuntimeError("j := load_json(\"\\{\\}\")\nprint j[\"x\"]").Categories.Single());

    [Fact]
    public void CsvRoundTripOfF32Rows()
    {
        var p = Tmp(".csv");
        try
        {
            Assert.Equal("[[1.0, 2.5], [3.0, 4.0]]\n", RunHelper.Ok($"save_csv(\"{p}\", [[1.0, 2.5], [3.0, 4.0]])\nprint load_csv(\"{p}\")"));
            File.WriteAllText(p, "a,b\n1,2\n");
            Assert.Equal("[[1.0, 2.0]]\n", RunHelper.Ok($"print load_csv(\"{p}\")"));
        }
        finally { File.Delete(p); }
    }
}

public class DateTimeTests
{
    [Theory]
    [InlineData("d\"2024-03-01\" + 30d", "03/31/2024")]
    [InlineData("d\"2024-01-31\" + 1mo", "02/29/2024")]
    [InlineData("d\"2023-01-31\" + 1mo", "02/28/2023")]
    [InlineData("d\"2024-02-29\" + 1y", "02/28/2025")]
    [InlineData("d\"2024-03-31\" - 1mo", "02/29/2024")]
    [InlineData("d\"2024-12-31\" + 1d", "01/01/2025")]
    [InlineData("d\"2023-12-31\" + 1q", "03/31/2024")]
    [InlineData("d\"2024-03-01\" - d\"2024-02-01\"", "29d")]
    [InlineData("d\"2023-03-01\" - d\"2023-02-01\"", "28d")]
    [InlineData("d\"2000-03-01\" - d\"2000-02-28\"", "2d")]
    [InlineData("d\"1900-03-01\" - d\"1900-02-28\"", "1d")]
    [InlineData("t\"2024-03-01T10:15:00Z\" + 90m", "03/01/2024 11:45:00")]
    [InlineData("t\"2024-03-01T10:15:00+05:30\"", "03/01/2024 04:45:00")]
    [InlineData("t\"2024-03-01T10:00:00Z\" - t\"2024-03-01T08:30:00Z\"", "1h30m")]
    [InlineData("tm\"09:30:00\" + 45m", "10:15:00")]
    [InlineData("wrap_time(tm\"23:30:00\", 90m)", "01:00:00")]
    [InlineData("2 * 1h", "2h")]
    [InlineData("1h + 30m", "1h30m")]
    [InlineData("90m / 3", "30m")]
    [InlineData("1y + 2mo", "1y2mo")]
    public void ArithmeticTable(string expr, string expected) => Assert.Equal(expected, RunHelper.Eval(expr));

    [Theory]
    [InlineData("tm\"23:00:00\" + 2h")]
    [InlineData("tm\"00:30:00\" - 1h")]
    [InlineData("d\"9999-12-31\" + 1d")]
    [InlineData("d\"2024-03-01\" + 36h")]
    public void ArithmeticErrors(string expr)
    {
        var r = RunHelper.RuntimeError($"print({expr})");
        Assert.True(r.Categories.Single() is DiagnosticCategory.TimeOverflow);
    }

    [Theory]
    [InlineData("d\"2024-03-01\" + t\"2024-03-01T00:00:00Z\"")]
    [InlineData("1mo + tm\"01:00:00\"")]
    [InlineData("tm\"01:00:00\" + 1mo")]
    [InlineData("d\"2024-01-01\" + d\"2024-01-02\"")]
    [InlineData("5 + 1h")]
    [InlineData("1h + 1mo")]
    public void StaticTypeErrors(string expr) =>
        Assert.Contains(DiagnosticCategory.BadOperator, RunHelper.CompileErrors($"x := {expr}"));

    [Fact]
    public void AccessorsAndWeekday() =>
        Assert.Equal("2024 3 1 5 10 15 0\n", RunHelper.Ok(
            "d := d\"2024-03-01\"\ns := t\"2024-03-01T10:15:00Z\"\nprint d.year(), d.month(), d.day(), d.weekday(), s.hour(), s.minute(), s.second()"));

    [Fact]
    public void MonthlyIterationDoesNotDrift() =>
        Assert.Equal("01/31/2024\n02/29/2024\n03/31/2024\n04/30/2024\n", RunHelper.Ok(
            "loop d in d\"2024-01-31\"..=d\"2024-04-30\" by 1mo:\n print d\n;"));

    [Fact]
    public void QuarterHourTimeIteration() =>
        Assert.Equal("09:00:00\n09:15:00\n09:30:00\n09:45:00\n", RunHelper.Ok(
            "loop t in tm\"09:00:00\"..tm\"10:00:00\" by 15m:\n print t\n;"));

    [Fact]
    public void DailyIterationIsTheDefaultAndEmptyWhenReversed() =>
        Assert.Equal("3\n0\n", RunHelper.Ok(
            "var a = 0\nloop d in d\"2024-03-01\"..d\"2024-03-04\":\n a += 1\n;\nprint a\nvar b = 0\nloop d in d\"2024-03-04\"..d\"2024-03-01\":\n b += 1\n;\nprint b"));

    [Fact]
    public void MissingStepOnTimeOrStampIsACompileError()
    {
        Assert.Contains(DiagnosticCategory.MissingRangeStep, RunHelper.CompileErrors("loop t in tm\"09:00:00\"..tm\"10:00:00\":\n print t\n;"));
        Assert.Contains(DiagnosticCategory.MissingRangeStep, RunHelper.CompileErrors("loop t in t\"2024-01-01T00:00:00Z\"..t\"2024-01-02T00:00:00Z\":\n print t\n;"));
    }

    [Fact]
    public void MixedEndpointTypesAreAnIdentityCrisis() =>
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("loop d in d\"2024-01-01\"..t\"2024-01-02T00:00:00Z\" by 1d:\n print d\n;"));

    [Fact]
    public void StepKindsAreValidatedPerType()
    {
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("loop t in tm\"09:00:00\"..tm\"10:00:00\" by 1mo:\n print t\n;"));
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("loop d in d\"2024-01-01\"..d\"2024-01-05\" by 1h:\n print d\n;"));
    }

    [Fact]
    public void RevThenAndInWorkOnDates() =>
        Assert.Equal("03/03/2024\n03/02/2024\ntrue\nfalse\n", RunHelper.Ok(
            "loop d in rev(d\"2024-03-02\"..=d\"2024-03-03\"):\n print d\n;\nprint d\"2024-03-03\" in d\"2024-03-01\"..d\"2024-03-10\" by 2d\nprint d\"2024-03-02\" in d\"2024-03-01\"..d\"2024-03-10\" by 2d"));

    [Theory]
    [InlineData("d\"2024-02-30\"")]
    [InlineData("tm\"25:00:00\"")]
    [InlineData("t\"2024-13-01T00:00:00Z\"")]
    [InlineData("d\"2024-1-1\"")]
    public void InvalidLiteralsAreComedicDiagnostics(string literal) =>
        Assert.Contains(DiagnosticCategory.BadLiteral, RunHelper.Run($"x := {literal}").Categories);

    [Fact]
    public void ParsingFunctionsReturnMaybe() =>
        Assert.Equal("Got(03/05/2024)\nNada\nGot(14:30:00)\nNada\n", RunHelper.Ok(
            "print date(\"2024-03-05\")\nprint date(\"2024-13-05\")\nprint time(\"14:30\")\nprint stamp(\"nope\")"));

    [Fact]
    public void FormatSpecsAreAlwaysIsoOrPatterned() =>
        Assert.Equal("2024-03-01 01/03/24 Friday 10:15\n", RunHelper.Ok(
            "d := d\"2024-03-01\"\ns := t\"2024-03-01T10:15:00Z\"\nprint \"{d:iso} {d:%d/%m/%y} {d:%A} {s:%H:%M}\""));

    [Fact]
    public void BadFormatPatternIsDiagnosed() =>
        Assert.Contains(DiagnosticCategory.BadFormat, RunHelper.CompileErrors("print \"{d\"2024-03-01\":%Q}\""));

    [Fact]
    public void LocalePinsTheFieldOrder()
    {
        var d = Value.Temporal(VKind.Date, 19783); // 2024-03-01
        Assert.Equal("03/01/2024", Temporal.LocaleDate(d.Bits, Temporal.Locale.EnUs));
        Assert.True(Temporal.Locale.TryFromTag("en-GB", out var gb));
        Assert.Equal("01/03/2024", Temporal.LocaleDate(d.Bits, gb));
        Assert.True(Temporal.Locale.TryFromTag("ja-JP", out var jp));
        Assert.Equal("2024/03/01", Temporal.LocaleDate(d.Bits, jp));
        Assert.True(Temporal.Locale.TryFromTag("de-DE", out var de));
        Assert.Equal("01.03.2024", Temporal.LocaleDate(d.Bits, de));
    }

    [Fact]
    public void ParsingFollowsTheLocale()
    {
        Assert.True(Temporal.Locale.TryFromTag("en-GB", out var gb));
        Assert.True(Temporal.TryParseDateLocale("05/03/2024", gb, out var days));
        Assert.Equal("2024-03-05", Temporal.IsoDate(days));
        Assert.True(Temporal.TryParseDateLocale("05/03/2024", Temporal.Locale.EnUs, out days));
        Assert.Equal("2024-05-03", Temporal.IsoDate(days));
    }

    [Fact]
    public void DatesAreHashableMapKeysButDursAreNot()
    {
        Assert.Equal("1\n", RunHelper.Ok("m := map(d\"2024-01-01\" => 1)\nprint m[d\"2024-01-01\"]"));
        Assert.Contains(DiagnosticCategory.NotHashable, RunHelper.CompileErrors("s := set(1h)"));
    }

    [Fact]
    public void DurAndCalCompareAndPrint()
    {
        Assert.Equal("true", RunHelper.Eval("90m > 1h"));
        Assert.Equal("500ms", RunHelper.Eval("500ms"));
        Assert.Equal("7d", RunHelper.Eval("1w"));
        Assert.Equal("0s", RunHelper.Eval("1s - 1s"));
    }
}

public class NetworkTests
{
    [Fact]
    public void LocalServerAndClientRoundTrip()
    {
        var o = RunHelper.Ok(
            "fn h(req::Req)::Res = Res.ok(\"{req.method} {req.path} {req.body}\")\n" +
            "s := serve(0, h)\nb := \"http://127.0.0.1:{s.port()}\"\nprint fetch(\"{b}/a\")\nprint post(\"{b}/b\", \"data\")\ns.stop()");
        Assert.Equal("GET /a \nPOST /b data\n", o);
    }

    [Fact]
    public void ServerStatusCodesBecomeCatchableErrors() =>
        Assert.Equal("NetworkFail\n", RunHelper.Ok(
            "fn h(req::Req)::Res = Res.not_found(\"no\")\ns := serve(0, h)\ntry:\n fetch(\"http://127.0.0.1:{s.port()}/x\")\ncatch e:\n print e.kind\n;\ns.stop()"));

    [Fact]
    public void QueryStringAndHeadersAreVisibleToTheHandler() =>
        Assert.Equal("v1\n", RunHelper.Ok(
            "fn h(req::Req)::Res = Res.ok(req.query[\"k\"])\ns := serve(0, h)\nprint fetch(\"http://127.0.0.1:{s.port()}/p?k=v1\")\ns.stop()"));

    [Fact]
    public void ConnectionRefusedIsANetworkError() =>
        Assert.Equal("NetworkFail\n", RunHelper.Ok("try:\n fetch(\"http://127.0.0.1:1/\")\ncatch e:\n print e.kind\n;"));

    [Fact]
    public void ServerOutlivesNothingWhenItsVariableLeavesScope() =>
        Assert.Equal("NetworkFail\n", RunHelper.Ok(
            "fn start()::i32:\n s := serve(0, |r::Req| Res.ok(\"x\"))\n ret s.port()\n;\np := start()\ntry:\n fetch(\"http://127.0.0.1:{p}/\")\ncatch e:\n print e.kind\n;"));

    [Fact]
    public void TcpEchoOverLoopback() =>
        Assert.Equal("ping\necho:ping\n", RunHelper.Ok(
            "l := Socket.listen(0)\nc := Socket.tcp(\"127.0.0.1\", l.port())\ns := l.accept()\nc.send(\"ping\")\nm := s.recv()\nprint m\ns.send(\"echo:{m}\")\nprint c.recv()"));

    [Fact]
    public void UdpDatagram() =>
        Assert.Equal("hi\n", RunHelper.Ok(
            "a := Socket.udp(0)\nb := Socket.udp(0)\nb.send_to(\"127.0.0.1\", a.port(), \"hi\")\nprint a.recv()"));

    [Fact]
    public void ClosedSocketCannotBeUsed()
    {
        var r = RunHelper.RuntimeError("l := Socket.listen(0)\nl.close()\nl.accept()");
        Assert.Equal(DiagnosticCategory.UseAfterMove, r.Categories.Single());
    }

    [Fact]
    public void StaticCallsOnUnknownMembersAreDiagnosed() =>
        Assert.Contains(DiagnosticCategory.UndefinedName, RunHelper.CompileErrors("s := Socket.nope(1)"));

    [Fact]
    public void ResHelpersBuildResponses() =>
        Assert.Equal("404 nothing\n200\n", RunHelper.Ok("r := Res.not_found(\"nothing\")\nprint r.status, r.body\nprint Res.json(\"1\").status"));
}

public class SyntaxPhase8To10Tests
{
    [Theory]
    [InlineData("class A:\n x::i32 = 1\n secret y::txt\n fn f(self)::i32 = 1\n secret fn g(~self):\n  self.x = 2\n ;\n;")]
    [InlineData("class A<T> from B with C, D<i32>:\n;")]
    [InlineData("trait T<X>:\n fn a(self)::X\n fn b(self)::i32 = 1\n;")]
    [InlineData("extend txt with S:\n fn a(self)::i32 = 1\n;")]
    [InlineData("try:\n x := 1\ncatch e:\n print e\n;")]
    [InlineData("drop x")]
    [InlineData("fn f<T: Iterable<i32>>(x::T)::i32? = Nada")]
    public void NewConstructsRoundTripThroughTheFormatter(string src)
    {
        var d1 = new DiagnosticEngine();
        var p1 = Splat.Syntax.Parser.ParseProgram(new SourceText("a", src), d1);
        Assert.False(d1.HasErrors, string.Join("\n", d1.Diagnostics.Select(d => d.Message)));
        var text = Splat.Syntax.SourceFormatter.Format(p1);
        var d2 = new DiagnosticEngine();
        var p2 = Splat.Syntax.Parser.ParseProgram(new SourceText("b", text), d2);
        Assert.False(d2.HasErrors, text);
        Assert.Equal(Splat.Syntax.AstPrinter.ProgramText(p1), Splat.Syntax.AstPrinter.ProgramText(p2));
    }

    [Theory]
    [InlineData("500ms", "500ms")]
    [InlineData("15m", "15m")]
    [InlineData("2w", "2w")]
    [InlineData("1mo", "1mo")]
    [InlineData("1y", "1y")]
    [InlineData("d\"2024-03-01\"", "d\"2024-03-01\"")]
    [InlineData("tm\"09:30:00\"", "tm\"09:30:00\"")]
    public void TemporalLiteralsParse(string src, string expected) => Assert.Equal(expected, ParseHelper.Expr(src));

    [Fact]
    public void DurationSuffixesStayIdentifiersWhenNotGluedToANumber() =>
        Assert.Equal("(+ m h)", ParseHelper.Expr("m + h"));

    [Fact]
    public void NumberRunningIntoAnUnknownSuffixIsStillACrimeScene() =>
        Assert.Contains(DiagnosticCategory.BadLiteral, RunHelper.Run("x := 5xyz").Categories);

    [Fact]
    public void MaybeSugarInTypes() =>
        Assert.Equal("Got(1)\n", RunHelper.Ok("fn f()::i32? = Got(1)\nprint f()"));

    [Fact]
    public void MalformedClassBodiesRecoverAndReport()
    {
        var r = RunHelper.Run("class A:\n 5\n x::i32 = 1\n fn f(self) = 1\n;\ny := 1");
        Assert.NotEmpty(r.Categories);
    }
}

public class Milestone2AcceptanceTests
{
    [Fact]
    public void AcceptanceScriptRunsAndMatchesItsExpectedOutput()
    {
        var r = RunHelper.Run(Golden.Read(Golden.M1Dir, "m2_acceptance.splat"), "m2_acceptance.splat");
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Assert.Empty(r.Diag.Diagnostics);
        Golden.Compare(Golden.M1Dir, "m2_acceptance.out", r.Out);
    }
}
