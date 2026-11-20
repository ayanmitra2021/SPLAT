using Splat.Core;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Runtime;

// Arithmetic, comparison and conversion. Integer arithmetic is CHECKED: an out-of-range result is a runtime error,
// never a silent wrap. Integer division and remainder truncate toward zero; dividing an integer by zero is an error,
// while float division by zero follows IEEE (inf / NaN). All integer kinds go through Int128 so u64 is exact.
public sealed partial class Interpreter
{
    private static string OpText(TokenKind k) => k switch
    {
        TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Star => "*", TokenKind.Slash => "/",
        TokenKind.Percent => "%", TokenKind.StarStar => "**", _ => k.ToString(),
    };

    private Value CheckedInt(VKind kind, Int128 r, string op, SourceSpan span)
    {
        var p = Kinds.P(kind);
        if (r < Types.Min(p) || r > Types.Max(p))
            throw Fail(Templates.IntegerOverflow(span, Kinds.Name(kind), op));
        return Value.FromInt128(kind, r);
    }

    private Int128 CheckedMul(Int128 x, Int128 y, VKind kind, SourceSpan span)
    {
        try { return checked(x * y); }
        catch (OverflowException) { throw Fail(Templates.IntegerOverflow(span, Kinds.Name(kind), "*")); }
    }

    private Value Binary(TokenKind op, Value a, Value b, SourceSpan span)
    {
        if (a.Kind == VKind.Object) return ObjectBinary(op, a, b, span);
        if (a.Kind == VKind.Tensor || b.Kind == VKind.Tensor) return TensorBinary(op, a, b, span);
        if (a.IsTemporal || b.IsTemporal) return TemporalBinary(op, a, b, span);
        if (a.IsInt) return IntBinary(op, a, b, span);
        if (a.IsFloat && b.IsInt && op == TokenKind.StarStar) return Value.Float(a.Kind, Transcendentals.Current.Pow(a.AsDouble, (double)b.AsInt128));
        if (a.IsFloat) return FloatBinary(op, a, b, span);

        switch (a.Kind)
        {
            case VKind.Txt:
                switch (op)
                {
                    case TokenKind.Plus: return Value.Str(a.AsString + b.AsString);
                    case TokenKind.EqEq: return Value.Bool(string.Equals(a.AsString, b.AsString, StringComparison.Ordinal));
                    case TokenKind.BangEq: return Value.Bool(!string.Equals(a.AsString, b.AsString, StringComparison.Ordinal));
                    default: return Ordered(op, string.CompareOrdinal(a.AsString, b.AsString), span);
                }
            case VKind.Char:
                return op switch
                {
                    TokenKind.EqEq => Value.Bool(a.Bits == b.Bits),
                    TokenKind.BangEq => Value.Bool(a.Bits != b.Bits),
                    _ => Ordered(op, a.Bits.CompareTo(b.Bits), span),
                };
            case VKind.Bool:
                return op == TokenKind.EqEq ? Value.Bool(a.Bits == b.Bits) : Value.Bool(a.Bits != b.Bits);
            case VKind.Unit:
                return Value.Bool(op == TokenKind.EqEq);
            case VKind.List or VKind.Set or VKind.Map or VKind.Tuple or VKind.Enum:
                return CompoundBinary(op, a, b, span);
            default:
                throw Fail(Templates.Internal(span, $"operator {op} on {a.TypeName}"));
        }
    }

    private Value Ordered(TokenKind op, int cmp, SourceSpan span) => op switch
    {
        TokenKind.Lt => Value.Bool(cmp < 0),
        TokenKind.Le => Value.Bool(cmp <= 0),
        TokenKind.Gt => Value.Bool(cmp > 0),
        TokenKind.Ge => Value.Bool(cmp >= 0),
        TokenKind.EqEq => Value.Bool(cmp == 0),
        TokenKind.BangEq => Value.Bool(cmp != 0),
        _ => throw Fail(Templates.Internal(span, $"comparison {op}")),
    };

    private Value IntBinary(TokenKind op, Value a, Value b, SourceSpan span)
    {
        Int128 x = a.AsInt128, y = b.AsInt128;
        switch (op)
        {
            case TokenKind.EqEq or TokenKind.BangEq or TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge:
                return Ordered(op, x.CompareTo(y), span);
            case TokenKind.Plus: return CheckedInt(a.Kind, x + y, "+", span);
            case TokenKind.Minus: return CheckedInt(a.Kind, x - y, "-", span);
            case TokenKind.Star: return CheckedInt(a.Kind, CheckedMul(x, y, a.Kind, span), "*", span);
            case TokenKind.Slash:
                if (y == 0) throw Fail(Templates.DivideByZero(span));
                return CheckedInt(a.Kind, x / y, "/", span);
            case TokenKind.Percent:
                if (y == 0) throw Fail(Templates.DivideByZero(span));
                return CheckedInt(a.Kind, x % y, "%", span);
            case TokenKind.StarStar:
                return IntPow(a.Kind, x, y, span);
            default:
                throw Fail(Templates.Internal(span, $"integer operator {op}"));
        }
    }

    private Value IntPow(VKind kind, Int128 x, Int128 e, SourceSpan span)
    {
        if (e < 0)
            throw Fail(Templates.MathDomain(span, $"{x} ** {e}: an integer cannot be raised to a negative power (the answer is not an integer)."));

        var p = Kinds.P(kind);
        Int128 min = Types.Min(p), max = Types.Max(p);
        Int128 result = 1, bs = x;
        while (e > 0)
        {
            if ((e & 1) == 1)
            {
                result = CheckedMul(result, bs, kind, span);
                if (result < min || result > max) throw Fail(Templates.IntegerOverflow(span, Kinds.Name(kind), "**"));
            }
            e >>= 1;
            if (e > 0)
            {
                bs = CheckedMul(bs, bs, kind, span);
                if (bs > max) throw Fail(Templates.IntegerOverflow(span, Kinds.Name(kind), "**"));
            }
        }
        return Value.FromInt128(kind, result);
    }

    private Value FloatBinary(TokenKind op, Value a, Value b, SourceSpan span)
    {
        double x = a.AsDouble, y = b.AsDouble;
        switch (op)
        {
            case TokenKind.EqEq: return Value.Bool(x == y);
            case TokenKind.BangEq: return Value.Bool(x != y);
            case TokenKind.Lt: return Value.Bool(x < y);
            case TokenKind.Le: return Value.Bool(x <= y);
            case TokenKind.Gt: return Value.Bool(x > y);
            case TokenKind.Ge: return Value.Bool(x >= y);
            case TokenKind.Plus: return Value.Float(a.Kind, x + y);
            case TokenKind.Minus: return Value.Float(a.Kind, x - y);
            case TokenKind.Star: return Value.Float(a.Kind, x * y);
            case TokenKind.Slash: return Value.Float(a.Kind, x / y);
            case TokenKind.Percent: return Value.Float(a.Kind, x % y);
            case TokenKind.StarStar: return Value.Float(a.Kind, Math.Pow(x, y));
            default: throw Fail(Templates.Internal(span, $"float operator {op}"));
        }
    }

    // ---- explicit conversions: i64(x), f32(n), txt(v), char(code) ----

    private Value Convert(Value v, PrimType target, SourceSpan span)
    {
        var tk = (VKind)(int)target.Kind;
        if (target.Kind == Prim.Txt) return Value.Str(Formatter.Display(v));
        if (v.Kind == VKind.Tensor)
        {
            // f64(A): the same shape, every element converted (rounded to the new precision).
            var src = v.AsTensor;
            var to = ElemOfType(target);
            return to == src.Elem ? v : Value.OfTensor(Tensor.FromDoubles(to, (long[])src.Shape.Clone(), src.ToDoubles()));
        }
        if (v.Kind == tk) return v;

        if (target.Kind == Prim.Char)
        {
            var code = v.AsInt128;
            if (code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
                throw Fail(Templates.BadConversion(span, $"the code point {code}", "char"));
            return Value.Char((int)code);
        }

        if (Types.IsInt(target.Kind))
        {
            Int128 n;
            if (v.Kind == VKind.Char) n = v.Bits;
            else if (v.IsInt) n = v.AsInt128;
            else
            {
                double d = v.AsDouble;
                if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) >= 1.0e38)
                    throw Fail(Templates.BadConversion(span, Formatter.Display(v), target.Name));
                n = (Int128)Math.Truncate(d);
            }
            if (n < Types.Min(target.Kind) || n > Types.Max(target.Kind))
                throw Fail(Templates.BadConversion(span, Formatter.Display(v), target.Name));
            return Value.FromInt128(tk, n);
        }

        // Target is a float.
        double asDouble = v.IsFloat ? v.AsDouble : (double)v.AsInt128;
        return Value.Float(tk, asDouble);
    }
}
