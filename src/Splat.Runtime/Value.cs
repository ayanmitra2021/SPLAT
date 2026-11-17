using System.Globalization;
using Splat.Semantics;

namespace Splat.Runtime;

/// <summary>Runtime tag. The first sixteen values mirror <see cref="Prim"/>.</summary>
public enum VKind : byte
{
    I8, I16, I32, I64, U8, U16, U32, U64, Usz, Smol, Mid, Chonk, Bit, Txt, Char, Zip,
    Range, Func, Chain,
}

/// <summary>
/// A tagged value. Integers, floats, bits and chars live inline in <see cref="Bits"/> (no boxing); only <c>txt</c>,
/// ranges and closures use the reference slot. Signed integers are stored sign-extended, unsigned zero-extended,
/// floats as the IEEE bits of a double (already rounded to the value's own precision).
/// </summary>
public readonly struct Value
{
    public readonly VKind Kind;
    public readonly long Bits;
    public readonly object? Ref;

    private Value(VKind kind, long bits, object? obj) { Kind = kind; Bits = bits; Ref = obj; }

    public static readonly Value Unit = new(VKind.Zip, 0, null);
    public static Value Bool(bool b) => new(VKind.Bit, b ? 1 : 0, null);
    public static Value Int(VKind kind, long bits) => new(kind, bits, null);
    public static Value Char(int codePoint) => new(VKind.Char, codePoint, null);
    public static Value Str(string s) => new(VKind.Txt, 0, s);
    public static Value Range(Value lo, Value hi, bool inclusive) => new(VKind.Range, 0, new RangeData(lo, hi, inclusive, 1, false));
    public static Value Range(RangeData data) => new(VKind.Range, 0, data);
    public static Value Func(object closure) => new(VKind.Func, 0, closure);
    /// <summary>A <c>then</c> chain: its parts are iterated one after another.</summary>
    public static Value Chain(Value[] parts) => new(VKind.Chain, 0, parts);

    /// <summary>A float of the given kind, rounded to that kind's precision.</summary>
    public static Value Float(VKind kind, double d) => new(kind, BitConverter.DoubleToInt64Bits(Round(kind, d)), null);

    public static double Round(VKind kind, double d) => kind switch
    {
        VKind.Mid => (double)(float)d,
        VKind.Smol => (double)(Half)d,
        _ => d,
    };

    public bool AsBool => Bits != 0;
    public double AsDouble => BitConverter.Int64BitsToDouble(Bits);
    public string AsString => (string)Ref!;
    public RangeData AsRange => (RangeData)Ref!;
    public Value[] AsChain => (Value[])Ref!;

    public bool IsInt => Kind <= VKind.Usz;
    public bool IsUnsigned => Kind is >= VKind.U8 and <= VKind.Usz;
    public bool IsFloat => Kind is VKind.Smol or VKind.Mid or VKind.Chonk;

    /// <summary>An integer value as a 128-bit number (so every integer type, including u64, is exact).</summary>
    public Int128 AsInt128 => IsUnsigned ? (Int128)(ulong)Bits : (Int128)Bits;

    public static Value FromInt128(VKind kind, Int128 v) =>
        Int(kind, Kinds.IsUnsigned(kind) ? unchecked((long)(ulong)v) : (long)v);

    public string TypeName => Kind switch
    {
        VKind.Range => "range",
        VKind.Func => "cook",
        VKind.Chain => "chain",
        _ => Types.Of((Prim)(int)Kind).Name,
    };

    public override string ToString() => Formatter.Display(this);
}

/// <summary>
/// A range <c>lo..hi</c> / <c>lo..=hi</c>, visiting every <see cref="Step"/>-th integer from <c>lo</c>. A reversed range
/// (<c>rev(...)</c>) visits the same values from the last one back down to the first.
/// </summary>
public sealed record RangeData(Value Lo, Value Hi, bool Inclusive, Int128 Step, bool Reversed)
{
    /// <summary>How many values the range visits.</summary>
    public Int128 Count
    {
        get
        {
            Int128 span = Hi.AsInt128 - Lo.AsInt128;
            if (Inclusive) return span < 0 ? 0 : span / Step + 1;
            return span <= 0 ? 0 : (span - 1) / Step + 1;
        }
    }

    /// <summary>The values in visiting order. Lazy: a huge range costs nothing until it is walked.</summary>
    public IEnumerable<Value> Items()
    {
        var kind = Lo.Kind;
        Int128 lo = Lo.AsInt128, n = Count;
        for (Int128 k = 0; k < n; k++)
            yield return Value.FromInt128(kind, lo + (Reversed ? n - 1 - k : k) * Step);
    }

    /// <summary>True when <paramref name="x"/> is one of the values the range visits (on the step grid, within the bounds).</summary>
    public bool Contains(Int128 x)
    {
        Int128 lo = Lo.AsInt128, hi = Hi.AsInt128;
        if (x < lo || (Inclusive ? x > hi : x >= hi)) return false;
        return (x - lo) % Step == 0;
    }
}

internal static class Kinds
{
    public static Prim P(VKind k) => (Prim)(int)k;
    public static bool IsUnsigned(VKind k) => k is >= VKind.U8 and <= VKind.Usz;
    public static bool IsSigned(VKind k) => k <= VKind.I64;
    public static bool IsInt(VKind k) => k <= VKind.Usz;
    public static bool IsFloat(VKind k) => k is VKind.Smol or VKind.Mid or VKind.Chonk;
    public static string Name(VKind k) => Types.Of(P(k)).Name;
    public static VKind Of(SType t) => t is PrimType p ? (VKind)(int)p.Kind : VKind.Zip;
}

/// <summary>Turns values into text for <c>squish</c>, <c>txt(x)</c> and string templates.</summary>
public static class Formatter
{
    public static string Display(Value v) => v.Kind switch
    {
        VKind.Bit => v.AsBool ? "yep" : "nop",
        VKind.Txt => v.AsString,
        VKind.Char => char.ConvertFromUtf32((int)v.Bits),
        VKind.Zip => "()",
        VKind.Range => RangeText(v.AsRange),
        VKind.Func => "<cook>",
        VKind.Chain => string.Join(" then ", v.AsChain.Select(Display)),
        _ when v.IsInt => v.AsInt128.ToString(CultureInfo.InvariantCulture),
        _ => FloatText(v.Kind, v.AsDouble),
    };

    private static string RangeText(RangeData r)
    {
        var text = $"{Display(r.Lo)}{(r.Inclusive ? "..=" : "..")}{Display(r.Hi)}";
        if (r.Step != 1) text += $" by {r.Step}";
        return r.Reversed ? $"rev({text})" : text;
    }

    /// <summary>How the REPL echoes a value: like <see cref="Display"/>, but text and chars show their quotes.</summary>
    public static string Repr(Value v) => v.Kind switch
    {
        VKind.Txt => "\"" + v.AsString.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("{", "\\{") + "\"",
        VKind.Char => "c\"" + Display(v).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"",
        _ => Display(v),
    };

    private static string FloatText(VKind kind, double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        string s = kind switch
        {
            VKind.Mid => ((float)d).ToString("R", CultureInfo.InvariantCulture),
            VKind.Smol => ((Half)d).ToString(CultureInfo.InvariantCulture),
            _ => d.ToString("R", CultureInfo.InvariantCulture),
        };
        // 3 prints as "3.0" so a float never looks like an int.
        return s.Any(c => c is '.' or 'E' or 'e') ? s : s + ".0";
    }

    public static string Format(Value v, FormatSpec spec)
    {
        string body;
        if (v.IsFloat && spec.Precision is int prec)
        {
            double d = v.AsDouble;
            body = double.IsNaN(d) || double.IsInfinity(d) ? FloatText(v.Kind, d) : d.ToString("F" + prec, CultureInfo.InvariantCulture);
        }
        else if (v.IsInt && spec.Radix is char r)
        {
            var n = v.AsInt128;
            bool neg = n < 0;
            var mag = (UInt128)(neg ? -n : n);
            string digits = r switch
            {
                'x' => ToBase(mag, 16, false),
                'X' => ToBase(mag, 16, true),
                'b' => ToBase(mag, 2, false),
                _ => ToBase(mag, 8, false),
            };
            body = (neg ? "-" : "") + digits;
        }
        else body = Display(v);

        if (body.Length >= spec.Width) return body;
        int pad = spec.Width - body.Length;
        bool numeric = v.IsInt || v.IsFloat;

        if (spec.ZeroPad && numeric)
        {
            bool neg = body.StartsWith('-');
            return (neg ? "-" : "") + new string('0', pad) + (neg ? body[1..] : body);
        }

        char align = spec.Align ?? (numeric ? '>' : '<');
        return align switch
        {
            '>' => new string(' ', pad) + body,
            '^' => new string(' ', pad / 2) + body + new string(' ', pad - pad / 2),
            _ => body + new string(' ', pad),
        };
    }

    private static string ToBase(UInt128 n, int b, bool upper)
    {
        if (n == 0) return "0";
        var chars = new List<char>();
        while (n > 0)
        {
            int d = (int)(n % (UInt128)b);
            chars.Add((char)(d < 10 ? '0' + d : (upper ? 'A' : 'a') + d - 10));
            n /= (UInt128)b;
        }
        chars.Reverse();
        return new string(chars.ToArray());
    }
}
