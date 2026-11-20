using System.Globalization;
using System.Text;
using Splat.Core;

namespace Splat.Syntax;

/// <summary>
/// Scans SPLAT source into tokens. Never throws: problems are reported to the <see cref="DiagnosticEngine"/> and
/// scanning continues. A lexer can run over a sub-range of a file, which is how string interpolations are re-lexed
/// with correct spans.
/// </summary>
public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["var"] = TokenKind.Var, ["fn"] = TokenKind.Fn, ["ret"] = TokenKind.Return,
        ["if"] = TokenKind.If, ["elif"] = TokenKind.Elif, ["el"] = TokenKind.Else,
        ["loop"] = TokenKind.Loop, ["br"] = TokenKind.Break, ["cont"] = TokenKind.Continue, ["pick"] = TokenKind.Pick,
        ["class"] = TokenKind.Class, ["from"] = TokenKind.From, ["with"] = TokenKind.With,
        ["trait"] = TokenKind.Trait, ["extend"] = TokenKind.Extend, ["self"] = TokenKind.Self, ["super"] = TokenKind.Super,
        ["init"] = TokenKind.Init, ["deinit"] = TokenKind.Deinit, ["secret"] = TokenKind.Secret,
        ["enum"] = TokenKind.Enum, ["try"] = TokenKind.Try, ["catch"] = TokenKind.Catch, ["rip"] = TokenKind.Rip,
        ["arena"] = TokenKind.Arena, ["drop"] = TokenKind.Drop, ["imp"] = TokenKind.Import,
        ["regress"] = TokenKind.Regress, ["classify"] = TokenKind.Classify, ["network"] = TokenKind.Network, ["train"] = TokenKind.Train,
        ["fit"] = TokenKind.Fit, ["on"] = TokenKind.On, ["in"] = TokenKind.In, ["while"] = TokenKind.While,
        ["and"] = TokenKind.And, ["or"] = TokenKind.Or, ["true"] = TokenKind.True, ["false"] = TokenKind.False,
        ["grad"] = TokenKind.Grad, ["intg"] = TokenKind.Intg, ["sum"] = TokenKind.Sum, ["prod"] = TokenKind.Prod,
    };

    private readonly SourceText _src;
    private readonly string _text;
    private readonly DiagnosticEngine _diag;
    private readonly int _start;
    private readonly int _end;
    private int _pos;

    // Open '(' and '[' brackets, innermost last. A ':' directly inside '[' is a slice colon.
    private readonly List<char> _brackets = [];
    private TokenKind _prev = TokenKind.EndOfFile;

    // Trivia seen before the token currently being scanned.
    private bool _space;
    private bool _newline;

    /// <param name="start">First offset to scan.</param>
    /// <param name="end">Offset to stop at (exclusive); negative means end of file.</param>
    public Lexer(SourceText source, DiagnosticEngine diagnostics, int start = 0, int end = -1)
    {
        _src = source;
        _text = source.Text;
        _diag = diagnostics;
        _start = Math.Clamp(start, 0, _text.Length);
        _end = end < 0 ? _text.Length : Math.Clamp(end, _start, _text.Length);
        _pos = _start;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipTrivia();
            if (_pos >= _end)
            {
                tokens.Add(Make(TokenKind.EndOfFile, _pos));
                return tokens;
            }
            var tok = ScanToken();
            if (tok is null) continue; // bad input was reported and skipped
            tokens.Add(tok);
            _prev = tok.Kind;
        }
    }

    // ---- helpers ----

    private char Cur => _pos < _end ? _text[_pos] : '\0';
    private char Peek(int n) => _pos + n < _end ? _text[_pos + n] : '\0';

    private Token Make(TokenKind kind, int start, object? value = null, IReadOnlyList<StringPart>? parts = null) =>
        new(kind, _text[start.._pos], _src.Span(start, _pos - start), start, value, _space || start == _start, _newline, parts);

    private Token Op(TokenKind kind, int len)
    {
        int start = _pos;
        _pos += len;
        return Make(kind, start);
    }

    private void Report(Diagnostic d) => _diag.Report(d);

    // ---- trivia ----

    private void SkipTrivia()
    {
        _space = false;
        _newline = false;
        while (_pos < _end)
        {
            char c = _text[_pos];
            if (c == '\n') { _newline = true; _space = true; _pos++; }
            else if (char.IsWhiteSpace(c)) { _space = true; _pos++; }
            else if (c == '#')
            {
                _space = true;
                if (Peek(1) == '-') SkipBlockComment();
                else while (_pos < _end && _text[_pos] != '\n') _pos++;
            }
            else break;
        }
    }

    // '#- ... -#', nestable so code containing block comments can itself be commented out.
    private void SkipBlockComment()
    {
        int start = _pos;
        _pos += 2;
        int depth = 1;
        while (_pos < _end)
        {
            if (Cur == '#' && Peek(1) == '-') { depth++; _pos += 2; }
            else if (Cur == '-' && Peek(1) == '#')
            {
                depth--;
                _pos += 2;
                if (depth == 0) return;
            }
            else
            {
                if (Cur == '\n') _newline = true;
                _pos++;
            }
        }
        Report(Templates.UnterminatedComment(_src.Span(start, 2)));
    }

    // ---- tokens ----

    private Token? ScanToken()
    {
        char c = Cur;
        if (char.IsLetter(c) || c == '_') return ScanWord();
        if (char.IsAsciiDigit(c)) return ScanNumber();
        if (c == '"') return ScanString();

        switch (c)
        {
            case ':':
                if (Peek(1) == ':') return Op(TokenKind.DoubleColon, 2);
                if (Peek(1) == '=') return Op(TokenKind.ColonAssign, 2);
                return Op(_brackets.Count > 0 && _brackets[^1] == '[' ? TokenKind.SliceColon : TokenKind.Colon, 1);
            case ';': return Op(TokenKind.Semicolon, 1);
            case ',': return Op(TokenKind.Comma, 1);
            case '(': _brackets.Add('('); return Op(TokenKind.LParen, 1);
            case '[': _brackets.Add('['); return Op(TokenKind.LBracket, 1);
            case ')': CloseBracket('('); return Op(TokenKind.RParen, 1);
            case ']': CloseBracket('['); return Op(TokenKind.RBracket, 1);
            case '+': return Peek(1) == '=' ? Op(TokenKind.PlusAssign, 2) : Op(TokenKind.Plus, 1);
            case '-': return Peek(1) == '=' ? Op(TokenKind.MinusAssign, 2) : Op(TokenKind.Minus, 1);
            case '*':
                if (Peek(1) == '*') return Op(TokenKind.StarStar, 2);
                return Peek(1) == '=' ? Op(TokenKind.StarAssign, 2) : Op(TokenKind.Star, 1);
            case '/': return Peek(1) == '=' ? Op(TokenKind.SlashAssign, 2) : Op(TokenKind.Slash, 1);
            case '%': return Op(TokenKind.Percent, 1);
            case '@': return Peek(1) == '=' ? Op(TokenKind.AtAssign, 2) : Op(TokenKind.At, 1);
            case '\\': return Op(TokenKind.Backslash, 1);
            case '·': return Op(TokenKind.MiddleDot, 1);
            case '<': return Peek(1) == '=' ? Op(TokenKind.Le, 2) : Op(TokenKind.Lt, 1);
            case '>':
                if (Peek(1) == '=') return Op(TokenKind.Ge, 2);
                return Peek(1) == '>' ? Op(TokenKind.GreaterGreater, 2) : Op(TokenKind.Gt, 1);
            case '=':
                if (Peek(1) == '=') return Op(TokenKind.EqEq, 2);
                return Peek(1) == '>' ? Op(TokenKind.FatArrow, 2) : Op(TokenKind.Assign, 1);
            case '!': return Peek(1) == '=' ? Op(TokenKind.BangEq, 2) : Op(TokenKind.Bang, 1);
            case '&': return Op(TokenKind.Amp, 1);
            case '~': return Op(TokenKind.Tilde, 1);
            // '||' is deliberately two Pipe tokens; the parser uses SpaceBefore to tell norm bars from lambda bars.
            case '|': return Peek(1) == '>' ? Op(TokenKind.PipeGreater, 2) : Op(TokenKind.Pipe, 1);
            case '$': return Op(TokenKind.Dollar, 1);
            case '?': return Op(TokenKind.Question, 1);
            case '.': return ScanDot();
            case '\'': return ScanTick();
            case '∂': return Op(TokenKind.Grad, 1);
            case '∫': return Op(TokenKind.Intg, 1);
            case '∑': return Op(TokenKind.Sum, 1);
            case '∏': return Op(TokenKind.Prod, 1);
        }

        int len = char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek(1)) ? 2 : 1;
        Report(Templates.BadCharacter(_src.Span(_pos, len), c));
        _pos += len;
        return null;
    }

    private void CloseBracket(char opener)
    {
        int i = _brackets.LastIndexOf(opener);
        if (i >= 0) _brackets.RemoveRange(i, _brackets.Count - i); // also drops unmatched openers in between
    }

    private Token ScanDot()
    {
        if (Peek(1) == '.') return Peek(2) == '=' ? Op(TokenKind.DotDotEq, 3) : Op(TokenKind.DotDot, 2);
        return Peek(1) switch
        {
            '*' => Peek(2) == '=' ? Op(TokenKind.DotStarAssign, 3) : Op(TokenKind.DotStar, 2),
            '/' => Peek(2) == '=' ? Op(TokenKind.DotSlashAssign, 3) : Op(TokenKind.DotSlash, 2),
            '+' => Op(TokenKind.DotPlus, 2),
            '-' => Op(TokenKind.DotMinus, 2),
            _ => Op(TokenKind.Dot, 1),
        };
    }

    // ' is postfix transpose and has no other meaning (chars are c"a"), so it is valid only after a value.
    private Token? ScanTick()
    {
        bool afterValue = _prev is TokenKind.Ident or TokenKind.RParen or TokenKind.RBracket
            or TokenKind.Transpose or TokenKind.ConjTranspose or TokenKind.Self;
        if (!afterValue)
        {
            Report(Templates.BadCharacter(_src.Span(_pos, 1), '\'',
                "Transpose ' must directly follow a value, like A' or (A @ B)'. For a character literal write c\"a\"."));
            _pos++;
            return null;
        }
        return Peek(1) == '\'' ? Op(TokenKind.ConjTranspose, 2) : Op(TokenKind.Transpose, 1);
    }

    private Token ScanWord()
    {
        int start = _pos;
        while (_pos < _end && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
        string word = _text[start.._pos];

        if (word == "c" && Cur == '"') return ScanChar(start);
        if (word is "d" or "t" or "tm" && Cur == '"') return ScanTemporalString(start, word);
        if (Keywords.TryGetValue(word, out var kind)) return Make(kind, start);
        return Make(TokenKind.Ident, start, word);
    }

    // ---- numbers ----

    private Token ScanNumber()
    {
        int start = _pos;
        TokenKind kind = TokenKind.Int;
        object value;

        if (Cur == '0' && Peek(1) is 'x' or 'X' or 'b' or 'B')
        {
            int radix = Peek(1) is 'x' or 'X' ? 16 : 2;
            _pos += 2;
            int digitsStart = _pos;
            while (_pos < _end && (IsDigitOfRadix(_text[_pos], radix) || _text[_pos] == '_')) _pos++;
            value = ParseInteger(_text[digitsStart.._pos].Replace("_", ""), radix, start, radix == 16 ? "0x" : "0b");
        }
        else
        {
            while (_pos < _end && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
            bool isFloat = false;
            if (Cur == '.' && char.IsAsciiDigit(Peek(1)))
            {
                isFloat = true;
                _pos++;
                while (_pos < _end && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
            }
            if (Cur is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
            {
                isFloat = true;
                _pos += 2;
                while (_pos < _end && char.IsAsciiDigit(_text[_pos])) _pos++;
            }

            string clean = _text[start.._pos].Replace("_", "");
            if (isFloat)
            {
                kind = TokenKind.Float;
                double d = double.Parse(clean, CultureInfo.InvariantCulture);
                if (double.IsInfinity(d))
                {
                    Report(Templates.BadLiteral(_src.Span(start, _pos - start), "This float is too big for even a f64.", "Use a smaller magnitude."));
                    d = 0;
                }
                value = d;
            }
            else value = ParseInteger(clean, 10, start, "");
        }

        // '500ms', '15m', '1mo': an integer glued to exactly one of the duration suffixes is a Dur or Cal literal.
        if (kind == TokenKind.Int && _pos < _end && char.IsAsciiLetter(_text[_pos]) && !_text[start..].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && !_text[start..].StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            int p = _pos;
            while (p < _end && (char.IsLetterOrDigit(_text[p]) || _text[p] == '_')) p++;
            string suffix = _text[_pos..p];
            ulong n = (ulong)value;
            if (Temporal.DurUnit(suffix) is { } nsPer)
            {
                _pos = p;
                return TemporalNumber(start, TemporalKind.Dur, n, nsPer);
            }
            if (Temporal.CalUnit(suffix) is { } months)
            {
                _pos = p;
                return TemporalNumber(start, TemporalKind.Cal, n, months);
            }
        }

        // '12abc', '0b102', '1_000x': a number must not run straight into a word or digit.
        if (_pos < _end && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
        {
            int junkStart = _pos;
            while (_pos < _end && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
            Report(Templates.BadLiteral(_src.Span(start, _pos - start),
                $"'{_text[start.._pos]}' is not a number and not a name; it is a crime scene.",
                $"Remove '{_text[junkStart.._pos]}', or put a space between the number and what follows."));
        }
        return Make(kind, start, value);
    }

    private Token TemporalNumber(int start, TemporalKind kind, ulong n, long unit)
    {
        long v = 0;
        try { v = checked((long)n * unit); if (n > long.MaxValue) throw new OverflowException(); }
        catch (OverflowException)
        {
            Report(Templates.BadLiteral(_src.Span(start, _pos - start), "That duration is longer than the universe has been around (it overflows 64 bits).", "Use a smaller number or a bigger unit."));
        }
        return Make(TokenKind.Temporal, start, new TemporalValue(kind, v));
    }

    /// <summary>d"2024-03-01", tm"09:30:00" and t"2024-03-01T10:15:00Z": ISO text between plain quotes, validated here.</summary>
    private Token ScanTemporalString(int start, string prefix)
    {
        _pos++; // opening quote
        int bodyStart = _pos;
        while (_pos < _end && _text[_pos] != '"' && _text[_pos] != '\n') _pos++;
        if (_pos >= _end || _text[_pos] != '"')
        {
            Report(Templates.UnterminatedString(_src.Span(start, 1)));
            return Make(TokenKind.Temporal, start, new TemporalValue(TemporalKind.Date, 0));
        }
        string body = _text[bodyStart.._pos];
        _pos++; // closing quote
        var kind = prefix switch { "d" => TemporalKind.Date, "tm" => TemporalKind.Time, _ => TemporalKind.Stamp };
        long value = 0;
        string? problem;
        bool ok = kind switch
        {
            TemporalKind.Date => Temporal.TryParseDate(body, out value, out problem),
            TemporalKind.Time => Temporal.TryParseTime(body, out value, out problem),
            _ => Temporal.TryParseStamp(body, out value, out problem),
        };
        if (!ok)
        {
            Report(Templates.BadLiteral(_src.Span(start, _pos - start), $"{prefix}\"{body}\" is not a real {(kind == TemporalKind.Date ? "date" : kind == TemporalKind.Time ? "time of day" : "timestamp")}: {problem}.",
                kind == TemporalKind.Date ? "Write dates as d\"yyyy-MM-dd\"." : kind == TemporalKind.Time ? "Write times as tm\"HH:mm:ss\" on a 24-hour clock." : "Write timestamps as t\"yyyy-MM-ddTHH:mm:ssZ\"."));
            value = 0;
        }
        return Make(TokenKind.Temporal, start, new TemporalValue(kind, value));
    }

    private static bool IsDigitOfRadix(char c, int radix) => radix == 16 ? Uri.IsHexDigit(c) : c is '0' or '1';

    private ulong ParseInteger(string digits, int radix, int start, string prefix)
    {
        if (digits.Length == 0)
        {
            Report(Templates.BadLiteral(_src.Span(start, _pos - start), $"'{prefix}' with no digits after it. Bold, but empty.",
                $"Add digits after '{prefix}'."));
            return 0UL;
        }
        ulong v = 0;
        foreach (char ch in digits)
        {
            ulong d = (ulong)Convert.ToInt32(ch.ToString(), 16);
            if (v > (ulong.MaxValue - d) / (ulong)radix)
            {
                Report(Templates.BadLiteral(_src.Span(start, _pos - start), "This integer does not fit in 64 bits. Not even a u64 can carry that.",
                    "Use a smaller value, or a float for huge magnitudes."));
                return 0UL;
            }
            v = v * (ulong)radix + d;
        }
        return v;
    }

    // ---- strings ----

    private Token ScanString()
    {
        int start = _pos;
        _pos++; // opening quote
        var parts = new List<StringPart>();
        var literal = new StringBuilder();
        int literalStart = _pos;
        bool terminated = false;

        void Flush()
        {
            if (literal.Length > 0)
                parts.Add(new TextPart(literal.ToString(), _src.Span(literalStart, _pos - literalStart)));
            literal.Clear();
        }

        while (_pos < _end)
        {
            char c = _text[_pos];
            if (c == '"') { terminated = true; break; }
            if (c == '\\') { ReadEscape(literal); continue; }
            if (c == '{')
            {
                Flush();
                if (!ScanInterpolation(parts)) { _pos = _end; break; }
                literalStart = _pos;
                continue;
            }
            literal.Append(c);
            _pos++;
        }

        if (terminated)
        {
            Flush();
            _pos++; // closing quote
        }
        else Report(Templates.UnterminatedString(_src.Span(start, 1)));

        return Make(TokenKind.String, start, null, parts);
    }

    // Reads '\x' at the cursor and appends the decoded character.
    private void ReadEscape(StringBuilder sb)
    {
        int start = _pos;
        _pos++; // backslash
        if (_pos >= _end) return;
        char n = _text[_pos];
        switch (n)
        {
            case 'n': sb.Append('\n'); break;
            case 't': sb.Append('\t'); break;
            case 'r': sb.Append('\r'); break;
            case '0': sb.Append('\0'); break;
            case '"': case '\\': case '{': case '}': sb.Append(n); break;
            default:
                Report(Templates.BadLiteral(_src.Span(start, 2), $"Unknown escape '\\{n}'. Even backslashes have standards.",
                    "Valid escapes are \\n \\t \\r \\0 \\\" \\\\ \\{ \\}."));
                sb.Append(n);
                break;
        }
        _pos++;
    }

    // Cursor is on '{'. Returns false when the string ends before the matching '}'.
    private bool ScanInterpolation(List<StringPart> parts)
    {
        int open = _pos;
        _pos++;
        int exprStart = _pos;
        int exprEnd = -1, formatStart = -1;
        int nest = 0;

        while (_pos < _end)
        {
            char c = _text[_pos];
            if (exprEnd >= 0)
            {
                // Format spec: everything up to the first '}'.
                if (c == '}') break;
                _pos++;
                continue;
            }
            if (c == '"') { _pos = SkipNestedString(_pos); continue; }
            if (c is '{' or '(' or '[') nest++;
            else if (c is ')' or ']') { if (nest > 0) nest--; }
            else if (c == '}')
            {
                if (nest == 0) break;
                nest--;
            }
            else if (c == ':' && nest == 0)
            {
                if (Peek(1) is ':' or '=') { _pos += 2; continue; } // '::' and ':=' belong to the expression
                exprEnd = _pos;
                formatStart = _pos + 1;
            }
            _pos++;
        }

        if (_pos >= _end) return false;

        int close = _pos;
        if (exprEnd < 0) exprEnd = close;
        string source = _text[exprStart..exprEnd];
        _pos = close + 1;

        if (string.IsNullOrWhiteSpace(source))
        {
            Report(Templates.BadLiteral(_src.Span(open, _pos - open), "An interpolation with nothing in it. Spooky, but useless.",
                "Put an expression between the braces, or write \\{ for a literal brace."));
            return true;
        }

        string? format = formatStart >= 0 ? _text[formatStart..close] : null;
        SourceSpan? formatSpan = formatStart >= 0 ? _src.Span(formatStart, close - formatStart) : null;
        parts.Add(new ExprPart(source, exprStart, _src.Span(exprStart, exprEnd - exprStart), format, formatSpan));
        return true;
    }

    // Skips a string literal nested inside an interpolation, returning the offset just past its closing quote.
    private int SkipNestedString(int p)
    {
        p++;
        while (p < _end)
        {
            char c = _text[p];
            if (c == '\\') p += 2;
            else if (c == '"') return p + 1;
            else if (c == '{') p = SkipNestedInterpolation(p + 1);
            else p++;
        }
        return _end;
    }

    private int SkipNestedInterpolation(int p)
    {
        int nest = 0;
        while (p < _end)
        {
            char c = _text[p];
            if (c == '"') { p = SkipNestedString(p); continue; }
            if (c == '{') nest++;
            else if (c == '}')
            {
                if (nest == 0) return p + 1;
                nest--;
            }
            p++;
        }
        return _end;
    }

    // c"a": cursor is on the opening quote; `start` is the offset of the 'c'.
    private Token ScanChar(int start)
    {
        _pos++; // opening quote
        var sb = new StringBuilder();
        bool terminated = false;
        while (_pos < _end)
        {
            char c = _text[_pos];
            if (c == '"') { terminated = true; _pos++; break; }
            if (c == '\n') break;
            if (c == '\\') ReadEscape(sb);
            else { sb.Append(c); _pos++; }
        }

        if (!terminated)
        {
            Report(Templates.UnterminatedString(_src.Span(start, 2)));
            return Make(TokenKind.Char, start, 0);
        }

        int count = 0, first = 0;
        foreach (var rune in sb.ToString().EnumerateRunes())
        {
            if (count == 0) first = rune.Value;
            count++;
        }
        if (count != 1)
        {
            Report(Templates.BadLiteral(_src.Span(start, _pos - start),
                $"A char literal holds exactly one character, and this one holds {count}.",
                "Use c\"a\" for a single character, or a \"string\" for several."));
        }
        return Make(TokenKind.Char, start, first);
    }
}
