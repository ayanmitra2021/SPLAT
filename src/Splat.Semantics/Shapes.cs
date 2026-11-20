using Splat.Syntax;

namespace Splat.Semantics;

// Phases 12-13: tensor shapes and the shape unifier (spec 4.2). A shape is a list of dimensions; the RANK is always static.

/// <summary>One tensor dimension as the checker sees it: a constant, a <c>$Name</c> variable, or dynamic (<c>?</c>).</summary>
public abstract class SDim : IEquatable<SDim>
{
    /// <summary>The source form: <c>3</c>, <c>$B</c> or <c>?</c>.</summary>
    public abstract string Text { get; }
    public override string ToString() => Text;
    public abstract bool Equals(SDim? other);
    public override bool Equals(object? obj) => obj is SDim d && Equals(d);
    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);

    public static readonly SDim Dyn = new DynSDim();
    public static SDim Const(long n) => new ConstSDim(n);
    public static SDim Var(string name) => new VarSDim(name);
}

public sealed class ConstSDim(long n) : SDim
{
    public long N { get; } = n;
    public override string Text => N.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public override bool Equals(SDim? other) => other is ConstSDim c && c.N == N;
}

/// <summary>A <c>$Name</c> dimension. Inside a generic function it is an unknown-but-fixed length (rigid); at a call site it is replaced by an <see cref="InferSDim"/>.</summary>
public sealed class VarSDim(string name) : SDim
{
    public string Name { get; } = name;
    public override string Text => "$" + Name;
    public override bool Equals(SDim? other) => other is VarSDim v && v.Name == Name;
}

public sealed class DynSDim : SDim
{
    internal DynSDim() { }
    public override string Text => "?";
    public override bool Equals(SDim? other) => other is DynSDim;
}

/// <summary>The per-call-site stand-in for one <c>$Name</c> of a generic signature (shape monomorphisation).</summary>
public sealed class InferSDim(string name) : SDim
{
    public string Name { get; } = name;
    public SDim? Bound { get; set; }
    public override string Text => Bound?.Text ?? "$" + Name;
    public override bool Equals(SDim? other) => ReferenceEquals(this, other) || (Bound is not null && Bound.Equals(other));
}

/// <summary>Carries a dimension through <see cref="Types.Substitute"/>, whose map is keyed by type-parameter name.</summary>
public sealed class DimBindingSType(SDim dim) : SType
{
    public SDim Dim { get; } = dim;
    public override string Name => Dim.Text;
    public override bool Equals(SType? other) => other is DimBindingSType d && d.Dim.Equals(Dim);
}

/// <summary>A tensor type <c>f32[3, $K, ?]</c>: an element type and one dimension per axis.</summary>
public sealed class TensorSType : SType
{
    public SType Elem { get; }
    public IReadOnlyList<SDim> Dims { get; }

    public TensorSType(SType elem, IReadOnlyList<SDim> dims) { Elem = elem; Dims = dims; }

    public int Rank => Dims.Count;
    public override string Name => $"{Elem.Name}[{string.Join(", ", Dims.Select(d => Shapes.R(d).Text))}]";

    public override bool Equals(SType? other) =>
        other is TensorSType t && t.Elem.Equals(Elem) && t.Dims.Count == Dims.Count
        && Dims.Zip(t.Dims).All(p => Shapes.R(p.First).Equals(Shapes.R(p.Second)));

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    public TensorSType WithDims(IReadOnlyList<SDim> dims) => new(Elem, dims);
    public TensorSType WithElem(SType elem) => new(elem, Dims);

    /// <summary>True when every dimension is a known constant.</summary>
    public bool IsStatic => Dims.All(d => Shapes.R(d) is ConstSDim);

    /// <summary>The element count when the shape is static.</summary>
    public long? StaticCount
    {
        get
        {
            long n = 1;
            foreach (var d in Dims)
            {
                if (Shapes.R(d) is not ConstSDim c) return null;
                n = checked(n * c.N);
            }
            return n;
        }
    }

    /// <summary>The "base name" extend blocks and the runtime key a tensor receiver by: element type and rank only, no dimensions.</summary>
    public string BaseKey => Elem.Name + "[" + new string(',', Math.Max(0, Rank - 1)) + "]";
}

/// <summary>The shape unifier: dimension relations, NumPy broadcasting and the matrix-product rule.</summary>
public static class Shapes
{
    /// <summary>Follows bound inference dimensions.</summary>
    public static SDim R(SDim d)
    {
        while (d is InferSDim { Bound: { } b }) d = b;
        return d;
    }

    /// <summary>True for <c>?</c> and for an inference dimension nothing has bound yet.</summary>
    public static bool IsDyn(SDim d) => R(d) is DynSDim or InferSDim;

    public static bool IsOne(SDim d) => R(d) is ConstSDim { N: 1 };

    /// <summary>The outcome of relating two dimensions.</summary>
    public enum Rel
    {
        /// <summary>Provably equal.</summary>
        Same,
        /// <summary>Not provable now (one side is dynamic); a runtime check is inserted.</summary>
        Runtime,
        /// <summary>Provably different (or two different rigid variables).</summary>
        Conflict,
    }

    /// <summary>
    /// Unifies two dimensions. Const = Const must be equal; a <c>$Var</c> from a generic signature (an inference dimension at a
    /// call site) binds to a constant, another variable or <c>?</c>; two rigid variables are equal only if they are the same
    /// name; <c>?</c> unifies with anything but needs a runtime check.
    /// </summary>
    public static Rel Unify(SDim a, SDim b, bool bind = true)
    {
        a = R(a); b = R(b);
        if (a is InferSDim ia) { if (bind && !ReferenceEquals(a, b)) ia.Bound = b; return b is DynSDim or InferSDim ? Rel.Runtime : Rel.Same; }
        if (b is InferSDim ib) { if (bind) ib.Bound = a; return a is DynSDim ? Rel.Runtime : Rel.Same; }
        if (a is DynSDim || b is DynSDim) return Rel.Runtime;
        return a.Equals(b) ? Rel.Same : Rel.Conflict;
    }

    /// <summary>
    /// Assignability of tensor types (no inference-dimension binding): element types equal, ranks equal, and every dimension pair
    /// either provably equal or involving <c>?</c> (checked at run time).
    /// </summary>
    public static bool Compatible(TensorSType actual, TensorSType expected)
    {
        if (!actual.Elem.Equals(expected.Elem) || actual.Rank != expected.Rank) return false;
        for (int i = 0; i < actual.Rank; i++)
            if (Unify(actual.Dims[i], expected.Dims[i], bind: false) == Rel.Conflict) return false;
        return true;
    }

    /// <summary>Call-site unification: like <see cref="Compatible"/> but binds the callee's inference dimensions.</summary>
    public static bool UnifyTypes(TensorSType expected, TensorSType actual)
    {
        if (!expected.Elem.Equals(actual.Elem) || expected.Rank != actual.Rank) return false;
        bool ok = true;
        for (int i = 0; i < expected.Rank; i++)
            if (Unify(expected.Dims[i], actual.Dims[i]) == Rel.Conflict) ok = false;
        return ok;
    }

    /// <summary>
    /// True when a value of <paramref name="actual"/> could meet <paramref name="expected"/> only through a run-time dimension check
    /// (some dimension of the actual is <c>?</c> where the expected one is known).
    /// </summary>
    public static bool NeedsGuard(TensorSType actual, TensorSType expected)
    {
        if (actual.Rank != expected.Rank) return false;
        for (int i = 0; i < actual.Rank; i++)
            if (IsDyn(actual.Dims[i]) && !IsDyn(expected.Dims[i])) return true;
        return false;
    }

    /// <summary>Replaces every <c>$Var</c> with its binding (unbound ones become <c>?</c>), recursively through inference dimensions.</summary>
    public static TensorSType Resolve(TensorSType t) =>
        t.WithDims(t.Dims.Select(d => R(d) is InferSDim ? SDim.Dyn : R(d)).ToList());

    /// <summary>The names of every <c>$Var</c> dimension in a type, in first-use order.</summary>
    public static void CollectVars(SType t, ICollection<string> into)
    {
        switch (t)
        {
            case TensorSType ts:
                foreach (var d in ts.Dims) if (R(d) is VarSDim v && !into.Contains(v.Name)) into.Add(v.Name);
                break;
            case ListSType l: CollectVars(l.Elem, into); break;
            case SetSType s: CollectVars(s.Elem, into); break;
            case MapSType m: CollectVars(m.Key, into); CollectVars(m.Val, into); break;
            case TupleSType tu: foreach (var i in tu.Items) CollectVars(i, into); break;
            case FuncSType f: foreach (var p in f.Params) CollectVars(p, into); CollectVars(f.Ret, into); break;
            case EnumInstanceSType e: foreach (var a in e.Args) CollectVars(a, into); break;
            case ClassSType c: foreach (var a in c.Args) CollectVars(a, into); break;
        }
    }

    // ---- broadcasting ----

    /// <summary>
    /// NumPy trailing-dimension broadcasting: shapes are aligned on the right; two dimensions are compatible when they are equal or
    /// one of them is the constant 1. Returns false on a definite conflict and reports the two dimensions (and their axis counted
    /// from the right, starting at 1) that cannot meet.
    /// </summary>
    public static bool TryBroadcast(IReadOnlyList<SDim> a, IReadOnlyList<SDim> b, out List<SDim> result, out SDim? badA, out SDim? badB, out int fromRight)
    {
        int n = Math.Max(a.Count, b.Count);
        var dims = new SDim[n];
        badA = badB = null;
        fromRight = 0;
        for (int k = 0; k < n; k++)
        {
            SDim? x = k < a.Count ? a[a.Count - 1 - k] : null;
            SDim? y = k < b.Count ? b[b.Count - 1 - k] : null;
            SDim r;
            if (x is null) r = y!;
            else if (y is null) r = x;
            else if (IsOne(x)) r = y;
            else if (IsOne(y)) r = x;
            else
            {
                var rel = Unify(x, y, bind: false);
                if (rel == Rel.Conflict) { badA = x; badB = y; fromRight = k + 1; result = []; return false; }
                // Same, or a dynamic side: prefer the one we know.
                r = IsDyn(x) ? y : x;
            }
            dims[n - 1 - k] = R(r);
        }
        result = [.. dims];
        return true;
    }

    // ---- matrix product ----

    public sealed record MatMulResult(TensorSType? Type, bool ScalarResult, SDim? K1, SDim? K2, string? Problem);

    /// <summary>
    /// The shape rule for <c>A @ B</c> (element types are checked by the caller):
    /// <c>[M,K] @ [K,N]</c> is <c>[M,N]</c>; <c>[M,K] @ [K]</c> is <c>[M]</c>; <c>[K] @ [K,N]</c> is <c>[N]</c>; <c>[K] @ [K]</c> is a scalar;
    /// with more than two axes the leading ones are batch axes that broadcast like <c>.+</c> (<c>[B,M,K] @ [K,N]</c> is <c>[B,M,N]</c>).
    /// </summary>
    public static MatMulResult MatMul(TensorSType a, TensorSType b)
    {
        int ra = a.Rank, rb = b.Rank;
        if (ra == 0 || rb == 0) return new(null, false, null, null, "a matrix product needs tensors on both sides");

        SDim ka = a.Dims[^1];
        SDim kb = rb == 1 ? b.Dims[0] : b.Dims[^2];
        if (Unify(ka, kb, bind: false) == Rel.Conflict) return new(null, false, ka, kb, null);

        if (ra == 1 && rb == 1) return new(null, true, ka, kb, null);

        var batchA = ra > 2 ? a.Dims.Take(ra - 2).ToList() : [];
        var batchB = rb > 2 ? b.Dims.Take(rb - 2).ToList() : [];
        if (!TryBroadcast(batchA, batchB, out var batch, out var ba, out var bb, out _))
            return new(null, false, ba, bb, $"the batch dimensions {ba!.Text} and {bb!.Text} cannot broadcast");

        var dims = new List<SDim>(batch);
        if (ra >= 2) dims.Add(a.Dims[^2]);
        if (rb >= 2) dims.Add(b.Dims[^1]);
        return new(new TensorSType(a.Elem, dims), false, ka, kb, null);
    }

    /// <summary>The type of <c>A'</c>: the last two axes swapped (a vector is its own transpose).</summary>
    public static TensorSType Transpose(TensorSType t)
    {
        if (t.Rank < 2) return t;
        var dims = t.Dims.ToList();
        (dims[^1], dims[^2]) = (dims[^2], dims[^1]);
        return t.WithDims(dims);
    }
}
