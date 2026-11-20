using System.Numerics;
using System.Numerics.Tensors;
using Splat.Data;

namespace Splat.Runtime;

public enum BinOp { Add, Sub, Mul, Div, Pow }
public enum UnOp { Neg, Abs, Sqrt, Exp, Ln, Sin, Cos, Tan, Tanh, Erf, Floor, Ceil }
public enum RedOp { Sum, Prod, Mean, Max, Min, ArgMax, ArgMin, NormL1, NormL2, NormInf }

/// <summary>
/// Every elementwise and reduction operation the interpreter performs on tensors goes through this one small interface
/// (spec 13.3.2: the Phase 29 Reference executor and the Phase 31 native kernels implement it too). <see cref="ManagedTensorKernels"/>
/// is the managed implementation; a BLAS- or TorchSharp-backed one could replace it.
/// </summary>
public interface ITensorKernels
{
    /// <summary>Elementwise <paramref name="op"/> with NumPy trailing-dimension broadcasting. Both operands have the same element type.</summary>
    Tensor Binary(BinOp op, Tensor a, Tensor b, string opText = "");
    Tensor Unary(UnOp op, Tensor a);
    /// <summary>Reduces every element to one number (the flat row-major index for ArgMax/ArgMin).</summary>
    double Reduce(RedOp op, Tensor a);
    /// <summary><c>A @ B</c> with the matrix, matrix-vector, vector-matrix, dot and batch rules of spec 4.2.</summary>
    Tensor MatMul(Tensor a, Tensor b);
}

/// <summary>
/// The seam for <c>exp ln sin cos tan tanh erf pow</c> (build plan Phase 14). Phase 29 swaps in <c>splat_libm</c> behind it.
/// Until then the .NET <see cref="Math"/> results may differ from native code by 1 ulp (see docs/decisions.md).
/// </summary>
public interface ITranscendentals
{
    double Exp(double x);
    double Ln(double x);
    double Sin(double x);
    double Cos(double x);
    double Tan(double x);
    double Tanh(double x);
    double Erf(double x);
    double Pow(double x, double y);
}

public sealed class NetTranscendentals : ITranscendentals
{
    public double Exp(double x) => Math.Exp(x);
    public double Ln(double x) => Math.Log(x);
    public double Sin(double x) => Math.Sin(x);
    public double Cos(double x) => Math.Cos(x);
    public double Tan(double x) => Math.Tan(x);
    public double Tanh(double x) => Math.Tanh(x);
    public double Pow(double x, double y) => Math.Pow(x, y);

    /// <summary>
    /// erf by the series 2/sqrt(pi) * exp(-x^2) * sum 2^n x^(2n+1) / (1*3*...*(2n+1)): every term is positive, so there is no
    /// cancellation, and it reaches double precision for |x| below 6 (beyond that erf is 1 to the last bit).
    /// </summary>
    public double Erf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        double ax = Math.Abs(x);
        if (ax >= 6) return Math.Sign(x);
        double term = ax, sum = ax, x2 = ax * ax;
        for (int n = 1; n < 400; n++)
        {
            term *= 2 * x2 / (2 * n + 1);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        double r = 2 / Math.Sqrt(Math.PI) * Math.Exp(-x2) * sum;
        return x < 0 ? -r : r;
    }
}

public static class Transcendentals
{
    /// <summary>The implementation every elementwise and scalar math function uses.</summary>
    public static ITranscendentals Current { get; set; } = new NetTranscendentals();
}

/// <summary>
/// The one place every float reduction (<c>sum</c>, <c>mean</c>, norms, <c>∑</c> over floats) accumulates. Phase 29 replaces the
/// pairwise tree here with the canonical reduction tree of spec 13.10.2.
/// </summary>
public static class Reduce
{
    public static double Sum<T>(ReadOnlySpan<T> v) where T : IFloatingPointIeee754<T> => Stats.Sum(v);

    public static double SumSq<T>(ReadOnlySpan<T> v) where T : IFloatingPointIeee754<T>
    {
        var acc = new PairwiseSum();
        foreach (var x in v) { double d = double.CreateChecked(x); acc.Add(d * d); }
        return acc.Total;
    }

    public static double SumAbs<T>(ReadOnlySpan<T> v) where T : IFloatingPointIeee754<T>
    {
        var acc = new PairwiseSum();
        foreach (var x in v) acc.Add(Math.Abs(double.CreateChecked(x)));
        return acc.Total;
    }

    public static double Prod<T>(ReadOnlySpan<T> v) where T : IFloatingPointIeee754<T>
    {
        double p = 1;
        foreach (var x in v) p *= double.CreateChecked(x);
        return p;
    }

    /// <summary>Largest absolute value; NaN if any element is NaN.</summary>
    public static double MaxAbs<T>(ReadOnlySpan<T> v) where T : IFloatingPointIeee754<T>
    {
        double m = 0;
        foreach (var x in v)
        {
            double a = Math.Abs(double.CreateChecked(x));
            if (double.IsNaN(a)) return double.NaN;
            if (a > m) m = a;
        }
        return m;
    }
}

/// <summary>The managed tensor kernels: <see cref="TensorPrimitives"/> for the contiguous fast paths, odometer loops for views and broadcasts.</summary>
public sealed class ManagedTensorKernels : ITensorKernels
{
    public static readonly ManagedTensorKernels Instance = new();

    // ---- elementwise ----

    public Tensor Binary(BinOp op, Tensor a, Tensor b, string opText = "")
    {
        if (a.Elem != b.Elem) throw new TensorError($"Tried to combine a '{a.ElemName}' tensor with a '{b.ElemName}' tensor; the element types must match.");
        var shape = TensorOps.BroadcastShape(a, b, opText);
        var dst = Tensor.Zeros(a.Elem, shape);
        if (dst.Count == 0) return dst;
        switch (a.Elem)
        {
            case TensorElem.F64: BinaryLoop<double>(op, a, b, dst); break;
            case TensorElem.F32: BinaryLoop<float>(op, a, b, dst); break;
            default: BinaryLoop<Half>(op, a, b, dst); break;
        }
        return dst;
    }

    private static T ApplyBin<T>(BinOp op, T x, T y) where T : IFloatingPointIeee754<T> => op switch
    {
        BinOp.Add => x + y,
        BinOp.Sub => x - y,
        BinOp.Mul => x * y,
        BinOp.Div => x / y,
        _ => T.CreateChecked(Transcendentals.Current.Pow(double.CreateChecked(x), double.CreateChecked(y))),
    };

    private static void BinaryLoop<T>(BinOp op, Tensor a, Tensor b, Tensor dst) where T : unmanaged, IFloatingPointIeee754<T>
    {
        var A = a.Buffer.AsSpan<T>();
        var B = b.Buffer.AsSpan<T>();
        var D = dst.ContiguousSpan<T>();
        long n = dst.Count;

        // Fast paths (vectorised): same shape, or a tensor against one scalar on the right. Pow stays on the transcendental seam.
        if (op != BinOp.Pow && a.IsContiguous && a.Shape.AsSpan().SequenceEqual(dst.Shape))
        {
            var x = A.Slice((int)a.Offset, (int)n);
            if (b.IsContiguous && b.Shape.AsSpan().SequenceEqual(dst.Shape))
            {
                var y = B.Slice((int)b.Offset, (int)n);
                switch (op)
                {
                    case BinOp.Add: TensorPrimitives.Add<T>(x, y, D); return;
                    case BinOp.Sub: TensorPrimitives.Subtract<T>(x, y, D); return;
                    case BinOp.Mul: TensorPrimitives.Multiply<T>(x, y, D); return;
                    case BinOp.Div: TensorPrimitives.Divide<T>(x, y, D); return;
                }
            }
            else if (b.Count == 1)
            {
                T y = B[(int)b.Offset];
                switch (op)
                {
                    case BinOp.Add: TensorPrimitives.Add<T>(x, y, D); return;
                    case BinOp.Sub: TensorPrimitives.Subtract<T>(x, y, D); return;
                    case BinOp.Mul: TensorPrimitives.Multiply<T>(x, y, D); return;
                    case BinOp.Div: TensorPrimitives.Divide<T>(x, y, D); return;
                }
            }
        }

        var ea = TensorOps.BroadcastTo(a, dst.Shape);
        var eb = TensorOps.BroadcastTo(b, dst.Shape);
        int r = dst.Rank;
        var idx = new long[Math.Max(r, 1)];
        long pa = ea.Offset, pb = eb.Offset;
        long inner = r == 0 ? 1 : dst.Shape[r - 1];
        long sa = r == 0 ? 0 : ea.Strides[r - 1], sb = r == 0 ? 0 : eb.Strides[r - 1];
        for (long k = 0; k < n;)
        {
            for (long j = 0; j < inner; j++) D[(int)(k + j)] = ApplyBin(op, A[(int)(pa + j * sa)], B[(int)(pb + j * sb)]);
            k += inner;
            for (int d = r - 2; d >= 0; d--)
            {
                idx[d]++; pa += ea.Strides[d]; pb += eb.Strides[d];
                if (idx[d] < dst.Shape[d]) break;
                pa -= ea.Strides[d] * dst.Shape[d]; pb -= eb.Strides[d] * dst.Shape[d];
                idx[d] = 0;
            }
        }
    }

    public Tensor Unary(UnOp op, Tensor a)
    {
        var dst = Tensor.Zeros(a.Elem, (long[])a.Shape.Clone());
        switch (a.Elem)
        {
            case TensorElem.F64: UnaryLoop<double>(op, a, dst); break;
            case TensorElem.F32: UnaryLoop<float>(op, a, dst); break;
            default: UnaryLoop<Half>(op, a, dst); break;
        }
        return dst;
    }

    private static T ApplyUn<T>(UnOp op, T x) where T : IFloatingPointIeee754<T>
    {
        var m = Transcendentals.Current;
        switch (op)
        {
            case UnOp.Neg: return -x;
            case UnOp.Abs: return T.Abs(x);
            case UnOp.Sqrt: return T.Sqrt(x);
            case UnOp.Floor: return T.Floor(x);
            case UnOp.Ceil: return T.Ceiling(x);
        }
        double d = double.CreateChecked(x);
        double r = op switch
        {
            UnOp.Exp => m.Exp(d), UnOp.Ln => m.Ln(d), UnOp.Sin => m.Sin(d), UnOp.Cos => m.Cos(d),
            UnOp.Tan => m.Tan(d), UnOp.Tanh => m.Tanh(d), _ => m.Erf(d),
        };
        return T.CreateChecked(r);
    }

    private static void UnaryLoop<T>(UnOp op, Tensor a, Tensor dst) where T : unmanaged, IFloatingPointIeee754<T>
    {
        var D = dst.ContiguousSpan<T>();
        var A = a.Buffer.AsSpan<T>();
        if (a.IsContiguous && op == UnOp.Neg) { TensorPrimitives.Negate<T>(A.Slice((int)a.Offset, D.Length), D); return; }
        if (a.IsContiguous && op == UnOp.Abs) { TensorPrimitives.Abs<T>(A.Slice((int)a.Offset, D.Length), D); return; }
        int i = 0;
        foreach (var p in a.Positions()) D[i++] = ApplyUn(op, A[(int)p]);
    }

    // ---- reductions ----

    public double Reduce(RedOp op, Tensor a) => a.Elem switch
    {
        TensorElem.F64 => ReduceT<double>(op, a),
        TensorElem.F32 => ReduceT<float>(op, a),
        _ => ReduceT<Half>(op, a),
    };

    private static double ReduceT<T>(RedOp op, Tensor a) where T : unmanaged, IFloatingPointIeee754<T>
    {
        var c = a.Contiguous();
        var v = (ReadOnlySpan<T>)c.ContiguousSpan<T>();
        if (v.Length == 0 && op is RedOp.Max or RedOp.Min or RedOp.ArgMax or RedOp.ArgMin)
            throw new TensorMathError("There is nothing to take the " + op.ToString().ToLowerInvariant() + " of: the tensor is empty.");
        return op switch
        {
            RedOp.Sum => Splat.Runtime.Reduce.Sum(v),
            RedOp.Prod => Splat.Runtime.Reduce.Prod(v),
            RedOp.Mean => Stats.Mean(v),
            RedOp.Max => Stats.Max(v),
            RedOp.Min => Stats.Min(v),
            RedOp.ArgMax => Stats.ArgMax(v),
            RedOp.ArgMin => Stats.ArgMin(v),
            RedOp.NormL1 => Splat.Runtime.Reduce.SumAbs(v),
            RedOp.NormL2 => Math.Sqrt(Splat.Runtime.Reduce.SumSq(v)),
            _ => Splat.Runtime.Reduce.MaxAbs(v),
        };
    }

    // ---- matrix product ----

    public Tensor MatMul(Tensor a, Tensor b) => TensorOps.MatMul(a, b, blocked: true);
}
