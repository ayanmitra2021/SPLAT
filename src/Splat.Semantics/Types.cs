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
            default:
                return actual.Equals(expected);
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
        _ => t,
    };

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
