using System.Globalization;
using System.Text;

namespace Splat.Core;

/// <summary>
/// Calendar arithmetic and text for SPLAT's date/time types (spec 4.5), shared by the lexer (literal validation) and the
/// runtime. Representations: <c>Date</c> = days since 1970-01-01 (proleptic Gregorian), <c>Time</c> = nanoseconds since
/// midnight, <c>Stamp</c> = UTC nanoseconds since the epoch, <c>Dur</c> = nanoseconds, <c>Cal</c> = months.
/// </summary>
public static class Temporal
{
    public const long NsPerMs = 1_000_000L;
    public const long NsPerSec = 1_000_000_000L;
    public const long NsPerMin = 60 * NsPerSec;
    public const long NsPerHour = 60 * NsPerMin;
    public const long NsPerDay = 24 * NsPerHour;
    public const long NsPerWeek = 7 * NsPerDay;

    private const int EpochDayNumber = 719162; // DateOnly.DayNumber of 1970-01-01
    public const long MinDays = -EpochDayNumber;
    public const long MaxDays = 3652058 - EpochDayNumber;

    public static bool ValidDays(long days) => days is >= MinDays and <= MaxDays;
    public static DateOnly ToDateOnly(long days) => DateOnly.FromDayNumber((int)(days + EpochDayNumber));
    public static long FromDateOnly(DateOnly d) => d.DayNumber - EpochDayNumber;

    // ---- floor division (negative stamps) ----

    public static long FloorDiv(long a, long b)
    {
        long q = a / b;
        return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
    }

    public static long FloorMod(long a, long b) => a - FloorDiv(a, b) * b;

    // ---- Stamp <-> DateTime ----

    public static DateTime StampToDateTime(long ns, out long subTickNs)
    {
        long ticks = FloorDiv(ns, 100);
        subTickNs = ns - ticks * 100;
        return DateTime.UnixEpoch.AddTicks(ticks);
    }

    public static bool TryDateTimeToStamp(DateTime utc, long subTickNs, out long ns)
    {
        try
        {
            ns = checked((utc.Ticks - DateTime.UnixEpoch.Ticks) * 100 + subTickNs);
            return true;
        }
        catch (OverflowException) { ns = 0; return false; }
    }

    // ---- civil parts ----

    public static (int Y, int M, int D) DateParts(long days) { var d = ToDateOnly(days); return (d.Year, d.Month, d.Day); }

    /// <summary>ISO weekday: 1 = Monday ... 7 = Sunday.</summary>
    public static int Weekday(long days)
    {
        int w = (int)ToDateOnly(days).DayOfWeek; // Sunday = 0
        return w == 0 ? 7 : w;
    }

    public static (int H, int Mi, int S) TimeParts(long nsOfDay)
    {
        long secs = nsOfDay / NsPerSec;
        return ((int)(secs / 3600), (int)(secs / 60 % 60), (int)(secs % 60));
    }

    // ---- arithmetic that can leave the representable range ----

    public static bool TryDateAddMonths(long days, long months, out long result)
    {
        result = 0;
        if (months is > 200_000 or < -200_000) return false;
        try { result = FromDateOnly(ToDateOnly(days).AddMonths((int)months)); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    public static bool TryDateAddDays(long days, long delta, out long result)
    {
        result = 0;
        try { result = checked(days + delta); return ValidDays(result); }
        catch (OverflowException) { return false; }
    }

    public static bool TryStampAddMonths(long ns, long months, out long result)
    {
        result = 0;
        if (months is > 200_000 or < -200_000) return false;
        try
        {
            var dt = StampToDateTime(ns, out long sub).AddMonths((int)months);
            return TryDateTimeToStamp(dt, sub, out result);
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    // ---- parsing (ISO; used by literals and by date()/time()/stamp()) ----

    private static bool Digits(string s, ref int i, int count, out int value)
    {
        value = 0;
        if (i + count > s.Length) return false;
        for (int k = 0; k < count; k++)
        {
            char c = s[i + k];
            if (!char.IsAsciiDigit(c)) return false;
            value = value * 10 + (c - '0');
        }
        i += count;
        return true;
    }

    /// <summary>Parses <c>yyyy-MM-dd</c>. On failure <paramref name="problem"/> says what is wrong (for the lexer's message).</summary>
    public static bool TryParseDate(string s, out long days, out string? problem)
    {
        days = 0; problem = null;
        int i = 0;
        if (!Digits(s, ref i, 4, out int y) || i >= s.Length || s[i++] != '-' || !Digits(s, ref i, 2, out int m)
            || i >= s.Length || s[i++] != '-' || !Digits(s, ref i, 2, out int d) || i != s.Length)
        { problem = "a date is written yyyy-MM-dd, like d\"2024-03-01\""; return false; }
        if (y < 1) { problem = "the year must be 0001 or later"; return false; }
        if (m is < 1 or > 12) { problem = $"there is no month {m}"; return false; }
        if (d < 1 || d > DateTime.DaysInMonth(y, m)) { problem = $"{MonthName(m)} {y} has no day {d}"; return false; }
        days = FromDateOnly(new DateOnly(y, m, d));
        return true;
    }

    /// <summary>Parses <c>HH:mm[:ss[.fraction]]</c> into nanoseconds since midnight.</summary>
    public static bool TryParseTime(string s, out long ns, out string? problem)
    {
        ns = 0; problem = null;
        int i = 0;
        if (!Digits(s, ref i, 2, out int h) || i >= s.Length || s[i++] != ':' || !Digits(s, ref i, 2, out int mi))
        { problem = "a time is written HH:mm:ss, like tm\"09:30:00\""; return false; }
        int sec = 0; long frac = 0;
        if (i < s.Length && s[i] == ':')
        {
            i++;
            if (!Digits(s, ref i, 2, out sec)) { problem = "a time is written HH:mm:ss, like tm\"09:30:00\""; return false; }
            if (i < s.Length && s[i] == '.')
            {
                i++;
                int start = i;
                long scale = NsPerSec / 10;
                while (i < s.Length && char.IsAsciiDigit(s[i]) && i - start < 9) { frac += (s[i] - '0') * scale; scale /= 10; i++; }
                if (i == start) { problem = "a fraction needs digits after the '.'"; return false; }
            }
        }
        if (i != s.Length) { problem = "a time is written HH:mm:ss, like tm\"09:30:00\""; return false; }
        if (h > 23) { problem = $"there is no hour {h} on a 24-hour clock"; return false; }
        if (mi > 59) { problem = $"there is no minute {mi}"; return false; }
        if (sec > 59) { problem = $"there is no second {sec}"; return false; }
        ns = h * NsPerHour + mi * NsPerMin + sec * NsPerSec + frac;
        return true;
    }

    /// <summary>Parses <c>yyyy-MM-ddTHH:mm:ss[.f](Z|+hh:mm|-hh:mm)?</c> (no zone means UTC), normalised to UTC.</summary>
    public static bool TryParseStamp(string s, out long ns, out string? problem)
    {
        ns = 0; problem = null;
        int t = s.IndexOfAny(['T', 't', ' ']);
        if (t < 0) { problem = "a timestamp is written yyyy-MM-ddTHH:mm:ssZ, like t\"2024-03-01T10:15:00Z\""; return false; }
        if (!TryParseDate(s[..t], out long days, out problem)) return false;
        string rest = s[(t + 1)..];
        long offsetNs = 0;
        int zone = rest.IndexOfAny(['Z', 'z', '+', '-']);
        if (zone >= 0)
        {
            string z = rest[zone..];
            rest = rest[..zone];
            if (z is "Z" or "z") { }
            else
            {
                int sign = z[0] == '-' ? -1 : 1;
                int j = 1;
                if (!Digits(z, ref j, 2, out int oh)) { problem = "the zone offset must look like +05:30"; return false; }
                int om = 0;
                if (j < z.Length && z[j] == ':') j++;
                if (j < z.Length && !Digits(z, ref j, 2, out om)) { problem = "the zone offset must look like +05:30"; return false; }
                if (j != z.Length || oh > 23 || om > 59) { problem = "the zone offset must look like +05:30"; return false; }
                offsetNs = sign * (oh * NsPerHour + om * NsPerMin);
            }
        }
        if (!TryParseTime(rest, out long tod, out problem)) return false;
        try
        {
            ns = checked(days * NsPerDay + tod - offsetNs);
            return true;
        }
        catch (OverflowException) { problem = "that moment is outside the Stamp range (about the years 1678 to 2262)"; return false; }
    }

    private static string MonthName(int m) => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m);

    // ---- duration suffixes: 500ms 5s 15m 1h 7d 2w (Dur) and 1mo 1q 1y (Cal) ----

    /// <summary>Nanoseconds per Dur suffix, or null if the suffix is not one.</summary>
    public static long? DurUnit(string suffix) => suffix switch
    {
        "ms" => NsPerMs, "s" => NsPerSec, "m" => NsPerMin, "h" => NsPerHour, "d" => NsPerDay, "w" => NsPerWeek, _ => null,
    };

    /// <summary>Months per Cal suffix, or null if the suffix is not one.</summary>
    public static long? CalUnit(string suffix) => suffix switch { "mo" => 1, "q" => 3, "y" => 12, _ => null };

    // ---- locale-aware text ----

    public enum FieldOrder { MonthDayYear, DayMonthYear, YearMonthDay }

    /// <summary>How dates are written: field order and separator. Built from a culture tag such as <c>en-US</c>.</summary>
    public sealed record Locale(string Tag, FieldOrder Order, char Separator)
    {
        public static readonly Locale EnUs = new("en-US", FieldOrder.MonthDayYear, '/');

        private static Locale _default = FromEnvironment();

        /// <summary>The locale used when printing. <c>SPLAT_LOCALE</c> or <c>--locale</c> pins it; otherwise the system locale.</summary>
        public static Locale Default { get => _default; set => _default = value; }

        public static Locale FromEnvironment()
        {
            var tag = Environment.GetEnvironmentVariable("SPLAT_LOCALE");
            if (!string.IsNullOrWhiteSpace(tag) && TryFromTag(tag, out var l)) return l;
            return FromCulture(CultureInfo.CurrentCulture);
        }

        public static bool TryFromTag(string tag, out Locale locale)
        {
            try
            {
                locale = FromCulture(CultureInfo.GetCultureInfo(tag.Trim()));
                return true;
            }
            catch (CultureNotFoundException) { locale = EnUs; return false; }
        }

        public static Locale FromCulture(CultureInfo c)
        {
            string pattern = c.DateTimeFormat.ShortDatePattern;
            var order = new List<char>();
            char sep = '/';
            bool sepFound = false;
            foreach (char ch in pattern)
            {
                if (ch is 'd' or 'M' or 'y') { if (order.Count == 0 || order[^1] != ch) order.Add(ch); }
                else if (!sepFound && !char.IsLetter(ch) && !char.IsWhiteSpace(ch)) { sep = ch; sepFound = true; }
            }
            var fo = order.Count > 0 && order[0] == 'y' ? FieldOrder.YearMonthDay
                : order.Count > 0 && order[0] == 'd' ? FieldOrder.DayMonthYear : FieldOrder.MonthDayYear;
            return new Locale(c.Name.Length == 0 ? "invariant" : c.Name, fo, sep);
        }
    }

    public static string IsoDate(long days)
    {
        var (y, m, d) = DateParts(days);
        return $"{y:D4}-{m:D2}-{d:D2}";
    }

    public static string IsoTime(long nsOfDay)
    {
        var (h, mi, s) = TimeParts(nsOfDay);
        return $"{h:D2}:{mi:D2}:{s:D2}{Fraction(nsOfDay % NsPerSec)}";
    }

    private static string Fraction(long ns) => ns == 0 ? "" : "." + ns.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');

    public static string IsoStamp(long ns)
    {
        long days = FloorDiv(ns, NsPerDay);
        return IsoDate(days) + "T" + IsoTime(FloorMod(ns, NsPerDay)) + "Z";
    }

    public static string LocaleDate(long days, Locale loc)
    {
        var (y, m, d) = DateParts(days);
        char s = loc.Separator;
        return loc.Order switch
        {
            FieldOrder.DayMonthYear => $"{d:D2}{s}{m:D2}{s}{y:D4}",
            FieldOrder.YearMonthDay => $"{y:D4}{s}{m:D2}{s}{d:D2}",
            _ => $"{m:D2}{s}{d:D2}{s}{y:D4}",
        };
    }

    public static string LocaleStamp(long ns, Locale loc)
    {
        long days = FloorDiv(ns, NsPerDay);
        return LocaleDate(days, loc) + " " + IsoTime(FloorMod(ns, NsPerDay));
    }

    /// <summary>strftime-style patterns: %Y %y %m %d %H %M %S %f (ns) %b %B %a %A %j %%.</summary>
    public static bool TryFormatPattern(string pattern, long days, long nsOfDay, out string result, out string? problem)
    {
        var sb = new StringBuilder();
        problem = null;
        var (y, m, d) = DateParts(days);
        var (h, mi, s) = TimeParts(nsOfDay);
        var inv = CultureInfo.InvariantCulture.DateTimeFormat;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c != '%') { sb.Append(c); continue; }
            if (++i >= pattern.Length) { problem = "ends with a lone '%'"; result = ""; return false; }
            switch (pattern[i])
            {
                case 'Y': sb.Append(y.ToString("D4", CultureInfo.InvariantCulture)); break;
                case 'y': sb.Append((y % 100).ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'm': sb.Append(m.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'd': sb.Append(d.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'H': sb.Append(h.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'M': sb.Append(mi.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'S': sb.Append(s.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'f': sb.Append((nsOfDay % NsPerSec).ToString("D9", CultureInfo.InvariantCulture)); break;
                case 'b': sb.Append(inv.GetAbbreviatedMonthName(m)); break;
                case 'B': sb.Append(inv.GetMonthName(m)); break;
                case 'a': sb.Append(inv.GetAbbreviatedDayName(ToDateOnly(days).DayOfWeek)); break;
                case 'A': sb.Append(inv.GetDayName(ToDateOnly(days).DayOfWeek)); break;
                case 'j': sb.Append(ToDateOnly(days).DayOfYear.ToString("D3", CultureInfo.InvariantCulture)); break;
                case '%': sb.Append('%'); break;
                default: problem = $"has an unknown code '%{pattern[i]}' (known: %Y %y %m %d %H %M %S %f %b %B %a %A %j %%)"; result = ""; return false;
            }
        }
        result = sb.ToString();
        return true;
    }

    /// <summary>Compact text for a duration: <c>1h30m</c>, <c>500ms</c>, <c>7d</c>, <c>0s</c>.</summary>
    public static string DurText(long ns)
    {
        if (ns == 0) return "0s";
        var sb = new StringBuilder();
        UInt128 rest = ns < 0 ? (UInt128)(-(Int128)ns) : (UInt128)ns;
        if (ns < 0) sb.Append('-');
        void Unit(long size, string name)
        {
            if (rest >= (UInt128)size) { sb.Append((rest / (UInt128)size).ToString()).Append(name); rest %= (UInt128)size; }
        }
        Unit(NsPerDay, "d"); Unit(NsPerHour, "h"); Unit(NsPerMin, "m"); Unit(NsPerSec, "s");
        if (rest > 0)
        {
            if (rest % (UInt128)NsPerMs == 0) sb.Append((rest / (UInt128)NsPerMs).ToString()).Append("ms");
            else sb.Append(rest.ToString()).Append("ns");
        }
        return sb.ToString();
    }

    /// <summary>Compact text for a calendar span: <c>1y2mo</c>, <c>3mo</c>.</summary>
    public static string CalText(long months)
    {
        if (months == 0) return "0mo";
        var sb = new StringBuilder();
        long abs = Math.Abs(months);
        if (months < 0) sb.Append('-');
        if (abs >= 12) sb.Append(abs / 12).Append('y');
        if (abs % 12 != 0) sb.Append(abs % 12).Append("mo");
        return sb.ToString();
    }

    // ---- parsing text that follows the locale (date(txt) / time(txt) / stamp(txt)) ----

    public static bool TryParseDateLocale(string text, Locale loc, out long days)
    {
        text = text.Trim();
        if (TryParseDate(text, out days, out _)) return true;
        var parts = text.Split(['/', '.', '-'], StringSplitOptions.None);
        days = 0;
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return false;
        int a = int.Parse(parts[0], CultureInfo.InvariantCulture), b = int.Parse(parts[1], CultureInfo.InvariantCulture),
            c = int.Parse(parts[2], CultureInfo.InvariantCulture);
        int y, m, d;
        switch (loc.Order)
        {
            case FieldOrder.DayMonthYear: d = a; m = b; y = c; break;
            case FieldOrder.YearMonthDay: y = a; m = b; d = c; break;
            default: m = a; d = b; y = c; break;
        }
        if (y < 1 || y > 9999 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        days = FromDateOnly(new DateOnly(y, m, d));
        return true;
    }

    public static bool TryParseStampLocale(string text, Locale loc, out long ns)
    {
        text = text.Trim();
        if (TryParseStamp(text, out ns, out _)) return true;
        int sp = text.IndexOf(' ');
        ns = 0;
        if (sp < 0) return false;
        if (!TryParseDateLocale(text[..sp], loc, out long days) || !TryParseTime(text[(sp + 1)..], out long tod, out _)) return false;
        ns = days * NsPerDay + tod;
        return true;
    }
}
