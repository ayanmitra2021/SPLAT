using System.Globalization;
using Splat.Syntax;

namespace Splat.Semantics;

/// <summary>Primitive type kinds. Order matters: the runtime maps these one-to-one onto its value tags.</summary>
public enum Prim { I8, I16, I32, I64, U8, U16, U32, U64, Usz, Smol, Mid, Chonk, Bit, Txt, Char, Zip }

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
    /// <summary>Compatible with everything. Used for generic parameters until Phase 7 enforces them.</summary>
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
    public override string Name => $"cook({string.Join(", ", Params.Select(p => p.Name))})::{Ret.Name}";
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

/// <summary>The primitive type singletons and their numeric properties.</summary>
public static class Types
{
    public static readonly PrimType I8 = new(Prim.I8, "i8"), I16 = new(Prim.I16, "i16"), I32 = new(Prim.I32, "i32"), I64 = new(Prim.I64, "i64");
    public static readonly PrimType U8 = new(Prim.U8, "u8"), U16 = new(Prim.U16, "u16"), U32 = new(Prim.U32, "u32"), U64 = new(Prim.U64, "u64"), Usz = new(Prim.Usz, "usz");
    public static readonly PrimType Smol = new(Prim.Smol, "smol"), Mid = new(Prim.Mid, "mid"), Chonk = new(Prim.Chonk, "chonk");
    public static readonly PrimType Bit = new(Prim.Bit, "bit"), Txt = new(Prim.Txt, "txt"), Char = new(Prim.Char, "char"), Zip = new(Prim.Zip, "zip");

    /// <summary>Type names usable in annotations and as conversion functions (<c>mid(x)</c>).</summary>
    public static readonly IReadOnlyDictionary<string, PrimType> ByName = new[]
    {
        I8, I16, I32, I64, U8, U16, U32, U64, Usz, Smol, Mid, Chonk, Bit, Txt, Char, Zip,
    }.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static PrimType Of(Prim p) => p switch
    {
        Prim.I8 => I8, Prim.I16 => I16, Prim.I32 => I32, Prim.I64 => I64,
        Prim.U8 => U8, Prim.U16 => U16, Prim.U32 => U32, Prim.U64 => U64, Prim.Usz => Usz,
        Prim.Smol => Smol, Prim.Mid => Mid, Prim.Chonk => Chonk,
        Prim.Bit => Bit, Prim.Txt => Txt, Prim.Char => Char, _ => Zip,
    };

    public static bool IsInt(Prim p) => p <= Prim.Usz;
    public static bool IsSigned(Prim p) => p <= Prim.I64;
    public static bool IsUnsigned(Prim p) => p >= Prim.U8 && p <= Prim.Usz;
    public static bool IsFloat(Prim p) => p is Prim.Smol or Prim.Mid or Prim.Chonk;
    public static bool IsNumeric(Prim p) => p <= Prim.Chonk;

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
            default:
                return actual.Equals(expected);
        }
    }

    public static string Describe(Int128 v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What the checker learned, consumed by the interpreter.</summary>
public sealed class TypeInfo
{
    private readonly Dictionary<Expr, SType> _types = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CookDecl, FuncSType> _cooks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BinaryExpr, CallExpr> _pipes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CallExpr, int[]> _argMaps = new(ReferenceEqualityComparer.Instance);

    internal void Set(Expr e, SType t) => _types[e] = t;
    internal void SetCook(CookDecl d, FuncSType t) => _cooks[d] = t;
    internal void SetPipe(BinaryExpr b, CallExpr call) => _pipes[b] = call;
    internal void SetArgMap(CallExpr c, int[] map) => _argMaps[c] = map;

    /// <summary>The checked type of an expression, or <c>null</c> if it was never checked.</summary>
    public SType? TypeOf(Expr e) => _types.GetValueOrDefault(e);

    /// <summary>The checked signature of a <c>cook</c> declaration.</summary>
    public FuncSType? CookType(CookDecl d) => _cooks.GetValueOrDefault(d);

    /// <summary>The call a <c>|&gt;</c> expression desugars to (<c>x |&gt; f(a)</c> is <c>f(x, a)</c>).</summary>
    public CallExpr? PipeCall(BinaryExpr b) => _pipes.GetValueOrDefault(b);

    /// <summary>For a call with named arguments: the parameter index each argument binds to.</summary>
    public int[]? ArgMap(CallExpr c) => _argMaps.GetValueOrDefault(c);
}
