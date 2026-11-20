using System.Numerics;
using System.Runtime.InteropServices;

namespace Splat.Runtime;

/// <summary>Broadcasting, the matrix product (a blocked kernel and a naive reference) and the linear solver.</summary>
internal static class TensorOps
{
    // ---- broadcasting ----

    public static long[] BroadcastShape(Tensor a, Tensor b, string opText) =>
        BroadcastShapes(a.Shape, b.Shape, $"Tried to '{opText}' a '{a.ShapeText}' with a '{b.ShapeText}'.");

    /// <summary>NumPy trailing-dimension broadcasting: equal dimensions, or one of them is 1.</summary>
    public static long[] BroadcastShapes(long[] a, long[] b, string headline)
    {
        int n = Math.Max(a.Length, b.Length);
        var r = new long[n];
        for (int k = 0; k < n; k++)
        {
            long x = k < a.Length ? a[a.Length - 1 - k] : 1;
            long y = k < b.Length ? b[b.Length - 1 - k] : 1;
            if (x == y || y == 1) r[n - 1 - k] = x;
            else if (x == 1) r[n - 1 - k] = y;
            else throw new TensorError($"{headline} Counting from the right, axis {k + 1} has {x} against {y}, and neither is 1.",
                "Broadcasting lines the shapes up on the right: each pair of dimensions must be equal, or one of them must be 1.");
        }
        return r;
    }

    /// <summary>A view of <paramref name="t"/> with <paramref name="shape"/>: broadcast axes get stride 0 (no copy).</summary>
    public static Tensor BroadcastTo(Tensor t, long[] shape)
    {
        if (t.Shape.AsSpan().SequenceEqual(shape)) return t;
        var strides = new long[shape.Length];
        int lead = shape.Length - t.Rank;
        if (lead < 0) throw new TensorError($"Tried to broadcast a '{t.ShapeText}' down to rank {shape.Length}.");
        for (int i = 0; i < t.Rank; i++)
        {
            if (t.Shape[i] == shape[lead + i]) strides[lead + i] = t.Strides[i];
            else if (t.Shape[i] == 1) strides[lead + i] = 0;
            else throw new TensorError($"Tried to broadcast a '{t.ShapeText}' onto shape [{string.Join(", ", shape)}]: axis {lead + i} has {t.Shape[i]} against {shape[lead + i]}.",
                "Broadcasting lines the shapes up on the right: each pair of dimensions must be equal, or one of them must be 1.");
        }
        return t.View(t.Offset, (long[])shape.Clone(), strides);
    }

    // ---- matrix product ----

    public static Tensor MatMul(Tensor a, Tensor b, bool blocked)
    {
        if (a.Elem != b.Elem) throw new TensorError($"Tried to '@' a '{a.ElemName}' tensor with a '{b.ElemName}' tensor; the element types must match.");
        int ra = a.Rank, rb = b.Rank;
        if (ra == 0 || rb == 0) throw new TensorError("A matrix product needs tensors on both sides.");

        long m = ra >= 2 ? a.Shape[^2] : 1, k = a.Shape[^1];
        long k2 = rb >= 2 ? b.Shape[^2] : b.Shape[0], n = rb >= 2 ? b.Shape[^1] : 1;
        if (k != k2)
            throw new TensorError($"mismatch", null)
            {
                MatMul = (a.ShapeText, b.ShapeText, k, k2, ra >= 2 && rb >= 2 && (k == n || m == k2)),
            };

        var batchA = ra > 2 ? a.Shape[..^2] : [];
        var batchB = rb > 2 ? b.Shape[..^2] : [];
        var batch = BroadcastShapes(batchA, batchB, $"Tried to '@' a '{a.ShapeText}' with a '{b.ShapeText}'.");

        var outShape = new List<long>(batch);
        if (ra >= 2) outShape.Add(m);
        if (rb >= 2) outShape.Add(n);
        var result = Tensor.Zeros(a.Elem, [.. outShape]);
        if (result.Count == 0) return result;

        var ac = a.Contiguous();
        var bc = b.Contiguous();
        long[] sa = MatrixStrides(batchA, batch.Length), sb = MatrixStrides(batchB, batch.Length);
        long batches = batch.Aggregate(1L, (x, y) => x * y);
        var idx = new long[batch.Length];
        for (long bi = 0; bi < batches; bi++)
        {
            long offA = 0, offB = 0;
            for (int d = 0; d < batch.Length; d++) { offA += idx[d] * sa[d]; offB += idx[d] * sb[d]; }
            switch (a.Elem)
            {
                case TensorElem.F64:
                    Gemm<double>(ac.ContiguousSpan<double>().Slice((int)(offA * m * k), (int)(m * k)),
                        bc.ContiguousSpan<double>().Slice((int)(offB * k * n), (int)(k * n)),
                        result.ContiguousSpan<double>().Slice((int)(bi * m * n), (int)(m * n)), (int)m, (int)k, (int)n, blocked);
                    break;
                case TensorElem.F32:
                    Gemm<float>(ac.ContiguousSpan<float>().Slice((int)(offA * m * k), (int)(m * k)),
                        bc.ContiguousSpan<float>().Slice((int)(offB * k * n), (int)(k * n)),
                        result.ContiguousSpan<float>().Slice((int)(bi * m * n), (int)(m * n)), (int)m, (int)k, (int)n, blocked);
                    break;
                default:
                {
                    // Half products accumulate in float and round once at the end.
                    var fa = ToFloats(ac.ContiguousSpan<Half>().Slice((int)(offA * m * k), (int)(m * k)));
                    var fb = ToFloats(bc.ContiguousSpan<Half>().Slice((int)(offB * k * n), (int)(k * n)));
                    var fc = new float[m * n];
                    Gemm<float>(fa, fb, fc, (int)m, (int)k, (int)n, blocked);
                    var dst = result.ContiguousSpan<Half>().Slice((int)(bi * m * n), (int)(m * n));
                    for (int i = 0; i < fc.Length; i++) dst[i] = (Half)fc[i];
                    break;
                }
            }
            for (int d = batch.Length - 1; d >= 0; d--)
            {
                if (++idx[d] < batch[d]) break;
                idx[d] = 0;
            }
        }
        return result;
    }

    /// <summary>For an operand's batch dimensions aligned to the broadcast batch rank: the distance, in whole matrices, between neighbours (0 where it broadcasts).</summary>
    private static long[] MatrixStrides(long[] own, int rank)
    {
        var s = new long[rank];
        long acc = 1;
        for (int i = own.Length - 1; i >= 0; i--)
        {
            s[rank - own.Length + i] = own[i] == 1 ? 0 : acc;
            acc *= own[i];
        }
        return s;
    }

    private static float[] ToFloats(ReadOnlySpan<Half> h)
    {
        var f = new float[h.Length];
        for (int i = 0; i < f.Length; i++) f[i] = (float)h[i];
        return f;
    }

    /// <summary>
    /// C = A (m x k) times B (k x n), row-major. The blocked kernel keeps every output element's sum in ascending-k order with
    /// separate multiply and add, so it is bit-identical to <see cref="GemmReference{T}"/>.
    /// </summary>
    public static void Gemm<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> c, int m, int k, int n, bool blocked) where T : unmanaged, IFloatingPointIeee754<T>
    {
        if (!blocked || !Vector.IsHardwareAccelerated) { GemmReference(a, b, c, m, k, n); return; }
        const int MB = 64, KB = 128;
        int v = Vector<T>.Count;
        c.Clear();
        for (int i0 = 0; i0 < m; i0 += MB)
        for (int k0 = 0; k0 < k; k0 += KB)
        {
            int i1 = Math.Min(i0 + MB, m), k1 = Math.Min(k0 + KB, k);
            for (int i = i0; i < i1; i++)
            {
                var crow = c.Slice(i * n, n);
                for (int kk = k0; kk < k1; kk++)
                {
                    T aik = a[i * k + kk];
                    var brow = b.Slice(kk * n, n);
                    var va = new Vector<T>(aik);
                    int j = 0;
                    for (; j <= n - v; j += v)
                        (new Vector<T>(crow.Slice(j, v)) + va * new Vector<T>(brow.Slice(j, v))).CopyTo(crow.Slice(j, v));
                    for (; j < n; j++) crow[j] += aik * brow[j];
                }
            }
        }
    }

    /// <summary>The naive triple loop: the definition the blocked kernel is tested against.</summary>
    public static void GemmReference<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> c, int m, int k, int n) where T : unmanaged, IFloatingPointIeee754<T>
    {
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++)
            {
                T acc = T.Zero;
                for (int kk = 0; kk < k; kk++) acc += a[i * k + kk] * b[kk * n + j];
                c[i * n + j] = acc;
            }
    }

    // ---- linear solve ----

    private const double Eps = 2.220446049250313e-16;

    /// <summary>
    /// Solves <c>A x = b</c>: LU with partial pivoting for a square matrix, Householder QR least squares when there are more
    /// equations than unknowns, and the minimum-norm solution when there are fewer. A singular or rank-deficient matrix raises a <see cref="TensorMathError"/>.
    /// </summary>
    public static Tensor Solve(Tensor A, Tensor B)
    {
        if (A.Elem != B.Elem) throw new TensorError($"Tried to solve a '{A.ElemName}' matrix against a '{B.ElemName}' right-hand side; the element types must match.");
        if (A.Rank != 2 || B.Rank is not (1 or 2))
            throw new TensorError($"Tried to solve '{A.ShapeText}' \\ '{B.ShapeText}'. The left side must be a matrix and the right a vector or matrix.");
        int m = (int)A.Shape[0], n = (int)A.Shape[1];
        if (B.Shape[0] != m)
            throw new TensorError($"Tried to solve '{A.ShapeText}' \\ '{B.ShapeText}'. The matrix has {m} rows but the right-hand side has {B.Shape[0]}.",
                "A \\ b needs one equation (row of A) per entry of b.");
        int k = B.Rank == 1 ? 1 : (int)B.Shape[1];
        var a = A.ToDoubles();
        var rhs = B.ToDoubles();
        double[] x = m == n ? SolveLU(a, rhs, n, k) : m > n ? LeastSquares(a, rhs, m, n, k) : MinimumNorm(a, rhs, m, n, k);
        return Tensor.FromDoubles(A.Elem, B.Rank == 1 ? [n] : [n, k], x);
    }

    private static TensorMathError Singular(string why) => new(
        $"The matrix is singular ({why}), so A \\ b has no unique answer. Dividing by that pivot would have ended in tears.");

    private static double[] SolveLU(double[] a, double[] b, int n, int k)
    {
        double maxAbs = 0;
        foreach (var v in a) maxAbs = Math.Max(maxAbs, Math.Abs(v));
        double tol = n * Eps * maxAbs;
        for (int col = 0; col < n; col++)
        {
            int p = col;
            double best = Math.Abs(a[col * n + col]);
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(a[r * n + col]) > best) { best = Math.Abs(a[r * n + col]); p = r; }
            if (!(best > tol)) throw Singular("its rows are linearly dependent");
            if (p != col)
            {
                for (int c = 0; c < n; c++) (a[col * n + c], a[p * n + c]) = (a[p * n + c], a[col * n + c]);
                for (int j = 0; j < k; j++) (b[col * k + j], b[p * k + j]) = (b[p * k + j], b[col * k + j]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r * n + col] / a[col * n + col];
                if (f == 0) continue;
                for (int c = col + 1; c < n; c++) a[r * n + c] -= f * a[col * n + c];
                for (int j = 0; j < k; j++) b[r * k + j] -= f * b[col * k + j];
            }
        }
        var x = new double[n * k];
        for (int i = n - 1; i >= 0; i--)
            for (int j = 0; j < k; j++)
            {
                double s = b[i * k + j];
                for (int c = i + 1; c < n; c++) s -= a[i * n + c] * x[c * k + j];
                x[i * k + j] = s / a[i * n + i];
            }
        return x;
    }

    /// <summary>Householder QR of an m x n matrix (m &gt;= n): the thin Q (m x n) and the upper-triangular R (n x n).</summary>
    private static void Qr(double[] a, int m, int n, out double[] q, out double[] r)
    {
        var w = (double[])a.Clone();
        var qf = new double[m * m];
        for (int i = 0; i < m; i++) qf[i * m + i] = 1;
        var v = new double[m];
        for (int j = 0; j < n; j++)
        {
            double norm = 0;
            for (int i = j; i < m; i++) norm += w[i * n + j] * w[i * n + j];
            norm = Math.Sqrt(norm);
            if (norm == 0) continue;
            double alpha = w[j * n + j] > 0 ? -norm : norm;
            double vn = 0;
            for (int i = j; i < m; i++) { v[i] = w[i * n + j] - (i == j ? alpha : 0); vn += v[i] * v[i]; }
            if (vn == 0) continue;
            for (int c = j; c < n; c++)
            {
                double s = 0;
                for (int i = j; i < m; i++) s += v[i] * w[i * n + c];
                double f = 2 * s / vn;
                for (int i = j; i < m; i++) w[i * n + c] -= f * v[i];
            }
            for (int row = 0; row < m; row++)
            {
                double s = 0;
                for (int i = j; i < m; i++) s += qf[row * m + i] * v[i];
                double f = 2 * s / vn;
                for (int i = j; i < m; i++) qf[row * m + i] -= f * v[i];
            }
        }
        q = new double[m * n];
        for (int i = 0; i < m; i++) for (int j = 0; j < n; j++) q[i * n + j] = qf[i * m + j];
        r = new double[n * n];
        for (int i = 0; i < n; i++) for (int j = i; j < n; j++) r[i * n + j] = w[i * n + j];
    }

    private static void CheckRank(double[] r, int n)
    {
        double maxDiag = 0;
        for (int i = 0; i < n; i++) maxDiag = Math.Max(maxDiag, Math.Abs(r[i * n + i]));
        double tol = n * Eps * maxDiag * 8;
        for (int i = 0; i < n; i++)
            if (!(Math.Abs(r[i * n + i]) > tol)) throw Singular("its columns are linearly dependent, so the least-squares fit is not unique");
    }

    private static double[] LeastSquares(double[] a, double[] b, int m, int n, int k)
    {
        Qr(a, m, n, out var q, out var r);
        CheckRank(r, n);
        var x = new double[n * k];
        for (int j = 0; j < k; j++)
            for (int i = n - 1; i >= 0; i--)
            {
                double s = 0;
                for (int row = 0; row < m; row++) s += q[row * n + i] * b[row * k + j]; // (Q^T b)_i
                for (int c = i + 1; c < n; c++) s -= r[i * n + c] * x[c * k + j];
                x[i * k + j] = s / r[i * n + i];
            }
        return x;
    }

    private static double[] MinimumNorm(double[] a, double[] b, int m, int n, int k)
    {
        // A is m x n with m < n. Factor A^T (n x m) = Q R; then x = Q (R^-T b).
        var at = new double[n * m];
        for (int i = 0; i < m; i++) for (int j = 0; j < n; j++) at[j * m + i] = a[i * n + j];
        Qr(at, n, m, out var q, out var r);
        CheckRank(r, m);
        var y = new double[m * k];
        for (int j = 0; j < k; j++)
            for (int i = 0; i < m; i++)
            {
                double s = b[i * k + j];
                for (int c = 0; c < i; c++) s -= r[c * m + i] * y[c * k + j];
                y[i * k + j] = s / r[i * m + i];
            }
        var x = new double[n * k];
        for (int row = 0; row < n; row++)
            for (int j = 0; j < k; j++)
            {
                double s = 0;
                for (int i = 0; i < m; i++) s += q[row * m + i] * y[i * k + j];
                x[row * k + j] = s;
            }
        return x;
    }
}
