namespace Splat.Semantics;

/// <summary>
/// A parsed <c>{x:spec}</c> format spec: <c>[&lt;&gt;^]? 0? width? (.precision)? [xXbo]?</c>.
/// Shared by the checker (validation) and the interpreter (rendering).
/// </summary>
public sealed record FormatSpec(char? Align, bool ZeroPad, int Width, int? Precision, char? Radix)
{
    public static readonly FormatSpec Default = new(null, false, 0, null, null);

    public static bool TryParse(string text, out FormatSpec spec, out string? error)
    {
        spec = Default;
        error = null;
        int i = 0;
        char? align = null;
        if (i < text.Length && text[i] is '<' or '>' or '^') { align = text[i]; i++; }

        bool zero = false;
        if (i < text.Length && text[i] == '0') { zero = true; i++; }

        int width = 0;
        int start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        if (i > start && !int.TryParse(text.AsSpan(start, i - start), out width) || width > 1000)
        {
            error = "has an absurd width";
            return false;
        }

        int? precision = null;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            if (i == start || !int.TryParse(text.AsSpan(start, i - start), out var p) || p > 100)
            {
                error = "needs a sensible number of digits after the '.'";
                return false;
            }
            precision = p;
        }

        char? radix = null;
        if (i < text.Length && text[i] is 'x' or 'X' or 'b' or 'o') { radix = text[i]; i++; }

        if (i < text.Length)
        {
            error = $"has unexpected '{text[i..]}' in it";
            return false;
        }

        spec = new FormatSpec(align, zero, width, precision, radix);
        return true;
    }
}
