using Splat.Data;

namespace Splat.Runtime;

/// <summary>The integrand returned NaN or an infinity at a point.</summary>
internal sealed class IntegrandFault(double[] at, double value) : Exception($"the integrand returned {value} at ({string.Join(", ", at)})")
{
    public double[] At { get; } = at;
    public double Value { get; } = value;
}

/// <summary>The adaptive quadrature gave up before reaching the tolerance (a divergent or wildly oscillating integrand).</summary>
internal sealed class IntegralDidNotConverge(string message) : Exception(message);

/// <summary>
/// Numerical integration for <c>∫[a..b, ...] |x, ...| body</c>. One dimension: global-adaptive Gauss-Kronrod (G7K15). Two and three
/// dimensions: the same rule nested. Above three: a quasi-Monte Carlo estimate over a Halton sequence. Everything is computed in
/// <see cref="double"/>; the integrand is a callback into the interpreter.
/// </summary>
internal static class Integrator
{
    // QUADPACK QK15 nodes and weights.
    private static readonly double[] Xgk =
    [
        0.991455371120812639206854697526329, 0.949107912342758524526189684047851, 0.864864423359769072789712788640926,
        0.741531185599394439863864773280788, 0.586087235467691130294144838258730, 0.405845151377397166906606412076961,
        0.207784955007898467600689403773245, 0.0,
    ];
    private static readonly double[] Wgk =
    [
        0.022935322010529224963732008058970, 0.063092092629978553290700663189204, 0.104790010322250183839876322541518,
        0.140653259715525918745189590510238, 0.169004726639267902826583426598550, 0.190350578064785409913256402421014,
        0.204432940075298892414161999234649, 0.209482141084727828012999174891714,
    ];
    private static readonly double[] Wg =
    [
        0.129484966168869693270611432679082, 0.279705391489276667901467771423780,
        0.381830050505118944950369775488975, 0.417959183673469387755102040816327,
    ];

    public const int MaxSubdivisions = 600;

    private readonly record struct Piece(double A, double B, double Result, double Error);

    /// <summary>One G7K15 panel: the Kronrod estimate and an error estimate (QUADPACK's scaling, floored by round-off).</summary>
    private static Piece Panel(Func<double, double> f, double a, double b, double epsilon)
    {
        double c = 0.5 * (a + b), h = 0.5 * (b - a);
        double fc = f(c);
        double resg = fc * Wg[3], resk = fc * Wgk[7], resabs = Math.Abs(resk);
        var fv1 = new double[7];
        var fv2 = new double[7];
        for (int j = 0; j < 3; j++)
        {
            int jt = 2 * j + 1;
            double dx = h * Xgk[jt];
            double f1 = f(c - dx), f2 = f(c + dx);
            fv1[jt] = f1; fv2[jt] = f2;
            resg += Wg[j] * (f1 + f2);
            resk += Wgk[jt] * (f1 + f2);
            resabs += Wgk[jt] * (Math.Abs(f1) + Math.Abs(f2));
        }
        for (int j = 0; j < 4; j++)
        {
            int jt = 2 * j;
            double dx = h * Xgk[jt];
            double f1 = f(c - dx), f2 = f(c + dx);
            fv1[jt] = f1; fv2[jt] = f2;
            resk += Wgk[jt] * (f1 + f2);
            resabs += Wgk[jt] * (Math.Abs(f1) + Math.Abs(f2));
        }
        double reskh = resk * 0.5;
        double resasc = Wgk[7] * Math.Abs(fc - reskh);
        for (int j = 0; j < 7; j++) resasc += Wgk[j] * (Math.Abs(fv1[j] - reskh) + Math.Abs(fv2[j] - reskh));
        resasc *= Math.Abs(h);
        resabs *= Math.Abs(h);
        double result = resk * h;
        double err = Math.Abs((resk - resg) * h);
        if (resasc != 0 && err != 0) err = resasc * Math.Min(1, Math.Pow(200 * err / resasc, 1.5));
        if (resabs > double.Epsilon / (50 * epsilon)) err = Math.Max(epsilon * 50 * resabs, err);
        return new Piece(a, b, result, err);
    }

    /// <summary>Adaptive G7K15 over [a, b]: bisect the interval with the largest error until the total error is within the tolerance.</summary>
    public static double Adaptive1D(Func<double, double> f, double a, double b, double tol, double epsilon)
    {
        if (a == b) return 0;
        double sign = 1;
        if (a > b) { (a, b) = (b, a); sign = -1; }
        // Never ask for more accuracy than the integrand's own precision can deliver.
        tol = Math.Max(tol, 100 * epsilon);
        var pieces = new List<Piece> { Panel(f, a, b, epsilon) };
        double total = pieces[0].Result, err = pieces[0].Error;
        while (err > Math.Max(tol, tol * Math.Abs(total)))
        {
            if (pieces.Count >= MaxSubdivisions)
                throw new IntegralDidNotConverge($"the estimate {total:R} was still off by about {err:E1} after {pieces.Count} subdivisions");
            int worst = 0;
            for (int i = 1; i < pieces.Count; i++) if (pieces[i].Error > pieces[worst].Error) worst = i;
            var p = pieces[worst];
            double mid = 0.5 * (p.A + p.B);
            if (mid <= p.A || mid >= p.B) throw new IntegralDidNotConverge("an interval shrank to nothing without converging");
            var left = Panel(f, p.A, mid, epsilon);
            var right = Panel(f, mid, p.B, epsilon);
            pieces[worst] = left;
            pieces.Add(right);
            total = 0; err = 0;
            foreach (var q in pieces) { total += q.Result; err += q.Error; }
        }
        return sign * total;
    }

    /// <summary>Integrates over a box. <paramref name="f"/> receives the point (do not keep the array).</summary>
    public static double Integrate(Func<double[], double> f, double[] lo, double[] hi, double tol, double epsilon, int samples)
    {
        int n = lo.Length;
        var x = new double[n];
        double Checked(double[] pt)
        {
            double v = f(pt);
            if (!double.IsFinite(v)) throw new IntegrandFault((double[])pt.Clone(), v);
            return v;
        }
        if (n <= 3)
        {
            double Level(int d)
            {
                if (d == n) return Checked(x);
                return Adaptive1D(t => { x[d] = t; return Level(d + 1); }, lo[d], hi[d], d == 0 ? tol : tol * 0.1, epsilon);
            }
            return Level(0);
        }
        return QuasiMonteCarlo(Checked, lo, hi, samples);
    }

    private static readonly int[] Primes = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71];

    /// <summary>The radical inverse of <paramref name="i"/> in <paramref name="b"/> (the i-th Halton coordinate).</summary>
    public static double Halton(long i, int b)
    {
        double f = 1, r = 0;
        while (i > 0)
        {
            f /= b;
            r += f * (i % b);
            i /= b;
        }
        return r;
    }

    private static double QuasiMonteCarlo(Func<double[], double> f, double[] lo, double[] hi, int samples)
    {
        int n = lo.Length;
        if (n > Primes.Length) throw new IntegralDidNotConverge($"{n} dimensions is more than the {Primes.Length} this Halton sequence supports");
        double volume = 1;
        for (int d = 0; d < n; d++) volume *= hi[d] - lo[d];
        var x = new double[n];
        var acc = new PairwiseSum();
        for (long i = 1; i <= samples; i++)
        {
            for (int d = 0; d < n; d++) x[d] = lo[d] + (hi[d] - lo[d]) * Halton(i, Primes[d]);
            acc.Add(f(x));
        }
        return volume * acc.Total / samples;
    }
}
