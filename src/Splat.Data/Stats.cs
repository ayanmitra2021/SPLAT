using System.Numerics;

namespace Splat.Data;

/// <summary>
/// Span-based statistical kernels. Tensor reductions in the runtime call these, and the columnar kernels of Phase 19 reuse
/// them, so there is exactly one implementation of each. All accumulation is done in <see cref="double"/>.
/// </summary>
public static class Stats
{
    /// <summary>
    /// The sum of the elements by pairwise (cascade) summation: blocks of 128 are added sequentially and block sums are
    /// combined like a binary counter. The result is deterministic and has O(log n) error growth instead of O(n).
    /// </summary>
    public static double Sum<T>(ReadOnlySpan<T> values) where T : IFloatingPointIeee754<T>
    {
        var acc = new PairwiseSum();
        foreach (var v in values) acc.Add(double.CreateChecked(v));
        return acc.Total;
    }

    /// <summary>Arithmetic mean; NaN for an empty span.</summary>
    public static double Mean<T>(ReadOnlySpan<T> values) where T : IFloatingPointIeee754<T> =>
        values.Length == 0 ? double.NaN : Sum(values) / values.Length;

    /// <summary>
    /// Variance by Welford's one-pass algorithm (stable for data with a large mean). <paramref name="ddof"/> is subtracted from
    /// the count: 0 gives the population variance, 1 the sample variance. NaN when there are not more than <paramref name="ddof"/> values.
    /// </summary>
    public static double Variance<T>(ReadOnlySpan<T> values, int ddof = 0) where T : IFloatingPointIeee754<T>
    {
        long n = 0;
        double mean = 0, m2 = 0;
        foreach (var v in values)
        {
            double x = double.CreateChecked(v);
            n++;
            double d = x - mean;
            mean += d / n;
            m2 += d * (x - mean);
        }
        return n - ddof <= 0 ? double.NaN : m2 / (n - ddof);
    }

    /// <summary>Standard deviation (the square root of <see cref="Variance{T}"/>).</summary>
    public static double StdDev<T>(ReadOnlySpan<T> values, int ddof = 0) where T : IFloatingPointIeee754<T> =>
        Math.Sqrt(Variance(values, ddof));

    /// <summary>
    /// The <paramref name="q"/>-quantile (0 to 1) with linear interpolation between the two nearest ranks (the NumPy default).
    /// NaN for an empty span, or when any element is NaN.
    /// </summary>
    public static double Quantile<T>(ReadOnlySpan<T> values, double q) where T : IFloatingPointIeee754<T>
    {
        if (values.Length == 0 || double.IsNaN(q) || q < 0 || q > 1) return double.NaN;
        var sorted = new double[values.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            sorted[i] = double.CreateChecked(values[i]);
            if (double.IsNaN(sorted[i])) return double.NaN;
        }
        Array.Sort(sorted);
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>Index of the first largest element; the first NaN wins if there is one; -1 for an empty span.</summary>
    public static int ArgMax<T>(ReadOnlySpan<T> values) where T : IFloatingPointIeee754<T> => ArgBest(values, max: true);

    /// <summary>Index of the first smallest element; the first NaN wins if there is one; -1 for an empty span.</summary>
    public static int ArgMin<T>(ReadOnlySpan<T> values) where T : IFloatingPointIeee754<T> => ArgBest(values, max: false);

    private static int ArgBest<T>(ReadOnlySpan<T> values, bool max) where T : IFloatingPointIeee754<T>
    {
        if (values.Length == 0) return -1;
        int best = 0;
        if (T.IsNaN(values[0])) return 0;
        for (int i = 1; i < values.Length; i++)
        {
            var v = values[i];
            if (T.IsNaN(v)) return i;
            if (max ? v > values[best] : v < values[best]) best = i;
        }
        return best;
    }

    /// <summary>Largest element (NaN if any element is NaN); NaN for an empty span.</summary>
    public static double Max<T>(ReadOnlySpan<T> values) where T : IFloatingPointIeee754<T> =>
        values.Length == 0 ? double.NaN : double.CreateChecked(values[ArgMax(values)]);

    /// <summary>Smallest element (NaN if any element is NaN); NaN for an empty span.</summary>
    public static double Min<T>(ReadOnlySpan<T> values) where T : IFloatingPointIeee754<T> =>
        values.Length == 0 ? double.NaN : double.CreateChecked(values[ArgMin(values)]);
}

/// <summary>
/// Incremental pairwise summation. Feeding the values one at a time gives exactly the same answer as <see cref="Stats.Sum{T}"/>,
/// so a loop that produces its terms lazily (a <c>∑</c> over a huge range) agrees with a sum over a materialised tensor.
/// </summary>
public struct PairwiseSum
{
    private const int Block = 128;
    private double[]? _stack;      // partial sums; _stack[k] covers 2^k blocks
    private long _blocks;          // completed blocks
    private double _current;       // sequential sum of the block in progress
    private int _inBlock;

    public void Add(double x)
    {
        _current += x;
        if (++_inBlock == Block) Flush();
    }

    private void Flush()
    {
        _stack ??= new double[64];
        double s = _current;
        _current = 0;
        _inBlock = 0;
        long n = _blocks++;
        int level = 0;
        while ((n & 1) == 1)
        {
            s = _stack[level] + s;
            n >>= 1;
            level++;
        }
        _stack[level] = s;
    }

    /// <summary>The sum of everything added so far.</summary>
    public readonly double Total
    {
        get
        {
            double t = _current;
            if (_stack is null) return t;
            // Combine the completed blocks from the smallest level up, then the partial block.
            long n = _blocks;
            for (int level = 0; n != 0; level++, n >>= 1)
                if ((n & 1) == 1) t = _stack[level] + t;
            return t;
        }
    }
}
