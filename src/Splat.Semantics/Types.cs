using System.Globalization;
using Splat.Syntax;

namespace Splat.Semantics;

/// <summary>Primitive type kinds. Order matters: the runtime maps these one-to-one onto its value tags.</summary>
public enum Prim { I8, I16, I32, I64, U8, U16, U32, U64, Usize, F16, F32, F64, Bool, Txt, Char, Unit }

/// <summary>A static type as the checker sees it.</summary>
public abstract class SType : IEquatable<SType>
{
    public abstract string Name { get; }
    public override string ToString() => Name;
    public abstract bool Equals(SType? other);
    public override bool Equals(object? obj) => obj is SType t && Equals(t);
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    /// <summary>The "do not cascade" type left behind after an error was already reported.</summary>
    public static readonly SType Error = new ErrorType();
    /// <summary>Compatible with everything. Used where the checker has no information (an unbound inference variable, an empty collection literal given a type hint).</summary>
    public static readonly SType Any = new AnyType();

    public bool IsError => ReferenceEquals(this, Error);
    public bool IsAny => ReferenceEquals(this, Any);

    private sealed class ErrorType : SType
    {
        public override string Name => "<error>";
        public override bool Equals(SType? other) => ReferenceEquals(this, other);
    }

    private sealed class AnyType : SType
    {
        public override string Name => "any";
        public override bool Equals(SType? other) => ReferenceEquals(this, other);
    }
}

public sealed class PrimType : SType
{
    public Prim Kind { get; }
    private readonly string _name;
    internal PrimType(Prim kind, string name) { Kind = kind; _name = name; }
    public override string Name => _name;
    public override bool Equals(SType? other) => other is PrimType p && p.Kind == Kind;
    public override int GetHashCode() => (int)Kind;
}

public sealed class FuncSType : SType
{
    public IReadOnlyList<SType> Params { get; }
    public SType Ret { get; }
    public FuncSType(IReadOnlyList<SType> parameters, SType ret) { Params = parameters; Ret = ret; }
    public override string Name => $"fn({string.Join(", ", Params.Select(p => p.Name))})::{Ret.Name}";
    public override bool Equals(SType? other) =>
        other is FuncSType f && Ret.Equals(f.Ret) && Params.Count == f.Params.Count && Params.Zip(f.Params).All(p => p.First.Equals(p.Second));
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

public sealed class RangeSType : SType
{
    public SType Elem { get; }
    public RangeSType(SType elem) => Elem = elem;
    /// <summary>True for an unstepped range of <c>Time</c>/<c>Stamp</c>, which has no natural step and cannot be iterated.</summary>
    public bool NeedsStep { get; init; }
    public override string Name => $"range<{Elem.Name}>";
    public override bool Equals(SType? other) => other is RangeSType r && r.Elem.Equals(Elem);
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A <c>then</c> chain of iterables that all produce <see cref="Elem"/>.</summary>
public sealed class ChainSType : SType
{
    public SType Elem { get; }
    public ChainSType(SType elem) => Elem = elem;
    public override string Name => $"chain<{Elem.Name}>";
    public override bool Equals(SType? other) => other is ChainSType r && r.Elem.Equals(Elem);
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

// ---- Phase 7: collections, tuples, enums ----

public sealed class ListSType : SType
{
    public SType Elem { get; }
    public ListSType(SType elem) => Elem = elem;
    public override string Name => $"List<{Elem.Name}>";
    public override bool Equals(SType? other) => other is ListSType l && l.Elem.Equals(Elem);
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

public sealed class SetSType : SType
{
    public SType Elem { get; }
    public SetSType(SType elem) => Elem = elem;
    public override string Name => $"Set<{Elem.Name}>";
    public override bool Equals(SType? other) => other is SetSType s && s.Elem.Equals(Elem);
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

public sealed class MapSType : SType
{
    public SType Key { get; }
    public SType Val { get; }
    public MapSType(SType key, SType val) { Key = key; Val = val; }
    public override string Name => $"Map<{Key.Name}, {Val.Name}>";
    public override bool Equals(SType? other) => other is MapSType m && m.Key.Equals(Key) && m.Val.Equals(Val);
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

public sealed class TupleSType : SType
{
    public IReadOnlyList<SType> Items { get; }
    public TupleSType(IReadOnlyList<SType> items) => Items = items;
    public override string Name => $"({string.Join(", ", Items.Select(i => i.Name))})";
    public override bool Equals(SType? other) => other is TupleSType t && t.Items.Count == Items.Count && t.Items.Zip(Items).All(p => p.First.Equals(p.Second));
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

/// <summary>One variant inside a enum type (name + resolved payload types, possibly with TypeParamSType placeholders).</summary>
public sealed class EnumVariantInfo
{
    public string Name { get; }
    public IReadOnlyList<SType> Payloads { get; }
    public EnumVariantInfo(string name, IReadOnlyList<SType> payloads) { Name = name; Payloads = payloads; }
}

/// <summary>A generic enum definition such as <c>Maybe&lt;T&gt;</c>. Registered in the type checker; not used directly as a value type.</summary>
public sealed class EnumSType : SType
{
    public string EnumName { get; }
    public IReadOnlyList<string> TypeParams { get; }
    public IReadOnlyList<EnumVariantInfo> Variants { get; }
    /// <summary>The source line of the declaration (0 for a built-in enum); used by "already declared" messages.</summary>
    public int DeclaredLine { get; }
    public EnumSType(string name, IReadOnlyList<string> typeParams, IReadOnlyList<EnumVariantInfo> variants, int declaredLine = 0)
    { EnumName = name; TypeParams = typeParams; Variants = variants; DeclaredLine = declaredLine; }
    public override string Name => TypeParams.Count == 0 ? EnumName : $"{EnumName}<{string.Join(", ", TypeParams)}>";
    public override bool Equals(SType? other) => other is EnumSType m && m.EnumName == EnumName;
    public override int GetHashCode() => EnumName.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A enum type with specific type arguments, e.g. <c>Maybe&lt;i32&gt;</c>.</summary>
public sealed class EnumInstanceSType : SType
{
    public EnumSType Base { get; }
    public IReadOnlyList<SType> Args { get; }
    public EnumInstanceSType(EnumSType @base, IReadOnlyList<SType> args) { Base = @base; Args = args; }
    public override string Name => Args.Count == 0 ? Base.EnumName : $"{Base.EnumName}<{string.Join(", ", Args.Select(a => a.Name))}>";
    public override bool Equals(SType? other) => other is EnumInstanceSType mi && mi.Base.EnumName == Base.EnumName && mi.Args.Count == Args.Count && mi.Args.Zip(Args).All(p => p.First.Equals(p.Second));
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    /// <summary>Applies the type arguments to substitute all TypeParamSType in a template type.</summary>
    public SType Substitute(SType template)
    {
        if (Base.TypeParams.Count == 0 || Args.Count == 0) return template;
        var map = Base.TypeParams.Zip(Args).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
        return Types.Substitute(template, map);
    }
}

/// <summary>
/// An inference variable: stands for one generic parameter at one call site while the checker unifies argument types
/// with the callee's parameter types. Unbound variables resolve to <c>any</c>.
/// </summary>
public sealed class InferVarSType : SType
{
    public string ParamName { get; }
    public SType? Bound { get; set; }
    public InferVarSType(string name) => ParamName = name;
    public override string Name => Bound?.Name ?? ParamName;
    public override bool Equals(SType? other) => ReferenceEquals(this, other) || (Bound is not null && Bound.Equals(other));
    public override int GetHashCode() => Bound?.GetHashCode() ?? ParamName.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A type parameter placeholder, e.g. <c>T</c> inside a generic enum definition.</summary>
public sealed class TypeParamSType : SType
{
    public string ParamName { get; }
    public TypeParamSType(string name) => ParamName = name;
    /// <summary>The trait this parameter was declared with (<c>T: Talkable</c>), or null. Not part of equality.</summary>
    public TraitSType? Bound { get; init; }
    public override string Name => ParamName;
    public override bool Equals(SType? other) => other is TypeParamSType tp && tp.ParamName == ParamName;
    public override int GetHashCode() => ParamName.GetHashCode(StringComparison.Ordinal);
}

/// <summary>The primitive type singletons and their numeric properties.</summary>
public static class Types
{
    public static readonly PrimType I8 = new(Prim.I8, "i8"), I16 = new(Prim.I16, "i16"), I32 = new(Prim.I32, "i32"), I64 = new(Prim.I64, "i64");
    public static readonly PrimType U8 = new(Prim.U8, "u8"), U16 = new(Prim.U16, "u16"), U32 = new(Prim.U32, "u32"), U64 = new(Prim.U64, "u64"), Usize = new(Prim.Usize, "usize");
    public static readonly PrimType F16 = new(Prim.F16, "f16"), F32 = new(Prim.F32, "f32"), F64 = new(Prim.F64, "f64");
    public static readonly PrimType Bool = new(Prim.Bool, "bool"), Txt = new(Prim.Txt, "txt"), Char = new(Prim.Char, "char"), Unit = new(Prim.Unit, "unit");

    // Phases 8-11: built-in native types.
    public static readonly NativeSType FileT = new("File"), SocketT = new("Socket"), ServerT = new("Server"), JsonT = new("Json");
    public static readonly NativeSType DateT = new("Date"), TimeT = new("Time"), StampT = new("Stamp"), DurT = new("Dur"), CalT = new("Cal");

    public static readonly IReadOnlyDictionary<string, NativeSType> NativeByName = new[]
    {
        FileT, SocketT, ServerT, JsonT, DateT, TimeT, StampT, DurT, CalT,
    }.ToDictionary(t => t.Name, StringComparer.Ordinal);

    // Phase 12 (CRUMPLE groundwork, spec 4.1 and 13.0.3): element-only types. They exist only inside tensor types such as `bf16[3, 4]`
    // or `u4[64]`; a scalar of one of these types lives in an f32 / u8 / i8. `bf16` is a SEPARATE type from `f16` (it accumulates in f32).
    public static readonly NativeSType Bf16T = new("bf16"), U1T = new("u1"), U2T = new("u2"), U4T = new("u4"), I4T = new("i4");

    public static readonly IReadOnlyDictionary<string, NativeSType> TensorOnlyElems = new[] { Bf16T, U1T, U2T, U4T, I4T }
        .ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>Element types whose tensors the runtime can store and compute with today (f16, f32, f64).</summary>
    public static bool IsRuntimeTensorElem(SType t) => t is PrimType { Kind: Prim.F16 or Prim.F32 or Prim.F64 };

    public static bool IsTemporalPoint(SType t) => ReferenceEquals(t, DateT) || ReferenceEquals(t, TimeT) || ReferenceEquals(t, StampT);
    public static bool IsTemporal(SType t) => IsTemporalPoint(t) || ReferenceEquals(t, DurT) || ReferenceEquals(t, CalT);

    /// <summary>Type names usable in annotations and as conversion functions (<c>f32(x)</c>).</summary>
    public static readonly IReadOnlyDictionary<string, PrimType> ByName = new[]
    {
        I8, I16, I32, I64, U8, U16, U32, U64, Usize, F16, F32, F64, Bool, Txt, Char, Unit,
    }.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static PrimType Of(Prim p) => p switch
    {
        Prim.I8 => I8, Prim.I16 => I16, Prim.I32 => I32, Prim.I64 => I64,
        Prim.U8 => U8, Prim.U16 => U16, Prim.U32 => U32, Prim.U64 => U64, Prim.Usize => Usize,
        Prim.F16 => F16, Prim.F32 => F32, Prim.F64 => F64,
        Prim.Bool => Bool, Prim.Txt => Txt, Prim.Char => Char, _ => Unit,
    };

    public static bool IsInt(Prim p) => p <= Prim.Usize;
    public static bool IsSigned(Prim p) => p <= Prim.I64;
    public static bool IsUnsigned(Prim p) => p >= Prim.U8 && p <= Prim.Usize;
    public static bool IsFloat(Prim p) => p is Prim.F16 or Prim.F32 or Prim.F64;
    public static bool IsNumeric(Prim p) => p <= Prim.F64;

    public static bool IsInt(SType t) => t is PrimType p && IsInt(p.Kind);
    public static bool IsFloat(SType t) => t is PrimType p && IsFloat(p.Kind);
    public static bool IsNumeric(SType t) => t is PrimType p && IsNumeric(p.Kind);
    public static bool IsSigned(SType t) => t is PrimType p && IsSigned(p.Kind);

    public static int Bits(Prim p) => p switch
    {
        Prim.I8 or Prim.U8 => 8,
        Prim.I16 or Prim.U16 => 16,
        Prim.I32 or Prim.U32 => 32,
        _ => 64,
    };

    public static Int128 Min(Prim p) => IsSigned(p) ? -((Int128)1 << (Bits(p) - 1)) : 0;
    public static Int128 Max(Prim p) => IsSigned(p) ? ((Int128)1 << (Bits(p) - 1)) - 1 : ((Int128)1 << Bits(p)) - 1;

    /// <summary>True when <paramref name="actual"/> may be used where <paramref name="expected"/> is required.</summary>
    public static bool Compatible(SType actual, SType expected)
    {
        if (actual is InferVarSType av) { if (av.Bound is null) return true; actual = av.Bound; }
        if (expected is InferVarSType ev) { if (ev.Bound is null) return true; expected = ev.Bound; }
        if (actual.IsError || expected.IsError || actual.IsAny || expected.IsAny) return true;
        switch (actual, expected)
        {
            case (FuncSType a, FuncSType e):
                return a.Params.Count == e.Params.Count
                    && Compatible(a.Ret, e.Ret)
                    && a.Params.Zip(e.Params).All(p => Compatible(p.First, p.Second));
            case (RangeSType a, RangeSType e):
                return Compatible(a.Elem, e.Elem);
            case (ChainSType a, ChainSType e):
                return Compatible(a.Elem, e.Elem);
            case (ListSType a, ListSType e):
                return Compatible(a.Elem, e.Elem);
            case (SetSType a, SetSType e):
                return Compatible(a.Elem, e.Elem);
            case (MapSType a, MapSType e):
                return Compatible(a.Key, e.Key) && Compatible(a.Val, e.Val);
            case (TupleSType a, TupleSType e):
                return a.Items.Count == e.Items.Count && a.Items.Zip(e.Items).All(p => Compatible(p.First, p.Second));
            case (EnumInstanceSType a, EnumInstanceSType e):
                return a.Base.EnumName == e.Base.EnumName && a.Args.Count == e.Args.Count
                    && a.Args.Zip(e.Args).All(p => Compatible(p.First, p.Second));
            case (EnumSType a, EnumSType e):
                return a.EnumName == e.EnumName;
            case (ClassSType a, ClassSType e):
                foreach (var up in a.Chain())
                    if (ReferenceEquals(up.Info, e.Info))
                        return up.Args.Count == e.Args.Count && up.Args.Zip(e.Args).All(p => Compatible(p.First, p.Second));
                return false;
            case (_, TraitSType te):
                return ImplementsTrait(actual, te);
            case (IterSType a, IterSType e):
                return Compatible(a.Elem, e.Elem);
            case (TensorSType a, TensorSType e):
                return Shapes.Compatible(a, e);
            default:
                return actual.Equals(expected);
        }
    }

    // ---- traits and iteration ----

    /// <summary>
    /// True when <paramref name="actual"/> satisfies the trait: a class that lists it (or inherits it), a parameter bounded
    /// by it, a type an <c>extend ... with</c> block gave it, or (for <c>Iterable&lt;T&gt;</c>) any built-in iterable.
    /// </summary>
    public static bool ImplementsTrait(SType actual, TraitSType expected)
    {
        if (actual.IsError || actual.IsAny) return true;
        bool ArgsOk(IReadOnlyList<SType> given) =>
            given.Count == expected.Args.Count && given.Zip(expected.Args).All(p => Compatible(p.First, p.Second));
        switch (actual)
        {
            case TraitSType t:
                return ReferenceEquals(t.Info, expected.Info) && ArgsOk(t.Args);
            case ClassSType c:
                foreach (var up in c.Chain())
                    foreach (var tr in up.Info.Traits)
                        if (ReferenceEquals(tr.Info, expected.Info) && ArgsOk(((TraitSType)up.Substitute(tr)).Args)) return true;
                break;
            case TypeParamSType { Bound: { } b }:
                if (ReferenceEquals(b.Info, expected.Info) && ArgsOk(b.Args)) return true;
                break;
        }
        if (expected.Info.IsBuiltinIterable)
            return ElementOf(actual) is { } e && expected.Args.Count == 1 && Compatible(e, expected.Args[0]);
        foreach (var impl in expected.Info.Impls)
            if (Equals(impl, actual) || Compatible(actual, impl)) return true;
        return false;
    }

    /// <summary>
    /// The one notion of "iterable" in the checker: the element type a value yields in <c>loop</c>, <c>then</c> chains and
    /// <c>in</c>, or null when the type cannot be iterated. User classes join through <c>trait Iterable&lt;T&gt;</c>.
    /// </summary>
    public static SType? ElementOf(SType t)
    {
        switch (t)
        {
            case RangeSType r: return r.Elem;
            case ChainSType c: return c.Elem;
            case PrimType { Kind: Prim.Txt }: return Char;
            case ListSType l: return l.Elem;
            case SetSType s: return s.Elem;
            case MapSType m: return new TupleSType([m.Key, m.Val]);
            case IterSType i: return i.Elem;
            // Milestone 3: a tensor yields its rows (axis 0) as views; a vector yields scalars.
            case TensorSType { Rank: 1 } v: return v.Elem;
            case TensorSType { Rank: > 1 } m: return m.WithDims(m.Dims.Skip(1).ToList());
            case ClassSType c:
                foreach (var up in c.Chain())
                    foreach (var tr in up.Info.Traits)
                        if (tr.Info.IsBuiltinIterable && ((TraitSType)up.Substitute(tr)).Args is [var e]) return e;
                return null;
            case TypeParamSType { Bound: { Info.IsBuiltinIterable: true } b }: return b.Args.Count == 1 ? b.Args[0] : null;
            case TraitSType { Info.IsBuiltinIterable: true } ts: return ts.Args.Count == 1 ? ts.Args[0] : null;
            default: return null;
        }
    }

    /// <summary>Replaces generic parameters (by name) throughout a type.</summary>
    public static SType Substitute(SType t, IReadOnlyDictionary<string, SType> map) => t switch
    {
        TypeParamSType tp when map.TryGetValue(tp.ParamName, out var sub) => sub,
        ListSType l => new ListSType(Substitute(l.Elem, map)),
        SetSType s => new SetSType(Substitute(s.Elem, map)),
        MapSType m => new MapSType(Substitute(m.Key, map), Substitute(m.Val, map)),
        TupleSType tu => new TupleSType(tu.Items.Select(i => Substitute(i, map)).ToList()),
        RangeSType r => new RangeSType(Substitute(r.Elem, map)),
        ChainSType c => new ChainSType(Substitute(c.Elem, map)),
        EnumInstanceSType mi => new EnumInstanceSType(mi.Base, mi.Args.Select(a => Substitute(a, map)).ToList()),
        FuncSType f => new FuncSType(f.Params.Select(p => Substitute(p, map)).ToList(), Substitute(f.Ret, map)),
        ClassSType c when c.Args.Count > 0 => new ClassSType(c.Info, c.Args.Select(a => Substitute(a, map)).ToList()),
        TraitSType tr when tr.Args.Count > 0 => new TraitSType(tr.Info, tr.Args.Select(a => Substitute(a, map)).ToList()),
        IterSType it => new IterSType(Substitute(it.Elem, map)),
        TensorSType ts => new TensorSType(Substitute(ts.Elem, map), ts.Dims.Select(d => SubstituteDim(d, map)).ToList()),
        _ => t,
    };

    /// <summary>A <c>$Name</c> dimension is replaced when the map holds a <see cref="DimBindingSType"/> under its name.</summary>
    private static SDim SubstituteDim(SDim d, IReadOnlyDictionary<string, SType> map) =>
        Shapes.R(d) is VarSDim v && map.TryGetValue(v.Name, out var s) && s is DimBindingSType b ? b.Dim : d;

    /// <summary>Follows bound inference variables; unbound ones become <c>any</c>.</summary>
    public static SType Resolve(SType t) => t switch
    {
        InferVarSType v => v.Bound is null ? SType.Any : Resolve(v.Bound),
        ListSType l => new ListSType(Resolve(l.Elem)),
        SetSType s => new SetSType(Resolve(s.Elem)),
        MapSType m => new MapSType(Resolve(m.Key), Resolve(m.Val)),
        TupleSType tu => new TupleSType(tu.Items.Select(Resolve).ToList()),
        RangeSType r => new RangeSType(Resolve(r.Elem)),
        ChainSType c => new ChainSType(Resolve(c.Elem)),
        EnumInstanceSType mi => new EnumInstanceSType(mi.Base, mi.Args.Select(Resolve).ToList()),
        FuncSType f => new FuncSType(f.Params.Select(Resolve).ToList(), Resolve(f.Ret)),
        ClassSType c when c.Args.Count > 0 => new ClassSType(c.Info, c.Args.Select(Resolve).ToList()),
        TraitSType tr when tr.Args.Count > 0 => new TraitSType(tr.Info, tr.Args.Select(Resolve).ToList()),
        IterSType it => new IterSType(Resolve(it.Elem)),
        TensorSType ts => Shapes.Resolve(new TensorSType(Resolve(ts.Elem), ts.Dims)),
        _ => t,
    };

    /// <summary>
    /// Unifies a callee's parameter type (which may contain inference variables) with an argument type, binding unbound
    /// variables. Returns false on a definite mismatch; <c>any</c>/error on either side never fails.
    /// </summary>
    public static bool Unify(SType expected, SType actual)
    {
        if (expected is InferVarSType ev)
        {
            if (ev.Bound is not null) return Unify(ev.Bound, actual);
            if (!actual.IsAny && !actual.IsError) ev.Bound = actual;
            return true;
        }
        if (actual is InferVarSType av)
        {
            if (av.Bound is not null) return Unify(expected, av.Bound);
            if (!expected.IsAny && !expected.IsError) av.Bound = expected;
            return true;
        }
        if (actual.IsError || expected.IsError || actual.IsAny || expected.IsAny) return true;
        switch (expected, actual)
        {
            case (ListSType e, ListSType a): return Unify(e.Elem, a.Elem);
            case (SetSType e, SetSType a): return Unify(e.Elem, a.Elem);
            case (MapSType e, MapSType a): return Unify(e.Key, a.Key) & Unify(e.Val, a.Val);
            case (RangeSType e, RangeSType a): return Unify(e.Elem, a.Elem);
            case (ChainSType e, ChainSType a): return Unify(e.Elem, a.Elem);
            case (TupleSType e, TupleSType a):
            {
                if (e.Items.Count != a.Items.Count) return false;
                bool ok = true;
                for (int i = 0; i < e.Items.Count; i++) ok &= Unify(e.Items[i], a.Items[i]);
                return ok;
            }
            case (EnumInstanceSType e, EnumInstanceSType a):
            {
                if (e.Base.EnumName != a.Base.EnumName || e.Args.Count != a.Args.Count) return false;
                bool ok = true;
                for (int i = 0; i < e.Args.Count; i++) ok &= Unify(e.Args[i], a.Args[i]);
                return ok;
            }
            case (FuncSType e, FuncSType a):
            {
                if (e.Params.Count != a.Params.Count) return false;
                bool ok = Unify(e.Ret, a.Ret);
                for (int i = 0; i < e.Params.Count; i++) ok &= Unify(e.Params[i], a.Params[i]);
                return ok;
            }
            case (ClassSType e, ClassSType a):
            {
                // A subclass argument unifies through the ancestor the parameter names.
                foreach (var up in a.Chain())
                {
                    if (!ReferenceEquals(up.Info, e.Info)) continue;
                    if (up.Args.Count != e.Args.Count) return false;
                    bool ok = true;
                    for (int i = 0; i < e.Args.Count; i++) ok &= Unify(e.Args[i], up.Args[i]);
                    return ok;
                }
                return false;
            }
            case (TraitSType e, _) when actual is not TraitSType:
            {
                // Passing a class (or anything) where `Trait<T>` is expected: bind T from the trait it declares.
                if (actual is ClassSType ca)
                    foreach (var up in ca.Chain())
                        foreach (var tr in up.Info.Traits)
                            if (ReferenceEquals(tr.Info, e.Info))
                            {
                                var args = ((TraitSType)up.Substitute(tr)).Args;
                                for (int i = 0; i < e.Args.Count && i < args.Count; i++) Unify(e.Args[i], args[i]);
                            }
                if (e.Info.IsBuiltinIterable && ElementOf(actual) is { } elem && e.Args.Count == 1) Unify(e.Args[0], elem);
                return ImplementsTrait(Resolve(actual), (TraitSType)Resolve(e));
            }
            case (IterSType e, IterSType a): return Unify(e.Elem, a.Elem);
            case (TensorSType e, TensorSType a): return Shapes.UnifyTypes(e, a);
            default:
                return Compatible(actual, expected);
        }
    }

    /// <summary>Map keys and set elements: integers, char, txt, bool, and tuples of those (not floats, because of NaN).</summary>
    public static bool IsHashable(SType t) => t switch
    {
        PrimType p => p.Kind is not (Prim.F16 or Prim.F32 or Prim.F64 or Prim.Unit),
        TupleSType tu => tu.Items.All(IsHashable),
        TypeParamSType => true, // generic bodies are checked once, before any instantiation
        NativeSType n => ReferenceEquals(n, DateT) || ReferenceEquals(n, TimeT) || ReferenceEquals(n, StampT),
        _ => t.IsAny || t.IsError,
    };

    /// <summary>Types whose values can be compared with <c>==</c>.</summary>
    public static bool IsEquatable(SType t) => t switch
    {
        PrimType => true,
        TupleSType tu => tu.Items.All(IsEquatable),
        ListSType l => IsEquatable(l.Elem),
        SetSType s => IsEquatable(s.Elem),
        MapSType m => IsEquatable(m.Key) && IsEquatable(m.Val),
        EnumInstanceSType mi => mi.Args.All(IsEquatable),
        NativeSType n => IsTemporal(n),
        TensorSType => true, // whole-tensor equality: same shape and every element equal
        _ => t.IsAny || t.IsError,
    };

    public static string Describe(Int128 v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What the checker learned, consumed by the interpreter.</summary>
public sealed class TypeInfo
{
    private readonly Dictionary<Expr, SType> _types = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FnDecl, FuncSType> _cooks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BinaryExpr, CallExpr> _pipes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CallExpr, int[]> _argMaps = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CallExpr, IReadOnlyList<SType>> _typeArgs = new(ReferenceEqualityComparer.Instance);

    // Milestone 3: `f32[[1, 2], [3, 4]]` (an element-type name indexed by one list literal) is a tensor literal, and an
    // expression whose static shape had a `?` where the context needs a known size gets a run-time shape guard.
    private readonly Dictionary<IndexExpr, ListLiteral> _tensorLits = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr, TensorSType> _guards = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ReduceExpr, bool> _pureReductions = new(ReferenceEqualityComparer.Instance);

    internal void SetTensorLiteral(IndexExpr e, ListLiteral lit) => _tensorLits[e] = lit;
    internal void SetGuard(Expr e, TensorSType expected) => _guards[e] = expected;
    internal void SetPureReduction(ReduceExpr e, bool pure) => _pureReductions[e] = pure;

    /// <summary>For <c>f32[[1, 2], [3, 4]]</c>: the list literal that holds the elements.</summary>
    public ListLiteral? TensorLiteral(IndexExpr e) => _tensorLits.GetValueOrDefault(e);

    /// <summary>The shape an expression must have at run time (it was only known up to <c>?</c> at check time), or null.</summary>
    public TensorSType? Guard(Expr e) => _guards.Count == 0 ? null : _guards.GetValueOrDefault(e);

    /// <summary>True when any shape guards were recorded (lets the interpreter skip the lookup in the common case).</summary>
    public bool HasGuards => _guards.Count > 0;

    /// <summary>Whether the body of a <c>∑</c>/<c>∏</c> is provably pure (spec 13.6.1: Phase 30 lowers only pure bodies to a closed form).</summary>
    public bool IsPureReduction(ReduceExpr e) => _pureReductions.GetValueOrDefault(e);

    internal void Set(Expr e, SType t) => _types[e] = t;
    internal void SetFn(FnDecl d, FuncSType t) => _cooks[d] = t;
    internal void SetPipe(BinaryExpr b, CallExpr call) => _pipes[b] = call;
    internal void SetArgMap(CallExpr c, int[] map) => _argMaps[c] = map;
    internal void SetTypeArgs(CallExpr c, IReadOnlyList<SType> args) => _typeArgs[c] = args;

    /// <summary>The checked type of an expression, or <c>null</c> if it was never checked.</summary>
    public SType? TypeOf(Expr e) => _types.GetValueOrDefault(e);

    /// <summary>The checked signature of a <c>fn</c> declaration.</summary>
    public FuncSType? FnType(FnDecl d) => _cooks.GetValueOrDefault(d);

    /// <summary>The call a <c>|&gt;</c> expression desugars to (<c>x |&gt; f(a)</c> is <c>f(x, a)</c>).</summary>
    public CallExpr? PipeCall(BinaryExpr b) => _pipes.GetValueOrDefault(b);

    /// <summary>For a call to a generic function: the type argument inferred for each type parameter (the checked instantiation).</summary>
    public IReadOnlyList<SType>? TypeArgs(CallExpr c) => _typeArgs.GetValueOrDefault(c);

    /// <summary>For a call with named arguments: the parameter index each argument binds to.</summary>
    public int[]? ArgMap(CallExpr c) => _argMaps.GetValueOrDefault(c);
}
