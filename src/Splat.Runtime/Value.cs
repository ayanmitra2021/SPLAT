using System.Globalization;
using Splat.Core;
using Splat.Semantics;

namespace Splat.Runtime;

/// <summary>Runtime tag. The first sixteen values mirror <see cref="Prim"/>.</summary>
public enum VKind : byte
{
    I8, I16, I32, I64, U8, U16, U32, U64, Usize, F16, F32, F64, Bool, Txt, Char, Unit,
    Range, Func, Chain,
    // Phase 7: collections and ADTs
    List, Set, Map, Tuple, Enum,
    // Phases 8-10: class instances and native handles, and the date/time family (payload in Bits).
    Object, Date, Time, Stamp, Dur, Cal,
    // Milestone 3: a tensor (or a view of one); the payload is a <see cref="Tensor"/>.
    Tensor,
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

    public static readonly Value Unit = new(VKind.Unit, 0, null);
    public static Value Bool(bool b) => new(VKind.Bool, b ? 1 : 0, null);
    public static Value Int(VKind kind, long bits) => new(kind, bits, null);
    public static Value Char(int codePoint) => new(VKind.Char, codePoint, null);
    public static Value Str(string s) => new(VKind.Txt, 0, s);
    public static Value Range(Value lo, Value hi, bool inclusive) => new(VKind.Range, 0, new RangeData(lo, hi, inclusive, 1, false));
    public static Value Range(RangeData data) => new(VKind.Range, 0, data);
    public static Value Func(object closure) => new(VKind.Func, 0, closure);
    /// <summary>A <c>then</c> chain: its parts are iterated one after another.</summary>
    public static Value Chain(Value[] parts) => new(VKind.Chain, 0, parts);
    public static Value List(SplatList list) => new(VKind.List, 0, list);
    public static Value Set(SplatSet set) => new(VKind.Set, 0, set);
    public static Value Map(SplatMap map) => new(VKind.Map, 0, map);
    public static Value Tuple(Value[] items) => new(VKind.Tuple, 0, items);
    public static Value Enum(EnumValue ev) => new(VKind.Enum, 0, ev);
    public static Value Obj(HeapObj o) => new(VKind.Object, 0, o);
    public static Value OfTensor(Tensor t) => new(VKind.Tensor, 0, t);
    /// <summary>A date/time value: Date = days since 1970-01-01, Time = ns since midnight, Stamp = UTC ns, Dur = ns, Cal = months.</summary>
    public static Value Temporal(VKind kind, long bits) => new(kind, bits, null);

    /// <summary>A float of the given kind, rounded to that kind's precision.</summary>
    public static Value Float(VKind kind, double d) => new(kind, BitConverter.DoubleToInt64Bits(Round(kind, d)), null);

    public static double Round(VKind kind, double d) => kind switch
    {
        VKind.F32 => (double)(float)d,
        VKind.F16 => (double)(Half)d,
        _ => d,
    };

    public bool AsBool => Bits != 0;
    public double AsDouble => BitConverter.Int64BitsToDouble(Bits);
    public string AsString => (string)Ref!;
    public RangeData AsRange => (RangeData)Ref!;
    public Value[] AsChain => (Value[])Ref!;
    public SplatList AsList => (SplatList)Ref!;
    public SplatSet AsSet => (SplatSet)Ref!;
    public SplatMap AsMap => (SplatMap)Ref!;
    public Value[] AsTuple => (Value[])Ref!;
    public EnumValue AsEnum => (EnumValue)Ref!;
    public HeapObj AsObj => (HeapObj)Ref!;
    public Tensor AsTensor => (Tensor)Ref!;

    public bool IsTemporal => Kind is VKind.Date or VKind.Time or VKind.Stamp or VKind.Dur or VKind.Cal;

    public bool IsInt => Kind <= VKind.Usize;
    public bool IsUnsigned => Kind is >= VKind.U8 and <= VKind.Usize;
    public bool IsFloat => Kind is VKind.F16 or VKind.F32 or VKind.F64;

    /// <summary>An integer value as a 128-bit number (so every integer type, including u64, is exact).</summary>
    public Int128 AsInt128 => IsUnsigned ? (Int128)(ulong)Bits : (Int128)Bits;

    public static Value FromInt128(VKind kind, Int128 v) =>
        Int(kind, Kinds.IsUnsigned(kind) ? unchecked((long)(ulong)v) : (long)v);

    public string TypeName => Kind switch
    {
        VKind.Range => "range",
        VKind.Func => "fn",
        VKind.Chain => "chain",
        VKind.List => "List",
        VKind.Set => "Set",
        VKind.Map => "Map",
        VKind.Tuple => "tuple",
        VKind.Enum => AsEnum.EnumName,
        VKind.Object => AsObj.TypeName,
        VKind.Tensor => AsTensor.BaseKey,
        VKind.Date => "Date", VKind.Time => "Time", VKind.Stamp => "Stamp", VKind.Dur => "Dur", VKind.Cal => "Cal",
        _ => Types.Of((Prim)(int)Kind).Name,
    };

    public override string ToString() => Formatter.Display(this);
}

/// <summary>
/// A range <c>lo..hi</c> / <c>lo..=hi</c>, visiting every <see cref="Step"/>-th integer from <c>lo</c>. A reversed range
/// (<c>rev(...)</c>) visits the same values from the last one back down to the first.
/// </summary>
public sealed record RangeData(Value Lo, Value Hi, bool Inclusive, Int128 Step, bool Reversed, bool Months = false)
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
        if (Months)
        {
            // Calendar steps compute element k as start + k*step, so month-end clamping never drifts.
            var all = MonthItems();
            if (!Reversed) { foreach (var v in all) yield return v; yield break; }
            foreach (var v in Enumerable.Reverse(all)) yield return v;
            yield break;
        }
        Int128 lo = Lo.AsInt128, n = Count;
        for (Int128 k = 0; k < n; k++)
            yield return Value.FromInt128(kind, lo + (Reversed ? n - 1 - k : k) * Step);
    }

    /// <summary>Element <paramref name="k"/> of a calendar-stepped range, or null when it leaves the representable calendar.</summary>
    private Value? MonthAt(long k)
    {
        long months;
        try { months = checked((long)Step * k); } catch (OverflowException) { return null; }
        if (Lo.Kind == VKind.Date)
            return Temporal.TryDateAddMonths(Lo.Bits, months, out long d) ? Value.Temporal(VKind.Date, d) : null;
        return Temporal.TryStampAddMonths(Lo.Bits, months, out long ns) ? Value.Temporal(VKind.Stamp, ns) : null;
    }

    private List<Value> MonthItems()
    {
        var list = new List<Value>();
        for (long k = 0; k < 1_000_000; k++)
        {
            if (MonthAt(k) is not { } v) break;
            if (Inclusive ? v.Bits > Hi.Bits : v.Bits >= Hi.Bits) break;
            list.Add(v);
        }
        return list;
    }

    /// <summary>True when <paramref name="x"/> is one of the values the range visits (on the step grid, within the bounds).</summary>
    public bool Contains(Value x)
    {
        Int128 lo = Lo.AsInt128, hi = Hi.AsInt128, v = x.AsInt128;
        if (v < lo || (Inclusive ? v > hi : v >= hi)) return false;
        if (Months) return MonthItems().Any(i => i.Bits == x.Bits);
        return (v - lo) % Step == 0;
    }
}

// ---- Phase 7: collection and ADT types ----

public sealed class SplatList
{
    public List<Value> Items { get; } = new();
}

public sealed class SplatSet
{
    private readonly Dictionary<Value, int> _index = new(ValueKeyComparer.Instance);
    private readonly List<Value> _order = new();

    public bool Add(Value v) {
        if (_index.ContainsKey(v)) return false;
        _index[v] = _order.Count;
        _order.Add(v);
        return true;
    }
    public bool Contains(Value v) => _index.ContainsKey(v);
    public bool Remove(Value v)
    {
        if (!_index.TryGetValue(v, out int at)) return false;
        _order.RemoveAt(at);
        _index.Clear();
        for (int i = 0; i < _order.Count; i++) _index[_order[i]] = i;
        return true;
    }
    public IReadOnlyList<Value> Items => _order;
    public int Count => _order.Count;
}

public sealed class SplatMap
{
    private readonly Dictionary<Value, int> _index = new(ValueKeyComparer.Instance);
    private readonly List<(Value Key, Value Val)> _order = new();

    public void Set(Value k, Value v) {
        if (_index.TryGetValue(k, out int i)) { _order[i] = (k, v); return; }
        _index[k] = _order.Count;
        _order.Add((k, v));
    }
    public bool TryGet(Value k, out Value v) {
        if (_index.TryGetValue(k, out int i)) { v = _order[i].Val; return true; }
        v = default; return false;
    }
    public bool ContainsKey(Value k) => _index.ContainsKey(k);
    public bool Remove(Value k)
    {
        if (!_index.TryGetValue(k, out int at)) return false;
        _order.RemoveAt(at);
        _index.Clear();
        for (int i = 0; i < _order.Count; i++) _index[_order[i].Key] = i;
        return true;
    }
    public IReadOnlyList<(Value Key, Value Val)> Items => _order;
    public int Count => _order.Count;
}

public sealed class EnumValue
{
    public string EnumName { get; }
    public string VariantName { get; }
    public Value[] Payloads { get; }
    public int PayloadCount => Payloads.Length;
    public EnumValue(string moodName, string variantName, Value[] payloads)
    { EnumName = moodName; VariantName = variantName; Payloads = payloads; }
}

public sealed class ValueKeyComparer : IEqualityComparer<Value>
{
    public static readonly ValueKeyComparer Instance = new();

    public bool Equals(Value a, Value b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            VKind.Txt => string.Equals(a.AsString, b.AsString, StringComparison.Ordinal),
            VKind.Tuple => a.AsTuple.Length == b.AsTuple.Length && a.AsTuple.Zip(b.AsTuple).All(p => Equals(p.First, p.Second)),
            _ when (int)a.Kind <= (int)VKind.Usize || a.Kind is VKind.Bool or VKind.Char or VKind.Date or VKind.Time or VKind.Stamp => a.Bits == b.Bits,
            VKind.Object => ReferenceEquals(a.Ref, b.Ref),
            _ => false,
        };
    }

    public int GetHashCode(Value v) => v.Kind switch
    {
        VKind.Txt => v.AsString.GetHashCode(StringComparison.Ordinal),
        VKind.Tuple => v.AsTuple.Aggregate(0, (h, x) => HashCode.Combine(h, GetHashCode(x))),
        _ when (int)v.Kind <= (int)VKind.Usize || v.Kind is VKind.Bool or VKind.Char or VKind.Date or VKind.Time or VKind.Stamp => v.Bits.GetHashCode(),
        VKind.Object => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v.Ref),
        _ => 0,
    };
}

internal static class Kinds
{
    public static Prim P(VKind k) => (Prim)(int)k;
    public static bool IsUnsigned(VKind k) => k is >= VKind.U8 and <= VKind.Usize;
    public static bool IsSigned(VKind k) => k <= VKind.I64;
    public static bool IsInt(VKind k) => k <= VKind.Usize;
    public static bool IsFloat(VKind k) => k is VKind.F16 or VKind.F32 or VKind.F64;
    public static string Name(VKind k) => Types.Of(P(k)).Name;
    public static VKind Of(SType t) => t is PrimType p ? (VKind)(int)p.Kind : VKind.Unit;
}

/// <summary>Turns values into text for <c>print</c>, <c>txt(x)</c> and string templates.</summary>
public static class Formatter
{
    public static string Display(Value v) => v.Kind switch
    {
        VKind.Bool => v.AsBool ? "true" : "false",
        VKind.Txt => v.AsString,
        VKind.Char => char.ConvertFromUtf32((int)v.Bits),
        VKind.Unit => "()",
        VKind.Range => RangeText(v.AsRange),
        VKind.Func => "<fn>",
        VKind.Chain => string.Join(" then ", v.AsChain.Select(Display)),
        VKind.Object => v.AsObj.Display(),
        VKind.Tensor => TensorFormat.Display(v.AsTensor, null),
        VKind.Date => Temporal.LocaleDate(v.Bits, Temporal.Locale.Default),
        VKind.Time => Temporal.IsoTime(v.Bits),
        VKind.Stamp => Temporal.LocaleStamp(v.Bits, Temporal.Locale.Default),
        VKind.Dur => Temporal.DurText(v.Bits),
        VKind.Cal => Temporal.CalText(v.Bits),
        VKind.List => "[" + string.Join(", ", v.AsList.Items.Select(Display)) + "]",
        VKind.Set => "set{" + string.Join(", ", v.AsSet.Items.Select(Display)) + "}",
        VKind.Map => "map{" + string.Join(", ", v.AsMap.Items.Select(p => Display(p.Key) + ": " + Display(p.Val))) + "}",
        VKind.Tuple => "(" + string.Join(", ", v.AsTuple.Select(Display)) + ")",
        VKind.Enum => v.AsEnum.PayloadCount == 0
            ? v.AsEnum.VariantName
            : v.AsEnum.VariantName + "(" + string.Join(", ", v.AsEnum.Payloads.Select(Display)) + ")",
        _ when v.IsInt => v.AsInt128.ToString(CultureInfo.InvariantCulture),
        _ => FloatText(v.Kind, v.AsDouble),
    };

    private static string RangeText(RangeData r)
    {
        var text = $"{Display(r.Lo)}{(r.Inclusive ? "..=" : "..")}{Display(r.Hi)}";
        if (r.Months) text += $" by {Temporal.CalText((long)r.Step)}";
        else if (r.Lo.Kind == VKind.Date) { if (r.Step != 1) text += $" by {Temporal.DurText((long)r.Step * Temporal.NsPerDay)}"; }
        else if (r.Lo.IsTemporal) text += $" by {Temporal.DurText((long)r.Step)}";
        else if (r.Step != 1) text += $" by {r.Step}";
        return r.Reversed ? $"rev({text})" : text;
    }

    /// <summary>How the REPL echoes a value: like <see cref="Display"/>, but text and chars show their quotes.</summary>
    public static string Repr(Value v) => v.Kind switch
    {
        VKind.Txt => "\"" + v.AsString.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("{", "\\{") + "\"",
        VKind.Char => "c\"" + Display(v).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"",
        _ => Display(v),
    };

    internal static string FloatText(VKind kind, double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        string s = kind switch
        {
            VKind.F32 => ((float)d).ToString("R", CultureInfo.InvariantCulture),
            VKind.F16 => ((Half)d).ToString(CultureInfo.InvariantCulture),
            _ => d.ToString("R", CultureInfo.InvariantCulture),
        };
        // 3 prints as "3.0" so a float never looks like an int.
        return s.Any(c => c is '.' or 'E' or 'e') ? s : s + ".0";
    }

    /// <summary>Renders a date/time with <c>{x:iso}</c> or a <c>%Y-%m-%d</c>-style pattern.</summary>
    public static string FormatTemporal(Value v, string format)
    {
        long days = v.Kind switch
        {
            VKind.Date => v.Bits,
            VKind.Stamp => Temporal.FloorDiv(v.Bits, Temporal.NsPerDay),
            _ => 0,
        };
        long tod = v.Kind switch
        {
            VKind.Time => v.Bits,
            VKind.Stamp => Temporal.FloorMod(v.Bits, Temporal.NsPerDay),
            _ => 0,
        };
        if (format == "iso")
            return v.Kind switch
            {
                VKind.Date => Temporal.IsoDate(v.Bits),
                VKind.Time => Temporal.IsoTime(v.Bits),
                _ => Temporal.IsoStamp(v.Bits),
            };
        return Temporal.TryFormatPattern(format, days, tod, out var text, out _) ? text : format;
    }

    public static string Format(Value v, FormatSpec spec)
    {
        string body;
        if (v.Kind == VKind.Tensor) return TensorFormat.Display(v.AsTensor, spec);
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
