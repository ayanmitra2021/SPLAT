using System.Globalization;
using Splat.Core;
using Splat.Data;
using Splat.Runtime;
using Splat.Semantics;

namespace Splat.Tests;

// ---------------------------------------------------------------------------------------------------------------------
// Phase 12-14 (Milestone 3): tensors, the shape unifier and the math syntax.
// ---------------------------------------------------------------------------------------------------------------------

public class AlignedBufferTests
{
    [Fact]
    public void EveryAllocationIsAlignedTo64Bytes()
    {
        for (int i = 0; i < 40; i++)
        {
            using var b = AlignedBuffer.Allocate(1 + i * 37);
            Assert.Equal(0L, (long)b.Address % 64);
            Assert.Equal(1 + i * 37, b.Length);
        }
    }

    [Fact]
    public void AllocationsAreZeroFilledAndSpansAreBoundsChecked()
    {
        using var b = AlignedBuffer.Allocate(64);
        Assert.All(b.AsSpan<byte>().ToArray(), x => Assert.Equal(0, x));
        Assert.Equal(16, b.AsSpan<float>().Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.AsSpan<float>(10, 10));
    }

    [Fact]
    public void ReferenceCountingFreesWhenTheLastReferenceGoes()
    {
        var b = AlignedBuffer.Allocate(1024);
        b.Retain();
        Assert.Equal(2, b.RefCount);
        b.Release();
        Assert.False(b.IsFreed);
        b.Release();
        Assert.True(b.IsFreed);
        Assert.Throws<ObjectDisposedException>(() => b.AsSpan<byte>());
        Assert.Throws<ObjectDisposedException>(() => b.Retain());
    }

    [Fact]
    public void BuffersHaveUniqueIds()
    {
        using var a = AlignedBuffer.Allocate(8);
        using var b = AlignedBuffer.Allocate(8);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void AFileCanBeMemoryMapped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"splat_map_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, BitConverter.GetBytes(1.5f).Concat(BitConverter.GetBytes(2.5f)).ToArray());
            var b = AlignedBuffer.MapFile(path);
            Assert.True(b.IsMapped);
            Assert.Equal(0L, (long)b.Address % 64);
            Assert.Equal([1.5f, 2.5f], b.AsSpan<float>().ToArray());
            b.Release();
            Assert.True(b.IsFreed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ATensorStoresItsElementsInAnAlignedBuffer()
    {
        var t = Tensor.Zeros(TensorElem.F32, [4, 5]);
        Assert.Equal(64, t.BaseAlignment);
        Assert.Equal(0L, (long)t.Buffer.Address % 64);
        Assert.Equal(80, t.Buffer.Length);
    }
}

public class StatsTests
{
    [Fact]
    public void MeanVarianceAndStdDev()
    {
        double[] x = [1, 2, 3, 4];
        Assert.Equal(2.5, Stats.Mean<double>(x));
        Assert.Equal(1.25, Stats.Variance<double>(x), 12);
        Assert.Equal(5.0 / 3.0, Stats.Variance<double>(x, ddof: 1), 12);
        Assert.Equal(Math.Sqrt(1.25), Stats.StdDev<double>(x), 12);
        Assert.True(double.IsNaN(Stats.Mean<double>([])));
        Assert.True(double.IsNaN(Stats.Variance<double>([1.0], ddof: 1)));
    }

    [Fact]
    public void VarianceIsStableForAHugeMean()
    {
        // A naive sum-of-squares formula loses every digit here; Welford does not.
        var x = new double[] { 1e9 + 1, 1e9 + 2, 1e9 + 3, 1e9 + 4 };
        Assert.Equal(1.25, Stats.Variance<double>(x), 6);
    }

    [Fact]
    public void QuantileInterpolatesLinearly()
    {
        double[] x = [4, 1, 3, 2];
        Assert.Equal(1.0, Stats.Quantile<double>(x, 0));
        Assert.Equal(2.5, Stats.Quantile<double>(x, 0.5));
        Assert.Equal(4.0, Stats.Quantile<double>(x, 1));
        Assert.Equal(1.75, Stats.Quantile<double>(x, 0.25));
        Assert.True(double.IsNaN(Stats.Quantile<double>([1, double.NaN], 0.5)));
        Assert.True(double.IsNaN(Stats.Quantile<double>(x, 1.5)));
    }

    [Fact]
    public void ArgMaxArgMinAndNaN()
    {
        Assert.Equal(1, Stats.ArgMax<float>([1f, 9f, 3f, 9f]));
        Assert.Equal(0, Stats.ArgMin<float>([1f, 9f, 1f]));
        Assert.Equal(2, Stats.ArgMax<double>([1, 2, double.NaN, 9]));   // the first NaN wins, like NumPy
        Assert.Equal(-1, Stats.ArgMax<double>([]));
        Assert.Equal(9.0, Stats.Max<Half>([(Half)1, (Half)9, (Half)3]));
    }

    [Fact]
    public void PairwiseSumIsDeterministicAndAccurate()
    {
        // 0.1f one million times: the exact total of the stored values is 100000.0015; a naive float loop is off by almost 1000.
        var ones = new float[1_000_000];
        Array.Fill(ones, 0.1f);
        Assert.InRange(Stats.Sum<float>(ones), 100000.0, 100000.01);
        float naive = 0;
        foreach (var v in ones) naive += v;
        Assert.True(Math.Abs(naive - 100000.0) > 100, "the naive float loop is the thing being avoided");

        // Feeding the terms one at a time gives the same answer as summing the span.
        var acc = new PairwiseSum();
        foreach (var v in ones) acc.Add(v);
        Assert.Equal(Stats.Sum<float>(ones), acc.Total);
    }
}

public class ShapeUnifierTests
{
    private static SDim C(long n) => SDim.Const(n);
    private static SDim V(string n) => SDim.Var(n);

    [Fact]
    public void ConstantsMustBeEqual()
    {
        Assert.Equal(Shapes.Rel.Same, Shapes.Unify(C(3), C(3)));
        Assert.Equal(Shapes.Rel.Conflict, Shapes.Unify(C(3), C(4)));
    }

    [Fact]
    public void DynamicUnifiesWithAnythingButNeedsARuntimeCheck()
    {
        Assert.Equal(Shapes.Rel.Runtime, Shapes.Unify(SDim.Dyn, C(3)));
        Assert.Equal(Shapes.Rel.Runtime, Shapes.Unify(V("N"), SDim.Dyn));
        Assert.Equal(Shapes.Rel.Runtime, Shapes.Unify(SDim.Dyn, SDim.Dyn));
    }

    [Fact]
    public void RigidVariablesAreEqualOnlyToThemselves()
    {
        Assert.Equal(Shapes.Rel.Same, Shapes.Unify(V("N"), V("N")));
        Assert.Equal(Shapes.Rel.Conflict, Shapes.Unify(V("N"), V("M")));
        Assert.Equal(Shapes.Rel.Conflict, Shapes.Unify(V("N"), C(3)));
    }

    [Fact]
    public void InferenceDimensionsBindAndStayConsistent()
    {
        var n = new InferSDim("N");
        Assert.Equal(Shapes.Rel.Same, Shapes.Unify(n, C(5)));
        Assert.Equal("5", n.Text);
        Assert.Equal(Shapes.Rel.Same, Shapes.Unify(n, C(5)));
        Assert.Equal(Shapes.Rel.Conflict, Shapes.Unify(n, C(6)));

        var m = new InferSDim("M");
        Assert.Equal(Shapes.Rel.Same, Shapes.Unify(m, V("K")));  // binds to another variable
        Assert.Equal(Shapes.Rel.Same, Shapes.Unify(m, V("K")));
        Assert.Equal(Shapes.Rel.Conflict, Shapes.Unify(m, C(2)));

        var d = new InferSDim("D");
        Assert.Equal(Shapes.Rel.Runtime, Shapes.Unify(d, SDim.Dyn));  // a variable may bind to ?
    }

    [Theory]
    [InlineData("3,4", "4", "3,4")]
    [InlineData("3,1", "1,4", "3,4")]
    [InlineData("5", "5", "5")]
    [InlineData("2,3,4", "3,4", "2,3,4")]
    [InlineData("2,1,4", "3,1", "2,3,4")]
    [InlineData("1", "7", "7")]
    public void BroadcastingAlignsOnTheRight(string a, string b, string expected)
    {
        Assert.True(Shapes.TryBroadcast(Dims(a), Dims(b), out var r, out _, out _, out _));
        Assert.Equal(expected, string.Join(",", r.Select(d => d.Text)));
    }

    [Theory]
    [InlineData("3", "4", 1)]
    [InlineData("2,3", "3,2", 1)]
    [InlineData("2,3,4", "2,4", 2)]
    public void BroadcastingConflictsReportTheAxisFromTheRight(string a, string b, int axis)
    {
        Assert.False(Shapes.TryBroadcast(Dims(a), Dims(b), out _, out var x, out var y, out int fromRight));
        Assert.NotNull(x); Assert.NotNull(y);
        Assert.Equal(axis, fromRight);
    }

    [Fact]
    public void ADynamicDimensionBroadcastsWithAKnownOne()
    {
        Assert.True(Shapes.TryBroadcast([SDim.Dyn, C(4)], [C(4)], out var r, out _, out _, out _));
        Assert.Equal("?,4", string.Join(",", r.Select(d => d.Text)));
        Assert.True(Shapes.TryBroadcast([SDim.Dyn], [C(5)], out var r2, out _, out _, out _));
        Assert.Equal("5", string.Join(",", r2.Select(d => d.Text)));
    }

    private static TensorSType T(params SDim[] d) => new(Types.F32, d);

    [Fact]
    public void MatrixProductShapeRules()
    {
        Assert.Equal("f32[3, 2]", Shapes.MatMul(T(C(3), C(4)), T(C(4), C(2))).Type!.Name);
        Assert.Equal("f32[3]", Shapes.MatMul(T(C(3), C(4)), T(C(4))).Type!.Name);        // matrix-vector
        Assert.Equal("f32[2]", Shapes.MatMul(T(C(4)), T(C(4), C(2))).Type!.Name);        // vector-matrix
        Assert.True(Shapes.MatMul(T(C(4)), T(C(4))).ScalarResult);                        // dot
        Assert.Equal("f32[2, 3, 5]", Shapes.MatMul(T(C(2), C(3), C(4)), T(C(4), C(5))).Type!.Name);        // batch x matrix
        Assert.Equal("f32[2, 3, 5]", Shapes.MatMul(T(C(2), C(3), C(4)), T(C(2), C(4), C(5))).Type!.Name);  // batch x batch
        Assert.Equal("f32[7, 3, 5]", Shapes.MatMul(T(C(7), C(3), C(4)), T(C(1), C(4), C(5))).Type!.Name); // 1 broadcasts
        Assert.Equal("f32[$B, $M, $N]", Shapes.MatMul(T(V("B"), V("M"), V("K")), T(V("B"), V("K"), V("N"))).Type!.Name);
    }

    [Fact]
    public void MatrixProductFailuresNameTheInnerDimensions()
    {
        var r = Shapes.MatMul(T(C(3), C(4)), T(C(5), C(2)));
        Assert.Null(r.Type);
        Assert.Equal("4", r.K1!.Text);
        Assert.Equal("5", r.K2!.Text);
        Assert.NotNull(Shapes.MatMul(T(C(2), C(3), C(4)), T(C(5), C(4), C(2))).Problem); // batch 2 vs 5
    }

    [Fact]
    public void TransposeSwapsTheLastTwoAxes()
    {
        Assert.Equal("f32[4, 3]", Shapes.Transpose(T(C(3), C(4))).Name);
        Assert.Equal("f32[2, 4, 3]", Shapes.Transpose(T(C(2), C(3), C(4))).Name);
        Assert.Equal("f32[5]", Shapes.Transpose(T(C(5))).Name);
    }

    [Fact]
    public void TensorTypesPrintTheirDimensions()
    {
        Assert.Equal("f32[3, $K, ?]", T(C(3), V("K"), SDim.Dyn).Name);
        Assert.True(T(C(3), C(4)).IsStatic);
        Assert.Equal(12L, T(C(3), C(4)).StaticCount);
        Assert.Null(T(C(3), SDim.Dyn).StaticCount);
        Assert.Equal("f32[,]", T(C(3), C(4)).BaseKey);
    }

    private static List<SDim> Dims(string s) => s.Split(',').Select(x => (SDim)SDim.Const(long.Parse(x, CultureInfo.InvariantCulture))).ToList();
}

public class TensorKernelTests
{
    private static Tensor Rand(TensorElem e, Random r, params long[] shape)
    {
        long n = shape.Aggregate(1L, (a, b) => a * b);
        return Tensor.FromDoubles(e, shape, Enumerable.Range(0, (int)n).Select(_ => r.NextDouble() * 4 - 2).ToArray());
    }

    [Fact]
    public void SlicesAndTransposesShareTheBuffer()
    {
        var t = Tensor.FromDoubles(TensorElem.F64, [3, 4], Enumerable.Range(0, 12).Select(i => (double)i).ToArray());
        var row = t.Row(1);
        Assert.Equal(t.BufferId, row.BufferId);
        Assert.Equal([4.0, 5.0, 6.0, 7.0], row.ToDoubles());
        var tr = t.Transposed();
        Assert.Equal([4, 3], tr.Shape);
        Assert.Equal(TensorLayout.Strided, tr.Layout);
        Assert.Equal(TensorLayout.Contiguous, t.Layout);
        row.WriteAt(row.Offset, 99);
        Assert.Equal(99.0, t.At(1, 0));            // the write went through the view
        Assert.Equal(99.0, tr.At(0, 1));
    }

    [Fact]
    public void CloneNeverSharesTheBuffer()
    {
        var t = Tensor.FromDoubles(TensorElem.F32, [2, 2], [1, 2, 3, 4]);
        var c = t.Transposed().Clone();
        Assert.NotEqual(t.BufferId, c.BufferId);
        Assert.True(c.IsContiguous);
        Assert.Equal([1.0, 3.0, 2.0, 4.0], c.ToDoubles());
    }

    [Fact]
    public void MetadataIsExact()
    {
        var t = Tensor.Zeros(TensorElem.F16, [2, 3]);
        Assert.Equal(2, t.ElemSize);
        Assert.Equal(6, t.Count);
        Assert.Equal([3L, 1L], t.Strides);
        Assert.Equal("f16[2, 3]", t.ShapeText);
        Assert.Equal("f16[,]", t.BaseKey);
        Assert.Equal(TensorLayout.Contiguous, t.Layout);
    }

    [Theory]
    [InlineData(TensorElem.F32)]
    [InlineData(TensorElem.F64)]
    public void BlockedMatmulIsBitIdenticalToTheNaiveReference(TensorElem elem)
    {
        var rng = new Random(7);
        foreach (var (m, k, n) in new[] { (1, 1, 1), (3, 5, 7), (17, 33, 9), (64, 130, 65), (5, 300, 2) })
        {
            var a = Rand(elem, rng, m, k);
            var b = Rand(elem, rng, k, n);
            var blocked = TensorOps.MatMul(a, b, blocked: true);
            var naive = TensorOps.MatMul(a, b, blocked: false);
            Assert.True(blocked.ToDoubles().SequenceEqual(naive.ToDoubles()), $"{m}x{k} @ {k}x{n} differs");
        }
    }

    [Fact]
    public void MatmulMatchesAnIndependentDoubleComputation()
    {
        var rng = new Random(3);
        var a = Rand(TensorElem.F64, rng, 6, 7);
        var b = Rand(TensorElem.F64, rng, 7, 5);
        var c = ManagedTensorKernels.Instance.MatMul(a, b);
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 5; j++)
            {
                double s = 0;
                for (int k = 0; k < 7; k++) s += a.At(i, k) * b.At(k, j);
                Assert.Equal(s, c.At(i, j), 12);
            }
    }

    [Fact]
    public void HalfMatmulRoundsOnceAtTheEnd()
    {
        var a = Tensor.FromDoubles(TensorElem.F16, [1, 3], [0.5, 0.25, 2]);
        var b = Tensor.FromDoubles(TensorElem.F16, [3, 1], [2, 4, 0.5]);
        Assert.Equal(3.0, ManagedTensorKernels.Instance.MatMul(a, b).At(0, 0));
    }

    [Fact]
    public void ElementwiseKernelsAgreeAcrossFastAndGeneralPaths()
    {
        var k = ManagedTensorKernels.Instance;
        var a = Tensor.FromDoubles(TensorElem.F32, [2, 3], [1, 2, 3, 4, 5, 6]);
        var fast = k.Binary(BinOp.Add, a, a);
        var viaView = k.Binary(BinOp.Add, a.Transposed().Transposed(), a);   // a strided operand takes the general path
        Assert.Equal(fast.ToDoubles(), viaView.ToDoubles());
        var scalar = k.Binary(BinOp.Mul, a, Tensor.Scalar(TensorElem.F32, 2));
        Assert.Equal([2.0, 4.0, 6.0, 8.0, 10.0, 12.0], scalar.ToDoubles());
        var sub = k.Binary(BinOp.Sub, Tensor.Scalar(TensorElem.F32, 10), a);   // scalar on the left keeps its order
        Assert.Equal([9.0, 8.0, 7.0, 6.0, 5.0, 4.0], sub.ToDoubles());
    }

    [Fact]
    public void BroadcastErrorsCarryTheAxis()
    {
        var k = ManagedTensorKernels.Instance;
        var e = Assert.Throws<TensorError>(() => k.Binary(BinOp.Add, Tensor.Zeros(TensorElem.F32, [3, 4]), Tensor.Zeros(TensorElem.F32, [5]), ".+"));
        Assert.Contains("axis 1 has 4 against 5", e.Message);
    }

    [Fact]
    public void ReductionsRoundTrip()
    {
        var k = ManagedTensorKernels.Instance;
        var t = Tensor.FromDoubles(TensorElem.F64, [2, 2], [1, -2, 3, -4]);
        Assert.Equal(-2.0, k.Reduce(RedOp.Sum, t));
        Assert.Equal(-0.5, k.Reduce(RedOp.Mean, t));
        Assert.Equal(3.0, k.Reduce(RedOp.Max, t));
        Assert.Equal(-4.0, k.Reduce(RedOp.Min, t));
        Assert.Equal(2.0, k.Reduce(RedOp.ArgMax, t));
        Assert.Equal(10.0, k.Reduce(RedOp.NormL1, t));
        Assert.Equal(4.0, k.Reduce(RedOp.NormInf, t));
        Assert.Equal(Math.Sqrt(30), k.Reduce(RedOp.NormL2, t), 12);
        Assert.Equal(24.0, k.Reduce(RedOp.Prod, t));
        // a transposed view reduces to the same numbers
        Assert.Equal(-2.0, k.Reduce(RedOp.Sum, t.Transposed()));
        Assert.Throws<TensorMathError>(() => k.Reduce(RedOp.Max, Tensor.Zeros(TensorElem.F64, [0])));
    }

    [Fact]
    public void ErfMatchesKnownValues()
    {
        var m = new NetTranscendentals();
        Assert.Equal(0.8427007929497149, m.Erf(1.0), 12);
        Assert.Equal(-0.9953222650189527, m.Erf(-2.0), 12);
        Assert.Equal(0.0, m.Erf(0.0));
        Assert.Equal(1.0, m.Erf(7.0));
        Assert.Equal(0.5204998778130465, m.Erf(0.5), 12);
    }

    [Fact]
    public void SolveHandlesAllThreeShapesOfSystem()
    {
        // square (LU with pivoting)
        var a = Tensor.FromDoubles(TensorElem.F64, [3, 3], [0, 2, 1, 1, 1, 1, 2, 1, 0]);
        var b = Tensor.FromDoubles(TensorElem.F64, [3], [5, 6, 3]);
        var x = TensorOps.Solve(a, b);
        var back = ManagedTensorKernels.Instance.MatMul(a, x);
        for (int i = 0; i < 3; i++) Assert.Equal(b.At(i), back.At(i), 10);

        // tall (least squares): the residual is orthogonal to the columns
        var d = Tensor.FromDoubles(TensorElem.F64, [4, 2], [1, 0, 1, 1, 1, 2, 1, 3]);
        var y = Tensor.FromDoubles(TensorElem.F64, [4], [1, 3, 5, 8]);
        var fit = TensorOps.Solve(d, y);
        var resid = ManagedTensorKernels.Instance.Binary(BinOp.Sub, ManagedTensorKernels.Instance.MatMul(d, fit), y, "-");
        var ortho = ManagedTensorKernels.Instance.MatMul(d.Transposed(), resid);
        Assert.Equal(0.0, ortho.At(0), 10);
        Assert.Equal(0.0, ortho.At(1), 10);

        // wide (minimum norm): x + y = 2 gives (1, 1)
        var w = TensorOps.Solve(Tensor.FromDoubles(TensorElem.F64, [1, 2], [1, 1]), Tensor.FromDoubles(TensorElem.F64, [1], [2]));
        Assert.Equal(1.0, w.At(0), 12);
        Assert.Equal(1.0, w.At(1), 12);
    }

    [Fact]
    public void SingularAndRankDeficientSystemsAreRejected()
    {
        var singular = Tensor.FromDoubles(TensorElem.F64, [2, 2], [1, 2, 2, 4]);
        var b = Tensor.FromDoubles(TensorElem.F64, [2], [1, 2]);
        Assert.Throws<TensorMathError>(() => TensorOps.Solve(singular, b));
        var tall = Tensor.FromDoubles(TensorElem.F64, [3, 2], [1, 2, 2, 4, 3, 6]);
        Assert.Throws<TensorMathError>(() => TensorOps.Solve(tall, Tensor.FromDoubles(TensorElem.F64, [3], [1, 2, 3])));
    }
}

public class TensorProgramTests
{
    private static string Run(string code) => RunHelper.Ok(code);

    // ---- Phase 12 ----

    [Theory]
    [InlineData("3, 4", "4", "[3, 4]")]
    [InlineData("3, 1", "1, 4", "[3, 4]")]
    [InlineData("5", "5", "[5]")]
    [InlineData("2, 3, 4", "3, 4", "[2, 3, 4]")]
    [InlineData("2, 1, 4", "3, 1", "[2, 3, 4]")]
    [InlineData("1", "7", "[7]")]
    [InlineData("4, 1", "3", "[4, 3]")]
    public void ValidBroadcastShapesGiveTheNumPyResult(string a, string b, string expected) =>
        Assert.Equal(expected + "\n", Run($"A := zeros({a})\nB := zeros({b})\nC := A .+ B\nprint C.shape()"));

    [Theory]
    [InlineData("3, 4", "5")]
    [InlineData("2, 3", "3, 2")]
    [InlineData("3", "4")]
    [InlineData("2, 3, 4", "2, 4")]
    [InlineData("0, 3", "2, 3")]
    public void InvalidBroadcastShapesAreGeometryCrimesAtCheckTime(string a, string b)
    {
        var r = RunHelper.Run($"print \"never\"\nA := zeros({a})\nB := zeros({b})\nC := A .+ B");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal("", r.Out);
        Assert.Equal([DiagnosticCategory.ShapeMismatch], r.Categories);
        Assert.Contains("GEOMETRY CRIME", r.Rendered);
    }

    [Fact]
    public void DynamicDimensionsDeferTheBroadcastCheckToRuntime()
    {
        var r = RunHelper.RuntimeError("fn mk(n::usize)::f32[?] = zeros(n)\nprint \"start\"\nprint mk(3) .+ mk(4)");
        Assert.Equal("start\n", r.Out);
        Assert.Equal([DiagnosticCategory.ShapeMismatch], r.Categories);
        Assert.Contains("axis 1 has 3 against 4", r.Rendered);
        Assert.Equal("[3.0, 3.0, 3.0]\n", Run("fn mk(n::usize)::f32[?] = ones(n)\nprint mk(3) .+ mk(1) .+ mk(3)"));
    }

    [Fact]
    public void AnUnknownDimensionIsCheckedWhereAKnownSizeIsNeeded()
    {
        const string defs = "fn mk(n::usize)::f32[?] = ones(n)\nfn needs3(v::f32[3])::f32 = sum(v)\n";
        Assert.Equal("3.0\n", Run(defs + "print needs3(mk(3))"));
        var r = RunHelper.RuntimeError(defs + "print needs3(mk(4))");
        Assert.Equal([DiagnosticCategory.ShapeMismatch], r.Categories);
        Assert.Contains("axis 0 is 4, not 3", r.Rendered);
    }

    [Fact]
    public void StarOnTensorsIsAnErrorWithATip()
    {
        var r = RunHelper.Run("A := zeros(2, 2)\nB := A * A");
        Assert.Equal([DiagnosticCategory.BadOperator], r.Categories);
        Assert.Contains("did you mean '.*'", r.Rendered);
        Assert.Contains("'@'", r.Rendered);
    }

    [Fact]
    public void ElementTypesNeverMixImplicitly()
    {
        Assert.Equal([DiagnosticCategory.BadOperator], RunHelper.CompileErrors("A := zeros(2)\nB::f64[2] = zeros(2)\nC := A .+ B"));
        Assert.Equal([DiagnosticCategory.BadOperator], RunHelper.CompileErrors("A := zeros(2)\nC := A + 1"));   // an integer literal is not an f32
        Assert.Equal("[2.0, 2.0]\n", Run("A::f64[2] = ones(2)\nprint A + 1.0"));
        Assert.Equal("[1.0, 1.0]\n[1.0, 1.0]\n", Run("A := ones(2)\nB := f64(A)\nprint B\nprint f32(B)"));
    }

    [Fact]
    public void ScalarOperandsAdoptTheTensorElementType()
    {
        Assert.Equal("[2.5, 3.5]\n", Run("A::f64[2] = f64[2, 3]\nprint A + 0.5"));
        Assert.Equal("[1.5, 2.5]\n", Run("A::f16[2] = f16[1, 2]\nprint A .+ 0.5"));
    }

    [Fact]
    public void SlicesAreViewsOfAVarTensor()
    {
        Assert.Equal("[[0.0, 5.0],\n [0.0, 0.0]]\n", Run("var T::f32[2, 2] = zeros(2, 2)\nT[0, 1] = 5.0\nprint T"));
        Assert.Equal("[[1.0, 0.0],\n [1.0, 0.0]]\n", Run("var T::f32[2, 2] = zeros(2, 2)\nT[:, 0] = 1.0\nprint T"));
        Assert.Equal("[[1.0, 2.0],\n [3.0, 4.0]]\n", Run("var T::f32[2, 2] = zeros(2, 2)\nT[0, :] = f32[1, 2]\nT[1, :] = f32[3, 4]\nprint T"));
    }

    [Fact]
    public void BindingANamedTensorCopiesIt()
    {
        Assert.Equal("0.0\n9.0\n", Run("var A::f32[2] = zeros(2)\nvar B = A\nB[0] = 9.0\nprint A[0]\nprint B[0]"));
    }

    [Fact]
    public void AViewKeepsPointingAtItsTensor()
    {
        // `row` is a view, so a later write through A shows up in it.
        Assert.Equal("7.0\n", Run("var A::f32[2, 2] = zeros(2, 2)\nrow := A[0]\nA[0, 1] = 7.0\nprint row[1]"));
    }

    [Fact]
    public void IndexingErrorsCarryASpan()
    {
        var r = RunHelper.RuntimeError("M::f32[2, 3] = zeros(2, 3)\ni := 3\nprint M[0, i]");
        Assert.Equal([DiagnosticCategory.IndexOutOfRange], r.Categories);
        Assert.Contains("Index 3 is not in [0, 3)", r.Rendered);
        Assert.Contains("M[0, i]", r.Rendered);
        Assert.Equal([DiagnosticCategory.IndexOutOfRange], RunHelper.RuntimeError("M := zeros(3)\nprint M[1..5]").Categories);
        Assert.Equal([DiagnosticCategory.IndexOutOfRange], RunHelper.RuntimeError("var M = zeros(3)\nM[3] = 1.0").Categories);
        Assert.Equal([DiagnosticCategory.ShapeMismatch], RunHelper.CompileErrors("M := zeros(3)\nprint M[1, 2]"));
    }

    [Fact]
    public void RangeIndicesSupportStepsAndReversal()
    {
        Assert.Equal("[0.0, 2.0, 4.0]\n", Run("print arange(6)[0..6 by 2]"));
        Assert.Equal("[1.0, 2.0, 3.0]\n", Run("print arange(6)[1..=3]"));
        Assert.Equal("[5.0, 3.0, 1.0]\n", Run("print arange(6)[rev(1..6 by 2)]"));
        Assert.Equal("[]\n", Run("print arange(6)[2..2]"));
    }

    [Fact]
    public void RowsIterateAsViewsAndVectorsAsScalars()
    {
        Assert.Equal("[1.0, 2.0]\n[3.0, 4.0]\n", Run("loop r in f32[[1, 2], [3, 4]]:\n print r\n;"));
        Assert.Equal("11.0\n21.0\n", Run("loop x in f32[10, 20]:\n print x + 1.0\n;"));
        Assert.Equal("0 1.0\n1 2.0\n", Run("loop i, x in enumerate(f32[1, 2]):\n print \"{i} {x}\"\n;"));
        Assert.Equal("true\nfalse\n", Run("print 2.0 in f32[1, 2]\nprint 3.0 in f32[1, 2]"));
        Assert.Equal("true\n", Run("print 4.0 in f32[[1, 2], [3, 4]]"));
    }

    [Fact]
    public void PrintingAlignsColumnsAndSummarisesLongAxes()
    {
        Assert.Equal("[[  1.5, 22.25],\n [100.0,   3.0]]\n", Run("print f32[[1.5, 22.25], [100.0, 3.0]]"));
        Assert.Equal("[ 0.0,  1.0,  2.0,  ..., 17.0, 18.0, 19.0]\n", Run("print arange(20)"));
        Assert.Equal("[[1.00, 2.00],\n [3.00, 4.50]]\n", Run("print \"{f32[[1.0, 2.0], [3.0, 4.5]]:.2}\""));
    }

    [Fact]
    public void TensorLiteralsNeedMatchingRowsAndTheRightElementType()
    {
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors("x := f32[[1, 2], [3]]"));
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("x::f32[2] = f32[1.0, true]"));
        Assert.Equal("2\n", Run("x := [[1.0, 2.0], [3.0, 4.0]]\nprint len(x)"));                 // no tensor hint: still a List
        Assert.Equal("[[1.0, 2.0]]\n", Run("x::f32[?, ?] = [[1.0, 2.0]]\nprint x"));             // the expected type makes it a tensor
    }

    [Fact]
    public void ConstructorsAndTheirShapes()
    {
        Assert.Equal("[2, 3]\n", Run("print zeros(2, 3).shape()"));
        Assert.Equal("[3, 3]\n", Run("print eye(3).shape()"));
        Assert.Equal("[0.0, 0.5, 1.0]\n", Run("print linspace(0.0, 1.0, 3)"));
        Assert.Equal("[3.0, 2.0, 1.0]\n", Run("print arange(3.0, 0.0, -1.0)"));
        Assert.Equal("[1.0, 1.0]\n", Run("print ones(2)"));
        Assert.Equal("[[0.0, 1.0],\n [2.0, 3.0]]\n", Run("print reshape(arange(4), 2, 2)"));
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors("x := reshape(arange(6), 4, 2)"));
        Assert.Equal([DiagnosticCategory.ShapeMismatch], RunHelper.RuntimeError("n := 4\nx := reshape(arange(6), n, 2)").Categories);
        Assert.Equal([DiagnosticCategory.MathDomain], RunHelper.RuntimeError("n := -1\nx := zeros(n)").Categories);
        Assert.NotEmpty(RunHelper.CompileErrors("x := zeros(-1)"));   // a negative size is rejected at check time
    }

    [Fact]
    public void RandIsSeededAndInUnitRange()
    {
        Assert.Equal("true\n", Run("seed(5)\na := rand(100)\nseed(5)\nb := rand(100)\nprint a == b"));
        Assert.Equal("true\n", Run("a := rand(1000)\nprint min(a) >= 0.0 and max(a) < 1.0"));
        Assert.Equal("true\n", Run("a::f64[500] = rand(500)\nprint min(a) >= 0.0 and max(a) < 1.0"));
    }

    [Fact]
    public void Float16TensorsRoundToHalfPrecision()
    {
        Assert.Equal("[0.5, 1.0]\n", Run("print f16[0.25, 0.5] * 2.0"));
        Assert.Equal("[1.0, 1.0]\n", Run("x := f16[1.0, 1.0001]\nprint x"));   // 1.0001 is not representable in half precision
        Assert.Equal("[1.5, 2.5]\n", Run("print f16[1, 2] + 0.5"));
    }

    [Fact]
    public void Bf16AndSubByteTypesAreRecordedButNotComputedYet()
    {
        Assert.Equal("4\n", Run("fn f(w::bf16[4])::usize = 4\nfn g(q::u4[8])::usize = 8\nprint 4"));
        // They exist in the type system (a function may take one), but arithmetic and creation are still "not yet built".
        Assert.Contains(DiagnosticCategory.NotYetBuilt, RunHelper.CompileErrors("x::bf16[2] = zeros(2)"));
        Assert.Contains(DiagnosticCategory.NotYetBuilt, RunHelper.CompileErrors("fn f(a::bf16[2], b::bf16[2])::bf16[2] = a .+ b"));
        Assert.Contains(DiagnosticCategory.NotYetBuilt, RunHelper.CompileErrors("x::i8[2] = i8[1, 2]"));
        // bf16 is a separate name from f16: a bf16 tensor does not take an f16 one.
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("fn f(a::bf16[2])::usize = 2\nx::f16[2] = f16[1, 2]\nprint f(x)"));
    }

    [Fact]
    public void CsvRoundTripsATensor()
    {
        var p = Path.Combine(Path.GetTempPath(), $"splat_m3_{Guid.NewGuid():N}.csv").Replace('\\', '/');
        try
        {
            Assert.Equal("true\n[2, 3]\n", Run($"A::f32[2, 3] = f32[[1.5, 2.0, 3.25], [4.0, 5.5, 6.0]]\nsave_csv(\"{p}\", A)\nB := load_csv(\"{p}\")\nprint A == B\nprint B.shape()"));
            File.WriteAllText(p, "x,y\n1,2\n3,4\n");   // a header row is skipped
            Assert.Equal("[[1.0, 2.0],\n [3.0, 4.0]]\n", Run($"print load_csv(\"{p}\")"));
            Assert.Equal("[[1.0, 2.0],\n [3.0, 4.0]]\n", Run($"C::f64[?, ?] = load_csv(\"{p}\")\nprint C"));
            File.WriteAllText(p, "1,2\n3\n");
            Assert.Equal([DiagnosticCategory.IoError], RunHelper.RuntimeError($"print load_csv(\"{p}\")").Categories);
            File.WriteAllText(p, "1,2\n3,oops\n");
            Assert.Equal([DiagnosticCategory.IoError], RunHelper.RuntimeError($"print load_csv(\"{p}\")").Categories);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void CompoundAssignmentOnSlices()
    {
        Assert.Equal("[[2.0, 1.0],\n [1.0, 1.0]]\n", Run("var T::f32[2, 2] = ones(2, 2)\nT[0, 0] += 1.0\nprint T"));
        Assert.Equal("[[3.0, 3.0],\n [1.0, 1.0]]\n", Run("var T::f32[2, 2] = ones(2, 2)\nT[0, :] += f32[2, 2]\nprint T"));
        Assert.Equal([DiagnosticCategory.ImmutableMutation], RunHelper.CompileErrors("T::f32[2] = zeros(2)\nT[0] = 1.0"));
        Assert.Equal([DiagnosticCategory.MutateWhileIterating], RunHelper.CompileErrors("var T::f32[2, 2] = zeros(2, 2)\nloop r in T:\n T[0, 0] = 1.0\n;"));
    }

    // ---- Phase 13 ----

    [Fact]
    public void TheSpecSection9ShapeErrorIsReportedBeforeAnyOutput()
    {
        var r = RunHelper.Run("print \"before\"\nA::f32[3, 4] = zeros(3, 4)\nB::f32[5, 2] = zeros(5, 2)\nC := A @ B");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal("", r.Out);
        var d = Assert.Single(r.Diag.Diagnostics);
        Assert.Equal("SE00104", d.Code);
        Assert.Equal("📐 GEOMETRY CRIME [Line 4]: Tried to '@' a 'f32[3, 4]' with a 'f32[5, 2]'. 4 != 5. Even in parallel universes, these matrices cannot hug.", d.Message);
    }

    [Fact]
    public void TheTransposeTipAppearsOnlyWhenTransposingWouldFixIt()
    {
        // 3x4 @ 2x4: B' is 4x2, so it works.
        var fixable = RunHelper.Run("A::f32[3, 4] = zeros(3, 4)\nB::f32[2, 4] = zeros(2, 4)\nC := A @ B");
        Assert.EndsWith("Tip: Transpose with \"'\"?", fixable.Diag.Diagnostics[0].Message);
        // 3x4 @ 5x2: neither transposition helps.
        var hopeless = RunHelper.Run("A::f32[3, 4] = zeros(3, 4)\nB::f32[5, 2] = zeros(5, 2)\nC := A @ B");
        Assert.DoesNotContain("Tip", hopeless.Diag.Diagnostics[0].Message);
        // The left side may be the one to transpose: 4x3 @ 4x2 needs A'.
        var left = RunHelper.Run("A::f32[4, 3] = zeros(4, 3)\nB::f32[4, 2] = zeros(4, 2)\nC := A @ B");
        Assert.Contains("Tip", left.Diag.Diagnostics[0].Message);
        // And the tip is real: the suggested program checks and runs.
        Assert.Equal("[3, 2]\n", Run("A::f32[3, 4] = zeros(3, 4)\nB::f32[2, 4] = zeros(2, 4)\nC := A @ B'\nprint C.shape()"));
    }

    [Fact]
    public void MatrixProductValuesAndRanks()
    {
        Assert.Equal("[[ 58.0,  64.0],\n [139.0, 154.0]]\n", Run("A := f32[[1, 2, 3], [4, 5, 6]]\nB := f32[[7, 8], [9, 10], [11, 12]]\nprint A @ B"));
        Assert.Equal("[-2.0, -2.0]\n", Run("A := f32[[1, 2, 3], [4, 5, 6]]\nprint A @ f32[1, 0, -1]"));
        Assert.Equal("[ 9.0, 12.0, 15.0]\n", Run("A := f32[[1, 2, 3], [4, 5, 6]]\nprint f32[1, 2] @ A"));
        Assert.Equal("32.0\n", Run("print f32[1, 2, 3] @ f32[4, 5, 6]"));
        Assert.Equal("32.0\n", Run("print f32[1, 2, 3] · f32[4, 5, 6]"));
        Assert.Equal("[2, 2, 2]\n", Run("r := reshape(arange(8), 2, 2, 2) @ eye(2)\nprint r.shape()"));
        Assert.Contains(DiagnosticCategory.BadOperator, RunHelper.CompileErrors("x := f32[1, 2] · 3.0"));
        Assert.Contains(DiagnosticCategory.BadOperator, RunHelper.CompileErrors("A := zeros(2, 2)\nx := A @ 2.0"));
    }

    [Fact]
    public void BatchesBroadcastLikeElementwiseOperators()
    {
        Assert.Equal("[5, 2, 3]\n", Run("a := zeros(5, 2, 4)\nb := zeros(4, 3)\nc := a @ b\nprint c.shape()"));
        Assert.Equal("[5, 2, 3]\n", Run("a := zeros(1, 2, 4)\nb := zeros(5, 4, 3)\nc := a @ b\nprint c.shape()"));
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors("a := zeros(2, 2, 4)\nb := zeros(3, 4, 3)\nc := a @ b"));
    }

    [Fact]
    public void TransposeIsAViewAndDoubleTransposeIsTheSame()
    {
        Assert.Equal("[[1.0, 3.0],\n [2.0, 4.0]]\n", Run("print f32[[1, 2], [3, 4]]'"));
        Assert.Equal("true\n", Run("A := f32[[1, 2], [3, 4]]\nprint A'' == A'"));
        Assert.Equal("[3, 2]\n", Run("A := zeros(2, 3)\nB := A'\nprint B.shape()"));
        Assert.Equal("[3]\n", Run("v := zeros(3)\nw := v'\nprint w.shape()"));   // a vector is its own transpose
        Assert.Contains(DiagnosticCategory.BadOperator, RunHelper.CompileErrors("x := 5\ny := x'"));
        // R is a view of Q', so writes to Q show through it.
        Assert.Equal("[[1.0, 9.0],\n [3.0, 4.0]]\n", Run("var Q = f32[[1, 2], [3, 4]]\nR := Q'\nQ[0, 1] = 3.0\nQ[1, 0] = 9.0\nprint R"));
    }

    [Fact]
    public void SolveResidualIsTiny()
    {
        var o = Run("A::f32[3, 3] = f32[[4, 1, 0], [1, 5, 2], [0, 2, 6]]\nb::f32[3] = f32[1, 2, 3]\nx := A \\ b\nprint ||A @ x - b|| < 0.0001");
        Assert.Equal("true\n", o);
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors("A := zeros(3, 3)\nb := zeros(4)\nx := A \\ b"));
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors("A := zeros(3)\nb := zeros(3)\nx := A \\ b"));
        var singular = RunHelper.RuntimeError("A::f64[2, 2] = f64[[1, 2], [2, 4]]\nx := A \\ f64[1, 2]");
        Assert.Equal([DiagnosticCategory.MathDomain], singular.Categories);
        Assert.Contains("singular", singular.Rendered);
    }

    [Fact]
    public void CompoundMatmulKeepsTheShape()
    {
        Assert.Equal("[[2.0, 1.0],\n [4.0, 3.0]]\n", Run("var P = f32[[1, 2], [3, 4]]\nP @= f32[[0, 1], [1, 0]]\nprint P"));
        // A (2x3) @= B (3x2) would change P's type: rejected.
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors("var P = zeros(2, 3)\nP @= zeros(3, 2)"));
        Assert.Equal("[[ 4.0,  8.0],\n [12.0, 16.0]]\n", Run("var P = f32[[1, 2], [3, 4]]\nP .*= f32[[4, 4], [4, 4]]\nprint P"));
    }

    [Fact]
    public void DimensionVariablesAreInstantiatedPerCallSite()
    {
        const string gram = "fn gram(X::f32[$N, $F])::f32[$F, $F] = X' @ X\n";
        Assert.Equal("[3, 3]\n", Run(gram + "g := gram(zeros(5, 3))\nprint g.shape()"));
        Assert.Equal("[2, 2]\n", Run(gram + "g := gram(zeros(7, 2))\nprint g.shape()"));   // a different instantiation, same function
        const string pair = "fn add(a::f32[$N], b::f32[$N])::f32[$N] = a + b\n";
        Assert.Equal("[4.0, 6.0]\n", Run(pair + "print add(f32[1, 2], f32[3, 4])"));
        // $N bound to 2 by the first argument: a 3-vector second argument is a compile error at THIS call site only.
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors(pair + "x := add(f32[1, 2], f32[1, 2, 3])"));
        // The result type carries the binding: a 3x3 gram cannot be assigned to a 2x2 slot.
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors(gram + "g::f32[2, 2] = gram(zeros(5, 3))"));
        // Explicit generic dimension parameters work too.
        Assert.Equal("[2]\n", Run("fn first<$R, $C>(m::f32[$R, $C])::f32[$R] = m[:, 0]\nprint first(zeros(2, 5)).shape()"));
    }

    [Fact]
    public void ADimensionVariableInsideAGenericBodyIsRigid()
    {
        // Inside the body, $N and $M are unknown but fixed, and different.
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors("fn f(a::f32[$N], b::f32[$M])::f32[$N] = a + b"));
        Assert.Contains(DiagnosticCategory.ShapeMismatch, RunHelper.CompileErrors("fn f(a::f32[$N, $M])::f32[$M, $M] = a @ a"));   // a @ a needs $M == $N
        Assert.Equal("[2, 2]\n", Run("fn f(a::f32[$N, $M])::f32[$M, $M] = a' @ a\nprint f(zeros(3, 2)).shape()"));
    }

    [Fact]
    public void ExtendOnATensorTypeBindsDimensionsPerReceiver()
    {
        const string ext = "extend f32[$R, $C]:\n fn frob_sq(self)::f32 = ||self||_F ** 2\n fn gram(self)::f32[$C, $C] = self' @ self\n fn n_rows(self)::usize = len(self)\n;\n";
        Assert.Equal("25.0\n", Run(ext + "print f32[[3, 0], [0, 4]].frob_sq()"));
        Assert.Equal("[3, 3]\n", Run(ext + "g := zeros(4, 3).gram()\nprint g.shape()"));
        Assert.Equal("4\n", Run(ext + "print zeros(4, 3).n_rows()"));
        // The block is for rank-2 f32 tensors only.
        Assert.Contains(DiagnosticCategory.UndefinedName, RunHelper.CompileErrors(ext + "x := f32[1, 2].frob_sq()"));
        Assert.Contains(DiagnosticCategory.UndefinedName, RunHelper.CompileErrors(ext + "x := f64[[1, 2], [3, 4]].frob_sq()"));
        // A same-named method in two blocks for one tensor type is ambiguous (checked per element type and rank).
        Assert.Contains(DiagnosticCategory.AmbiguousMember, RunHelper.CompileErrors("extend f32[$A, $B]:\n fn m(self)::i32 = 1\n;\nextend f32[$C, $D]:\n fn m(self)::i32 = 2\n;"));
        Assert.Empty(RunHelper.Run("extend f32[$A]:\n fn m(self)::i32 = 1\n;\nextend f64[$A]:\n fn m(self)::i32 = 2\n;").Categories);
    }

    [Fact]
    public void DotProductAndTransposeInsideMethods()
    {
        Assert.Equal("14.0\n", Run("extend f32[$N]:\n fn norm_sq(self)::f32 = self · self\n;\nprint f32[1, 2, 3].norm_sq()"));
    }

    [Fact]
    public void LinearAlgebraResultsAreExactForSmallIntegerCases()
    {
        Assert.Equal("[1.0, 2.0]\n", Run("A::f64[2, 2] = f64[[2, 0], [0, 4]]\nprint A \\ f64[2, 8]"));
        Assert.Equal("[4.0, 3.0]\n", Run("P::f64[2, 2] = f64[[0, 1], [1, 0]]\nprint P \\ f64[3, 4]"));
    }

    // ---- Phase 14 ----

    [Fact]
    public void NormsEvaluate()
    {
        Assert.Equal("5.0\n", Run("print ||f64[3, 4]||"));
        Assert.Equal("7.0\n", Run("print ||f64[3, -4]||_1"));
        Assert.Equal("4.0\n", Run("print ||f64[3, -4]||_inf"));
        Assert.Equal("5.0\n", Run("print ||f64[[3, 0], [0, 4]]||_F"));
        Assert.Equal("5\n", Run("print ||-5||"));
        Assert.Equal("true\n", Run("a := f64[1, 2]\nb := f64[1, 2]\nprint ||a - b|| < 1e-12"));
        Assert.Contains(DiagnosticCategory.BadOperator, RunHelper.CompileErrors("x := ||\"txt\"||"));
    }

    [Theory]
    [InlineData("||a|| or ||b||", "(or (norm 2 a) (norm 2 b))")]
    [InlineData("|x| ||x||", "(lambda (params x) (norm 2 x))")]
    [InlineData("||a - b||_inf", "(norm inf (- a b))")]
    [InlineData("||A||_F ** 2", "(** (norm F A) 2)")]
    [InlineData("x |> |a| ||a||", "(|> x (lambda (params a) (norm 2 a)))")]
    [InlineData("||a|| > 1 and ||b||_1 < 2", "(and (> (norm 2 a) 1) (< (norm 1 b) 2))")]
    public void NormBarsAndLambdaBarsAreToldApart(string source, string expected) => Assert.Equal(expected, ParseHelper.Expr(source));

    [Fact]
    public void ANormCanBeThePrintArgument()
    {
        Assert.Equal("5.0\n", Run("v := f32[3, 4]\nprint ||v||"));
        Assert.Equal("6\n", Run("print sum[i in 0..4] i"));
        Assert.Equal("6\n", Run("print ∑[i in 0..4] i"));
    }

    [Fact]
    public void SumsAndProductsAreTypedByTheirBody()
    {
        Assert.Equal("55\n", Run("print ∑[i in 1..=10] i"));
        Assert.Equal("120\n", Run("print ∏[i in 1..=5] i"));
        Assert.Equal("3.0\n", Run("print ∑[i in 0..4] f64(i) * 0.5"));
        Assert.Equal("0\n1\n0.0\n", Run("print ∑[i in 0..0] i\nprint ∏[i in 3..3] i\nprint ∑[i in 5..2] 1.5"));
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("x := ∑[i in 0..3] \"s\""));
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("x := ∑[i in 3.5] i"));
        Assert.Equal("true\n", Run("x::i64 = ∑[i in 0..10] i\nprint x == 45"));
    }

    [Fact]
    public void AsciiAndUnicodeSpellingsParseToTheSameNode()
    {
        Assert.Equal(ParseHelper.Expr("∑[i in 0..N] i + 1"), ParseHelper.Expr("sum[i in 0..N] i + 1"));
        Assert.Equal(ParseHelper.Expr("∏[i in 0..N] i"), ParseHelper.Expr("prod[i in 0..N] i"));
        Assert.Equal(ParseHelper.Expr("∫[0.0..1.0] |x| x"), ParseHelper.Expr("intg[0.0..1.0] |x| x"));
    }

    [Fact]
    public void IntegerSumsAreCheckedAndNameTheTermThatOverflows()
    {
        var r = RunHelper.RuntimeError("total := ∑[i in 0..100000] i * i");
        Assert.Equal([DiagnosticCategory.IntegerOverflow], r.Categories);
        Assert.Contains("for i = 1861", r.Rendered);   // 1861 is the first term where the running total passes i32
        // The same loop in i64 is fine, and a brute-force loop agrees about where the i32 one fails.
        Assert.Equal("333328333350000\n", Run("big::i64 = ∑[i in 0..100000] i * i\nprint big"));
        long running = 0; int failedAt = -1;
        for (int i = 0; i < 100000; i++) { running += (long)i * i; if (running > int.MaxValue) { failedAt = i; break; } }
        Assert.Equal(1861, failedAt);
        Assert.Equal([DiagnosticCategory.IntegerOverflow], RunHelper.RuntimeError("p := ∏[i in 1..=20] i").Categories);
    }

    [Fact]
    public void SumsOverTensorIndexingAndOtherIterables()
    {
        Assert.Equal("70.0\n", Run("v := f32[1, 2, 3, 4]\nw := f32[5, 6, 7, 8]\nprint ∑[i in 0..4] v[i] * w[i]"));
        Assert.Equal("5.0\n", Run("M := f32[[1, 2], [3, 4]]\nprint ∑[i in 0..2] M[i, i]"));
        Assert.Equal("10.0\n", Run("M := f32[[1, 2], [3, 4]]\nprint ∑[i in 0..2] ∑[j in 0..2] M[i, j]"));
        Assert.Equal("60\n", Run("print ∑[x in [10, 20, 30]] x"));
        Assert.Equal("18\n", Run("print ∑[i in 0..10 by 3] i"));
        Assert.Equal("2.0\n", Run("print ∑[x in f32[0.5, 1.5]] x"));
    }

    [Fact]
    public void TheReductionKeepsItsBinderAndPurityForPhase30()
    {
        var (program, diag, src) = ParseHelper.Parse("n := 5\nfn f(x::i32)::i32 = x\npure := ∑[i in 0..n] i * i + 1\nimpure := ∑[i in 0..n] f(i)\nfloaty := ∑[i in 0..n] sqrt(f64(i))");
        Assert.False(diag.HasErrors);
        var info = TypeChecker.Check(program, diag);
        Assert.False(diag.HasErrors);
        var reds = program.Statements.OfType<Splat.Syntax.BindStmt>().Select(b => b.Value).OfType<Splat.Syntax.ReduceExpr>().ToList();
        Assert.Equal(3, reds.Count);
        Assert.All(reds, r => { Assert.Equal("i", r.Variable); Assert.IsAssignableFrom<Splat.Syntax.Expr>(r.Range); });
        Assert.True(info.IsPureReduction(reds[0]));
        Assert.False(info.IsPureReduction(reds[1]));   // calls a user function
        Assert.True(info.IsPureReduction(reds[2]));
    }

    [Fact]
    public void IntegralsAreAccurate()
    {
        double Num(string code) => double.Parse(Run(code).Trim(), CultureInfo.InvariantCulture);
        // The integral of sin from 0 to pi is 2.
        Assert.Equal(2.0, Num("r::f64 = ∫[0.0..3.141592653589793] |x| sin(x)\nprint r"), 8);
        // The Gaussian integral over the line is sqrt(pi).
        Assert.Equal(Math.Sqrt(Math.PI), Num("r::f64 = ∫[-8.0..8.0] |x| exp(-x * x)\nprint r"), 8);
        // A 2D integral.
        Assert.Equal(1.0, Num("r::f64 = ∫[0.0..1.0, 0.0..2.0] |x, y| x * y\nprint r"), 8);
        // A 3D integral.
        Assert.Equal(1.5, Num("r::f64 = ∫[0.0..1.0, 0.0..1.0, 0.0..1.0] |x, y, z| x + y + z\nprint r"), 8);
        // Quasi-Monte Carlo above three dimensions: the exact answer is 2; a 65536-point Halton set gets close.
        Assert.Equal(2.0, Num("r::f64 = ∫[0.0..1.0, 0.0..1.0, 0.0..1.0, 0.0..1.0, 0.0..1.0, 0.0..1.0] |a, b, c, d, e, f| a*a + b*b + c*c + d*d + e*e + f*f\nprint r"), 2);
        // Reversed bounds flip the sign; the ASCII spelling is the same operation.
        Assert.Equal(-0.5, Num("r::f64 = intg[1.0..0.0] |x| x\nprint r"), 10);
        // A tolerance as a named argument.
        Assert.Equal(Math.PI / 4, Num("r::f64 = ∫[0.0..1.0, tol = 1e-12] |x| 1.0 / (1.0 + x * x)\nprint r"), 11);
        // Oscillating integrands need many subdivisions but converge.
        Assert.Equal((1 - Math.Cos(50.0)) / 50.0, Num("r::f64 = ∫[0.0..1.0] |x| sin(50.0 * x)\nprint r"), 8);
    }

    [Fact]
    public void AnF32IntegralComputesInDoubleAndRoundsTheAnswer()
    {
        Assert.Equal("0.5\n", Run("f::f32 = ∫[0.0..1.0] |x| x\nprint f"));
    }

    [Fact]
    public void BadIntegralsAreReported()
    {
        var nan = RunHelper.RuntimeError("r::f64 = ∫[-1.0..1.0] |x| sqrt(x)");
        Assert.Equal([DiagnosticCategory.MathDomain], nan.Categories);
        Assert.Contains("NaN", nan.Rendered);
        var div = RunHelper.RuntimeError("r::f64 = ∫[0.0..1.0] |x| 1.0 / x");
        Assert.Equal([DiagnosticCategory.MathDomain], div.Categories);
        Assert.Contains("did not converge", div.Rendered);
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("r := ∫[0..1] |x| x"));                 // integer bounds
        Assert.Contains(DiagnosticCategory.WrongArgCount, RunHelper.CompileErrors("r := ∫[0.0..1.0] |x, y| x"));        // one range, two parameters
        Assert.Contains(DiagnosticCategory.UnexpectedToken, RunHelper.CompileErrors("r := ∫[0.0..1.0, step = 2] |x| x"));
    }

    [Fact]
    public void TheMathPrelude()
    {
        Assert.Equal("4.0\n", Run("print sqrt(16.0)"));
        Assert.Equal("1.0\n0.0\n", Run("print exp(0.0)\nprint ln(1.0)"));
        Assert.Equal("3\n3.5\n", Run("print abs(-3)\nprint abs(-3.5)"));
        Assert.Equal("2.0\n3.0\n", Run("print floor(2.7)\nprint ceil(2.2)"));
        Assert.Equal("7\n3.5\n", Run("print max(3, 7)\nprint max(3.5, -1.0)"));
        Assert.Equal("1024.0\n1024\n1024.0\n", Run("print pow(2.0, 10.0)\nprint 2 ** 10\nprint 2.0 ** 10"));
        Assert.Equal("[1.0, 2.0, 3.0]\n", Run("print sqrt(f32[1, 4, 9])"));
        Assert.Equal("[1.0, 4.0, 9.0]\n", Run("print f32[1, 2, 3] ** 2"));
        Assert.Equal("[1.0, 4.0, 3.0]\n", Run("print f32[1, 2, 3] ** f32[3, 2, 1]"));
        Assert.Equal("0.842701\n", Run("print \"{erf(1.0):.6}\""));
        Assert.Equal("3\n", Run("print len(f32[1, 2, 3])"));
        // Pipes and a user binding that shadows a prelude name.
        Assert.Equal("4.0\n", Run("print 16.0 |> sqrt"));
        Assert.Equal("5\n", Run("mean := 5\nprint mean"));
        Assert.Equal("9\n", Run("fn max(a::i32, b::i32)::i32 = a + b\nprint max(4, 5)"));
    }

    [Fact]
    public void ReductionsOverTensorsAndLists()
    {
        Assert.Equal("21.0\n3.5\n6.0\n1.0\n5\n0\n", Run("A := f32[[1, 2, 3], [4, 5, 6]]\nprint sum(A)\nprint mean(A)\nprint max(A)\nprint min(A)\nprint argmax(A)\nprint argmin(A)"));
        Assert.Equal("6\n9\n2\n24\n", Run("print sum([1, 2, 3])\nprint max([4, 9, 2])\nprint min([4, 9, 2])\nprint prod([1, 2, 3, 4])"));
        Assert.Equal("3.0\n", Run("print mean([1.0, 2.0, 6.0])"));
        Assert.Equal("0\n", Run("xs::List<i32> = []\nprint sum(xs)"));
        Assert.Equal([DiagnosticCategory.MathDomain], RunHelper.RuntimeError("xs::List<i32> = []\nprint max(xs)").Categories);
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("print mean([1, 2])"));
        Assert.Contains(DiagnosticCategory.TypeMismatch, RunHelper.CompileErrors("print sum(\"abc\")"));
    }

    [Fact]
    public void IntegerPowersAndNegativeExponents()
    {
        Assert.Contains(DiagnosticCategory.BadOperator, RunHelper.CompileErrors("x := 2 ** -1"));
        Assert.Equal([DiagnosticCategory.MathDomain], RunHelper.RuntimeError("n := -1\nx := 2 ** n").Categories);   // not a literal: still caught at run time
        Assert.Equal("0.5\n", Run("print 2.0 ** -1"));                                                           // a float base is fine
        Assert.Equal("0.5\n", Run("print 2.0 ** -1.0"));
    }

    [Fact]
    public void FloatMathGoesThroughTheTranscendentalsSeam()
    {
        // Phase 29 swaps the implementation behind this one property; the default is the managed one.
        Assert.IsType<NetTranscendentals>(Transcendentals.Current);
        Assert.Equal(Math.Exp(1.5), Transcendentals.Current.Exp(1.5));
    }

    [Fact]
    public void TensorsPersistAcrossReplEntries()
    {
        var o = new StringWriter { NewLine = "\n" };
        var session = new SessionState(new StringReader(""), o, new StringWriter());
        Assert.Equal(RunOutcome.Ok, session.Eval("A::f32[2, 2] = f32[[1, 2], [3, 4]]").Outcome);
        Assert.Equal(RunOutcome.Ok, session.Eval("fn tr(M::f32[$N, $N])::f32 = ∑[i in 0..2] M[i, i]").Outcome);
        var echo = session.Eval("tr(A)");
        Assert.Equal(RunOutcome.Ok, echo.Outcome);
        Assert.Equal("=> 5.0 :: f32", echo.Echo);
        Assert.Equal("=> [[ 7.0, 10.0],\n [15.0, 22.0]] :: f32[2, 2]", session.Eval("A @ A").Echo);
        // A bad shape in a later entry leaves the session alone.
        var bad = session.Eval("B := A @ zeros(3, 3)");
        Assert.NotEqual(RunOutcome.Ok, bad.Outcome);
        Assert.Equal(RunOutcome.Ok, session.Eval("A @ A").Outcome);
        _ = o;
    }

    [Fact]
    public void GuardedShapesDoNotBreakTheCommonPath()
    {
        // A dynamic dimension that agrees with the slot costs nothing and prints normally.
        Assert.Equal("2.0\n", Run("fn mk(n::usize)::f32[?] = ones(n)\nx::f32[2] = mk(2)\nprint sum(x)"));
        var r = RunHelper.RuntimeError("fn mk(n::usize)::f32[?] = ones(n)\nx::f32[2] = mk(3)");
        Assert.Equal([DiagnosticCategory.ShapeMismatch], r.Categories);
    }
}

public class PurityTests
{
    [Theory]
    [InlineData("i * i + 1", true)]
    [InlineData("sqrt(f64(i))", true)]
    [InlineData("v[i] * w[i]", true)]
    [InlineData("-(i % 3)", true)]
    [InlineData("print(i)", false)]
    [InlineData("\"s{i}\"", false)]
    [InlineData("f(i)", false)]
    public void BodiesArePureOnlyWhenTheyCannotHaveEffects(string body, bool pure)
    {
        var src = $"v := f32[1, 2, 3]\nw := f32[1, 2, 3]\nfn f(x::i32)::i32 = x\nr := ∑[i in 0..3] {body}";
        var (program, diag, _) = ParseHelper.Parse(src);
        var info = TypeChecker.Check(program, diag);
        var red = program.Statements.OfType<Splat.Syntax.BindStmt>().Select(b => b.Value).OfType<Splat.Syntax.ReduceExpr>().Last();
        // (print(...) and a template are also type errors; purity is still recorded)
        Assert.Equal(pure, info.IsPureReduction(red));
    }
}

public class Milestone3GoldenTests
{
    [Theory]
    [InlineData("tensors_basic.splat")]
    [InlineData("tensors_slicing.splat")]
    [InlineData("tensors_broadcast.splat")]
    [InlineData("tensors_matmul.splat")]
    [InlineData("tensors_generic.splat")]
    [InlineData("tensors_solve.splat")]
    [InlineData("math_norms.splat")]
    [InlineData("math_reduce.splat")]
    [InlineData("math_integrals.splat")]
    public void ProgramsRunAndMatchTheirExpectedOutput(string file)
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, file), file);
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Assert.Empty(r.Diag.Diagnostics);
        Golden.Compare(Golden.Dir, Path.ChangeExtension(file, ".out"), r.Out);
    }

    [Theory]
    [InlineData("bad_tensor_shapes.splat", RunOutcome.CompileError)]
    [InlineData("bad_matmul.splat", RunOutcome.CompileError)]
    [InlineData("bad_math.splat", RunOutcome.CompileError)]
    [InlineData("bad_m3_shape.splat", RunOutcome.CompileError)]
    [InlineData("bad_tensor_runtime.splat", RunOutcome.RuntimeError)]
    [InlineData("bad_singular.splat", RunOutcome.RuntimeError)]
    [InlineData("bad_integral_nan.splat", RunOutcome.RuntimeError)]
    [InlineData("bad_integral_diverge.splat", RunOutcome.RuntimeError)]
    [InlineData("bad_sum_overflow.splat", RunOutcome.RuntimeError)]
    public void BadProgramsReportTheirDiagnostics(string file, RunOutcome expected)
    {
        var r = RunHelper.Run(Golden.Read(Golden.Dir, file), file);
        Assert.Equal(expected, r.Outcome);
        if (expected == RunOutcome.CompileError) Assert.Equal("", r.Out);
        Assert.All(r.Diag.Diagnostics, d => Assert.Matches("^S[EW][0-9]{5}$", d.Code));
        Golden.Compare(Golden.Dir, Path.ChangeExtension(file, ".err"), r.Rendered);
    }

    [Fact]
    public void AcceptanceScriptRunsAndMatchesItsExpectedOutput()
    {
        var r = RunHelper.Run(Golden.Read(Golden.M1Dir, "m3_acceptance.splat"), "m3_acceptance.splat");
        Assert.True(r.Outcome == RunOutcome.Ok, r.Rendered);
        Assert.Empty(r.Diag.Diagnostics);
        Golden.Compare(Golden.M1Dir, "m3_acceptance.out", r.Out);
    }

    [Fact]
    public void AppendingTheShapeErrorStopsTheWholeAcceptanceScriptBeforeAnyOutput()
    {
        var script = Golden.Read(Golden.M1Dir, "m3_acceptance.splat") + "\nC := zeros(3, 4) @ zeros(5, 2)\n";
        var r = RunHelper.Run(script, "m3_acceptance.splat");
        Assert.Equal(RunOutcome.CompileError, r.Outcome);
        Assert.Equal("", r.Out);
        Assert.Equal([DiagnosticCategory.ShapeMismatch], r.Categories);
    }
}
