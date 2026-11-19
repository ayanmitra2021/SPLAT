using System.Text;

namespace Splat.Semantics;

/// <summary>A normalised pattern: what the exhaustiveness algorithm works on.</summary>
internal abstract record Pat;

internal sealed record PWild : Pat
{
    public static readonly PWild I = new();
}

/// <summary>A constructor with sub-patterns: an enum variant, a tuple (<c>"(,)"</c>), a bool (<c>true</c>/<c>false</c>) or <c>()</c>.</summary>
internal sealed record PCtor(string Key, IReadOnlyList<Pat> Args) : Pat;

/// <summary>An inclusive integer or char interval (a literal is a one-point interval).</summary>
internal sealed record PRange(Int128 Lo, Int128 Hi, bool IsChar = false) : Pat;

/// <summary>A literal of a type with no finite constructor set (txt, floats): it never helps exhaustiveness.</summary>
internal sealed record PLit(string Key) : Pat;

/// <summary>
/// The classic pattern-matrix algorithm (Maranget, "Warnings for pattern matching"): <see cref="Useful"/> says whether a
/// pattern vector matches something no earlier row matches (redundancy and exhaustiveness are both instances of it), and
/// <see cref="Witnesses"/> builds example values nothing covers. Integer and char columns are split into segments at every
/// boundary that occurs in the column, so ranges such as <c>0</c>, <c>1..=9</c> and <c>_</c> are handled exactly.
/// </summary>
internal static class PatternMatrix
{
    private sealed record Ctor(string Key, IReadOnlyList<SType> ArgTypes, Int128? Lo = null, Int128? Hi = null, bool IsChar = false);

    private const int CharMax = 0x10FFFF;

    // ---- column signatures ----

    /// <summary>The complete list of constructors of <paramref name="t"/>, or null when the type has no finite signature.</summary>
    private static List<Ctor>? Signature(SType t, IEnumerable<Pat> heads)
    {
        switch (t)
        {
            case EnumInstanceSType mi:
                return mi.Base.Variants.Select(v => new Ctor(v.Name, v.Payloads.Select(mi.Substitute).ToList())).ToList();
            case TupleSType tu:
                return [new Ctor("(,)", tu.Items)];
            case PrimType { Kind: Prim.Bool }:
                return [new Ctor("true", []), new Ctor("false", [])];
            case PrimType { Kind: Prim.Unit }:
                return [new Ctor("()", [])];
            case PrimType { Kind: var k } when Types.IsInt(k) || k == Prim.Char:
            {
                bool isChar = k == Prim.Char;
                Int128 lo = isChar ? 0 : Types.Min(k), hi = isChar ? CharMax : Types.Max(k);
                var cuts = new SortedSet<Int128> { lo, hi + 1 };
                foreach (var h in heads)
                {
                    if (h is not PRange r) continue;
                    if (r.Lo > lo && r.Lo <= hi) cuts.Add(r.Lo);
                    if (r.Hi + 1 > lo && r.Hi + 1 <= hi) cuts.Add(r.Hi + 1);
                }
                var list = cuts.ToList();
                var segs = new List<Ctor>();
                for (int i = 0; i + 1 < list.Count; i++)
                    segs.Add(new Ctor($"{list[i]}..{list[i + 1] - 1}", [], list[i], list[i + 1] - 1, isChar));
                return segs;
            }
            default:
                return null;
        }
    }

    private static bool Covers(Pat head, Ctor c) => head switch
    {
        PWild => true,
        PCtor pc => pc.Key == c.Key,
        PRange r => c.Lo is { } lo && c.Hi is { } hi && r.Lo <= lo && hi <= r.Hi,
        _ => false,
    };

    private static IReadOnlyList<Pat> ArgsOf(Pat head, Ctor c) =>
        head is PCtor pc ? pc.Args : Enumerable.Repeat<Pat>(PWild.I, c.ArgTypes.Count).ToList();

    private static Pat[] Concat(IEnumerable<Pat> a, IEnumerable<Pat> b) => a.Concat(b).ToArray();
    private static SType[] Concat(IEnumerable<SType> a, IEnumerable<SType> b) => a.Concat(b).ToArray();

    private static List<Pat[]> Specialize(List<Pat[]> rows, Ctor c) =>
        rows.Where(r => Covers(r[0], c)).Select(r => Concat(ArgsOf(r[0], c), r.Skip(1))).ToList();

    private static List<Pat[]> DefaultRows(List<Pat[]> rows) =>
        rows.Where(r => r[0] is PWild).Select(r => r.Skip(1).ToArray()).ToList();

    // ---- usefulness ----

    /// <summary>True when some value matches <paramref name="q"/> and no row of <paramref name="rows"/>.</summary>
    public static bool Useful(List<Pat[]> rows, Pat[] q, SType[] tys)
    {
        if (q.Length == 0) return rows.Count == 0;
        var h = q[0];
        var t = tys[0];
        var heads = rows.Select(r => r[0]).ToList();

        if (h is PLit lit)
        {
            var spec = rows.Where(r => r[0] is PWild || (r[0] is PLit l2 && l2.Key == lit.Key)).Select(r => r.Skip(1).ToArray()).ToList();
            return Useful(spec, q.Skip(1).ToArray(), tys.Skip(1).ToArray());
        }

        if (h is PWild)
        {
            var sig = Signature(t, heads);
            if (sig is not null && sig.Count > 0 && sig.All(c => heads.Any(hd => hd is not PWild && Covers(hd, c))))
                return sig.Any(c => UsefulCtor(rows, q, tys, c));
            return Useful(DefaultRows(rows), q.Skip(1).ToArray(), tys.Skip(1).ToArray());
        }

        var sig2 = Signature(t, heads.Append(h));
        if (sig2 is null)
        {
            // No finite signature (e.g. a variant pattern against an unknown type): compare by key only.
            var key = ((PCtor)h).Key;
            var pc = (PCtor)h;
            var anys = Enumerable.Repeat<SType>(SType.Any, pc.Args.Count).ToArray();
            var spec = rows.Where(r => r[0] is PWild || (r[0] is PCtor rc && rc.Key == key))
                .Select(r => Concat(r[0] is PCtor rc2 ? rc2.Args : Enumerable.Repeat<Pat>(PWild.I, pc.Args.Count), r.Skip(1))).ToList();
            return Useful(spec, Concat(pc.Args, q.Skip(1)), Concat(anys, tys.Skip(1)));
        }

        if (h is PCtor pctor)
        {
            var c = sig2.FirstOrDefault(x => x.Key == pctor.Key);
            return c is null || UsefulCtor(rows, q, tys, c);
        }

        var range = (PRange)h;
        return sig2.Where(c => Covers(range, c)).Any(c => UsefulCtor(rows, q, tys, c));
    }

    private static bool UsefulCtor(List<Pat[]> rows, Pat[] q, SType[] tys, Ctor c)
    {
        var spec = Specialize(rows, c);
        var qArgs = ArgsOf(q[0], c);
        return Useful(spec, Concat(qArgs, q.Skip(1)), Concat(c.ArgTypes, tys.Skip(1)));
    }

    // ---- witnesses ----

    /// <summary>Up to <paramref name="cap"/> pattern vectors that match values no row matches (empty when the rows are exhaustive).</summary>
    public static List<Pat[]> Witnesses(List<Pat[]> rows, SType[] tys, int cap)
    {
        if (tys.Length == 0) return rows.Count == 0 ? [[]] : [];

        var heads = rows.Select(r => r[0]).ToList();
        var sig = Signature(tys[0], heads);
        var result = new List<Pat[]>();

        if (sig is not null && sig.Count > 0)
        {
            var missing = sig.Where(c => !heads.Any(hd => hd is not PWild && Covers(hd, c))).ToList();
            if (missing.Count == 0)
            {
                foreach (var c in sig)
                {
                    var inner = Witnesses(Specialize(rows, c), Concat(c.ArgTypes, tys.Skip(1)), cap);
                    foreach (var w in inner)
                    {
                        var head = Rebuild(c, w.Take(c.ArgTypes.Count).ToList());
                        result.Add(Concat([head], w.Skip(c.ArgTypes.Count)));
                        if (result.Count >= cap) return result;
                    }
                }
                return result;
            }

            var rest = Witnesses(DefaultRows(rows), tys.Skip(1).ToArray(), cap);
            if (rest.Count == 0) return result;
            foreach (var m in MergeSegments(missing))
            {
                foreach (var w in rest)
                {
                    result.Add(Concat([m], w));
                    if (result.Count >= cap) return result;
                }
            }
            return result;
        }

        foreach (var w in Witnesses(DefaultRows(rows), tys.Skip(1).ToArray(), cap))
        {
            result.Add(Concat([PWild.I], w));
            if (result.Count >= cap) break;
        }
        return result;
    }

    private static Pat Rebuild(Ctor c, List<Pat> args) =>
        c.Lo is { } lo && c.Hi is { } hi ? new PRange(lo, hi, c.IsChar) : new PCtor(c.Key, args);

    /// <summary>One witness pattern per missing constructor; adjacent integer segments are joined into one range.</summary>
    private static List<Pat> MergeSegments(List<Ctor> missing)
    {
        var outList = new List<Pat>();
        PRange? run = null;
        foreach (var c in missing)
        {
            if (c.Lo is { } lo && c.Hi is { } hi)
            {
                if (run is not null && run.Hi + 1 == lo) run = run with { Hi = hi };
                else { if (run is not null) outList.Add(run); run = new PRange(lo, hi, c.IsChar); }
            }
            else
            {
                if (run is not null) { outList.Add(run); run = null; }
                outList.Add(new PCtor(c.Key, Enumerable.Repeat<Pat>(PWild.I, c.ArgTypes.Count).ToList()));
            }
        }
        if (run is not null) outList.Add(run);
        return outList;
    }

    // ---- display ----

    public static string Show(Pat p)
    {
        switch (p)
        {
            case PWild: return "_";
            case PLit l: return l.Key;
            case PRange r:
                return r.Lo == r.Hi ? ShowPoint(r.Lo, r.IsChar) : $"{ShowPoint(r.Lo, r.IsChar)}..={ShowPoint(r.Hi, r.IsChar)}";
            case PCtor c when c.Key == "(,)":
                return "(" + string.Join(", ", c.Args.Select(Show)) + ")";
            case PCtor c when c.Args.Count == 0:
                return c.Key;
            case PCtor c:
            {
                var sb = new StringBuilder(c.Key).Append('(');
                sb.Append(string.Join(", ", c.Args.Select(Show)));
                return sb.Append(')').ToString();
            }
            default: return "?";
        }
    }

    private static string ShowPoint(Int128 v, bool isChar) =>
        isChar && v >= 32 && v < 127 ? $"c\"{(char)(int)v}\"" : v.ToString();
}
