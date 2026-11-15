using Splat.Core;

namespace Splat.Syntax;

/// <summary>
/// Recursive-descent parser for statements, blocks and control flow, with a Pratt expression parser
/// (<c>Parser.Expr.cs</c>). Never throws to callers: problems go to the <see cref="DiagnosticEngine"/>, the parser
/// resynchronises at the next statement and keeps going so several errors surface in one run.
/// </summary>
public sealed partial class Parser
{
    private const int MaxDepth = 200;

    /// <summary>Names that may be called without parentheses as a statement: <c>squish "hi {name}"</c>.</summary>
    private static readonly HashSet<string> CommandNames = new(StringComparer.Ordinal) { "squish", "yell", "slurp" };

    private readonly SourceText _src;
    private readonly DiagnosticEngine _diag;
    private readonly List<Token> _t;
    private int _p;
    private int _depth;
    private int _lastErrorOffset = -1;

    /// <summary>Internal panic-mode signal; always caught inside the parser.</summary>
    private sealed class ParseAbort : Exception;

    public Parser(SourceText source, DiagnosticEngine diagnostics, List<Token>? tokens = null)
    {
        _src = source;
        _diag = diagnostics;
        _t = tokens ?? new Lexer(source, diagnostics).Tokenize();
    }

    public static Program ParseProgram(SourceText source, DiagnosticEngine diagnostics) =>
        new Parser(source, diagnostics).ParseProgram();

    /// <summary>Parses the whole text as one expression (used by tests and string interpolation).</summary>
    public static Expr ParseExpression(SourceText source, DiagnosticEngine diagnostics) =>
        new Parser(source, diagnostics).ParseWholeExpression();

    // ---- token helpers ----

    private Token Cur => _t[_p];
    private Token PeekTok(int n = 1) => _t[Math.Min(_p + n, _t.Count - 1)];
    private Token Prev => _p > 0 ? _t[_p - 1] : _t[0];
    private bool At(TokenKind k) => Cur.Kind == k;
    private bool AtEnd => Cur.Kind == TokenKind.EndOfFile;

    private Token Advance()
    {
        var t = _t[_p];
        if (_p < _t.Count - 1) _p++;
        return t;
    }

    private bool Match(TokenKind k)
    {
        if (!At(k)) return false;
        Advance();
        return true;
    }

    private SourceSpan SpanFrom(Token start) => _src.Join(start.Span, Prev.Span);
    private SourceSpan SpanFrom(SourceSpan start) => _src.Join(start, Prev.Span);

    private static string Describe(Token t)
    {
        if (t.Kind == TokenKind.EndOfFile) return "end of file";
        var text = t.Text.Replace("\n", "\\n");
        return text.Length > 24 ? text[..24] + "..." : text;
    }

    private ParseAbort Fail(Token at, string expected)
    {
        _diag.Report(Templates.UnexpectedToken(at.Span, Describe(at), expected));
        _lastErrorOffset = at.Offset;
        return new ParseAbort();
    }

    private Token Expect(TokenKind k, string expected)
    {
        if (At(k)) return Advance();
        throw Fail(Cur, expected);
    }

    private string ExpectIdent(string expected) => (string)Expect(TokenKind.Ident, expected).Text;

    private void Enter()
    {
        if (++_depth > MaxDepth)
        {
            _diag.Report(Templates.TooDeep(Cur.Span));
            _depth = 0; // let the unwinding finish without reporting again
            throw new ParseAbort();
        }
    }

    private void Leave() { if (_depth > 0) _depth--; }

    // ---- program and statements ----

    public Program ParseProgram()
    {
        var stmts = new List<Stmt>();
        while (!AtEnd)
        {
            if (At(TokenKind.Semicolon))
            {
                // A ';' that an error was just reported at is already accounted for; do not pile on.
                if (Cur.Offset != _lastErrorOffset) _diag.Report(Templates.StraySemicolon(Cur.Span));
                Advance();
                continue;
            }
            stmts.Add(ParseStatementSafe());
        }
        var span = stmts.Count == 0 ? Cur.Span : _src.Join(stmts[0].Span, stmts[^1].Span);
        return new Program(stmts, span);
    }

    private Expr ParseWholeExpression()
    {
        try
        {
            var e = ParseExpr();
            if (!AtEnd) throw Fail(Cur, "the end of the expression");
            return e;
        }
        catch (ParseAbort)
        {
            return new ErrorExpr(Cur.Span);
        }
    }

    private static bool IsStatementStart(TokenKind k) => k is
        TokenKind.Wag or TokenKind.Cook or TokenKind.Yeet or TokenKind.Hmm or TokenKind.Spin or TokenKind.Bail
        or TokenKind.Skip or TokenKind.Pick or TokenKind.Mold or TokenKind.Quirk or TokenKind.Teach or TokenKind.Mood
        or TokenKind.Yolo or TokenKind.Zone or TokenKind.Nuke or TokenKind.Yoink or TokenKind.Brain or TokenKind.Grind
        or TokenKind.Rip;

    private Stmt ParseStatementSafe()
    {
        int startPos = _p;
        int startDepth = _depth;
        var startTok = Cur;
        try
        {
            return ParseStatement();
        }
        catch (ParseAbort)
        {
            _depth = startDepth;
            Synchronize();
            if (_p == startPos) Advance(); // always make progress
            return new ErrorStmt(SpanFrom(startTok));
        }
    }

    /// <summary>
    /// Panic-mode recovery: skip to a token that can start a statement or that belongs to the enclosing block
    /// (<c>;</c>, <c>meh</c>, <c>nah</c>, end of file). An identifier that starts a new line also counts.
    /// </summary>
    private void Synchronize()
    {
        while (!AtEnd)
        {
            var k = Cur.Kind;
            if (k is TokenKind.Semicolon or TokenKind.Meh or TokenKind.Nah or TokenKind.Oops) return;
            if (IsStatementStart(k)) return;
            if (k == TokenKind.Ident && Cur.NewlineBefore && Prev.Kind != TokenKind.Comma) return;
            Advance();
        }
    }

    private Stmt ParseStatement()
    {
        Enter();
        try
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Wag: return ParseWag();
                case TokenKind.Cook: return ParseCook();
                case TokenKind.Yeet: return ParseYeet();
                case TokenKind.Rip: return ParseRip();
                case TokenKind.Hmm: return ParseHmm();
                case TokenKind.Spin: return ParseSpin();
                case TokenKind.Bail: Advance(); return new BailStmt(t.Span);
                case TokenKind.Skip: Advance(); return new SkipStmt(t.Span);
                case TokenKind.Meh or TokenKind.Nah:
                    return ParseOrphanBranch();
                case TokenKind.Pick or TokenKind.Mold or TokenKind.Quirk or TokenKind.Teach or TokenKind.Mood
                    or TokenKind.Yolo or TokenKind.Zone or TokenKind.Nuke or TokenKind.Yoink or TokenKind.Brain
                    or TokenKind.Grind:
                    return ParseReserved();
                case TokenKind.Ident:
                    if (PeekTok().Kind == TokenKind.ColonAssign || PeekTok().Kind == TokenKind.DoubleColon)
                        return ParseBind();
                    return ParseExpressionStatement();
                default:
                    return ParseExpressionStatement();
            }
        }
        finally { Leave(); }
    }

    // ---- bindings and assignment ----

    private Stmt ParseBind()
    {
        var name = Advance();
        if (Match(TokenKind.ColonAssign))
        {
            var value = ParseExpr();
            return new BindStmt(name.Text, name.Span, null, value, false, SpanFrom(name));
        }
        Expect(TokenKind.DoubleColon, "'::' or ':='");
        var type = ParseType();
        Expect(TokenKind.Assign, "'=' and a value");
        var v = ParseExpr();
        return new BindStmt(name.Text, name.Span, type, v, false, SpanFrom(name));
    }

    private Stmt ParseWag()
    {
        var kw = Advance();
        var name = Expect(TokenKind.Ident, "a variable name after 'wag'");
        TypeRef? type = null;
        if (Match(TokenKind.DoubleColon)) type = ParseType();
        if (At(TokenKind.ColonAssign)) throw Fail(Cur, "'=' ('wag' variables use '=', not ':=')");
        Expect(TokenKind.Assign, "'=' and a value");
        var value = ParseExpr();
        return new BindStmt(name.Text, name.Span, type, value, true, SpanFrom(kw));
    }

    private static bool IsAssignOp(TokenKind k) => k is
        TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign
        or TokenKind.SlashAssign or TokenKind.AtAssign or TokenKind.DotStarAssign or TokenKind.DotSlashAssign;

    private Stmt ParseExpressionStatement()
    {
        var start = Cur;

        // Statement-form call without parentheses: `squish "Got {val}", x`.
        if (start.Kind == TokenKind.Ident && CommandNames.Contains(start.Text) && StartsCommandArgument(PeekTok()))
        {
            Advance();
            var args = new List<Arg>();
            do
            {
                var e = ParseExpr();
                args.Add(new Arg(null, e, e.Span));
            } while (Match(TokenKind.Comma));
            var callee = new NameExpr(start.Text, start.Span);
            var call = new CallExpr(callee, args, SpanFrom(start));
            return new ExprStmt(call, call.Span);
        }

        var expr = ParseExpr();
        if (IsAssignOp(Cur.Kind))
        {
            if (expr is not (NameExpr or IndexExpr or MemberExpr))
            {
                _diag.Report(Templates.NotAssignable(expr.Span));
                throw new ParseAbort();
            }
            var op = Advance();
            var value = ParseExpr();
            return new AssignStmt(expr, op.Kind, value, SpanFrom(start));
        }
        return new ExprStmt(expr, expr.Span);
    }

    /// <summary>
    /// Command-call rule: a prelude name followed, on the same line and after a space, by a token that cannot continue
    /// an expression as an operator (a literal, a name, <c>!</c>, <c>&amp;</c> or <c>~</c>). Anything else (including
    /// <c>(</c>, <c>[</c>, <c>-</c>) is parsed as an ordinary expression, so parenthesised calls always work.
    /// </summary>
    private static bool StartsCommandArgument(Token t) =>
        !t.NewlineBefore && t.SpaceBefore && t.Kind is
            TokenKind.String or TokenKind.Int or TokenKind.Float or TokenKind.Char or TokenKind.Ident or TokenKind.Yep
            or TokenKind.Nop or TokenKind.Me or TokenKind.Elder or TokenKind.Bang or TokenKind.Amp or TokenKind.Tilde;

    // ---- yeet / rip ----

    /// <summary>An optional trailing expression must start on the same line as its keyword.</summary>
    private bool ValueFollowsOnSameLine() => !Cur.NewlineBefore && CanStartExpr(Cur);

    private Stmt ParseYeet()
    {
        var kw = Advance();
        Expr? value = ValueFollowsOnSameLine() ? ParseExpr() : null;
        return new YeetStmt(value, SpanFrom(kw));
    }

    private Stmt ParseRip()
    {
        var kw = Advance();
        Expr? msg = ValueFollowsOnSameLine() ? ParseExpr() : null;
        return new RipStmt(msg, SpanFrom(kw));
    }

    // ---- blocks ----

    /// <summary>Consumes the ':' that opens a block. If it is missing, reports it and carries on as if it were there.</summary>
    private Token ExpectBlockOpen(string context)
    {
        if (At(TokenKind.Colon)) return Advance();
        _diag.Report(Templates.UnexpectedToken(Cur.Span, Describe(Cur), $"':' to open the {context}"));
        return Prev;
    }

    /// <summary>Statements up to (not including) ';', end of file, and optionally 'meh'/'nah'.</summary>
    private List<Stmt> ParseStatements(bool stopAtBranch)
    {
        var stmts = new List<Stmt>();
        while (!AtEnd && !At(TokenKind.Semicolon))
        {
            if (stopAtBranch && Cur.Kind is TokenKind.Meh or TokenKind.Nah) break;
            stmts.Add(ParseStatementSafe());
        }
        return stmts;
    }

    private Block MakeBlock(Token opener, List<Stmt> stmts) =>
        new(stmts, stmts.Count == 0 ? opener.Span : _src.Join(opener.Span, stmts[^1].Span));

    /// <summary>Consumes the closing ';', or reports 💀 OOF at the block opener when the file ended first.</summary>
    private void CloseBlock(Token opener)
    {
        if (At(TokenKind.Semicolon)) { Advance(); return; }
        _diag.Report(Templates.MissingSemicolon(opener.Span, opener.Span.Line));
    }

    private Block ParseBlock(string context)
    {
        Enter();
        try
        {
            var opener = ExpectBlockOpen(context);
            var stmts = ParseStatements(stopAtBranch: false);
            var block = MakeBlock(opener, stmts);
            CloseBlock(opener);
            return block;
        }
        finally { Leave(); }
    }

    // ---- hmm / meh / nah ----

    private Stmt ParseHmm()
    {
        var kw = Advance();
        var branches = new List<HmmBranch>();
        Block? elseBlock = null;
        Token lastOpener;

        var (cond, opener) = ParseHeaderThenOpen("hmm", "condition");
        lastOpener = opener;
        var body = MakeBlock(opener, ParseStatements(stopAtBranch: true));
        branches.Add(new HmmBranch(cond, body, _src.Join(kw.Span, body.Span)));

        while (Cur.Kind is TokenKind.Meh or TokenKind.Nah)
        {
            var bk = Advance();
            if (bk.Kind == TokenKind.Meh)
            {
                var (c, o) = ParseHeaderThenOpen("meh", "condition");
                lastOpener = o;
                var b = MakeBlock(o, ParseStatements(stopAtBranch: true));
                if (elseBlock is not null) _diag.Report(Templates.OrphanBranch(bk.Span, "meh"));
                else branches.Add(new HmmBranch(c, b, _src.Join(bk.Span, b.Span)));
            }
            else
            {
                var o = ExpectBlockOpen("'nah' block");
                lastOpener = o;
                var b = MakeBlock(o, ParseStatements(stopAtBranch: true));
                if (elseBlock is not null) _diag.Report(Templates.OrphanBranch(bk.Span, "nah"));
                else elseBlock = b;
            }
        }

        CloseBlock(lastOpener);
        return new HmmStmt(branches, elseBlock, SpanFrom(kw));
    }

    /// <summary>
    /// Parses a header expression and the ':' after it. A broken header skips ahead to its ':' so the body still
    /// parses as a block.
    /// </summary>
    private (Expr Expr, Token Opener) ParseHeaderThenOpen(string keyword, string what)
    {
        Expr expr;
        try
        {
            expr = ParseExpr();
        }
        catch (ParseAbort)
        {
            SkipToBlockOpen();
            expr = new ErrorExpr(Cur.Span);
        }
        return (expr, ExpectBlockOpen($"'{keyword}' block"));
    }

    private void SkipToBlockOpen()
    {
        while (!AtEnd && Cur.Kind is not (TokenKind.Colon or TokenKind.Semicolon) && !IsStatementStart(Cur.Kind)
               && !(Cur.NewlineBefore && Cur.Kind == TokenKind.Ident))
            Advance();
    }

    /// <summary>A 'meh'/'nah' with no 'hmm': report it, then swallow its header, body and closing ';' so no cascade follows.</summary>
    private Stmt ParseOrphanBranch()
    {
        var kw = Advance();
        _diag.Report(Templates.OrphanBranch(kw.Span, kw.Text));
        if (kw.Kind == TokenKind.Meh) SkipToBlockOpen();
        if (Match(TokenKind.Colon))
        {
            ParseStatements(stopAtBranch: true);
            if (!At(TokenKind.Meh) && !At(TokenKind.Nah)) Match(TokenKind.Semicolon);
        }
        return new ErrorStmt(SpanFrom(kw));
    }

    // ---- spin ----

    private Stmt ParseSpin()
    {
        var kw = Advance();
        if (At(TokenKind.Colon))
        {
            var body = ParseBlock("'spin' block");
            return new SpinStmt(SpinKind.Forever, null, null, body, SpanFrom(kw));
        }
        if (Match(TokenKind.While))
        {
            var (cond, opener) = ParseHeaderThenOpen("spin while", "condition");
            return FinishSpin(kw, SpinKind.While, null, cond, opener);
        }
        if (At(TokenKind.Ident) && PeekTok().Kind == TokenKind.In)
        {
            var v = Advance();
            Advance();
            var (subject, opener) = ParseHeaderThenOpen("spin", "range");
            return FinishSpin(kw, SpinKind.In, v.Text, subject, opener);
        }
        throw Fail(Cur, "'i in range', 'while condition' or ':' after 'spin'");
    }

    private SpinStmt FinishSpin(Token kw, SpinKind kind, string? variable, Expr subject, Token opener)
    {
        Enter();
        try
        {
            var stmts = ParseStatements(stopAtBranch: false);
            var block = MakeBlock(opener, stmts);
            CloseBlock(opener);
            return new SpinStmt(kind, variable, subject, block, SpanFrom(kw));
        }
        finally { Leave(); }
    }

    // ---- cook ----

    private static readonly Dictionary<TokenKind, string> OverloadSymbols = new()
    {
        [TokenKind.Plus] = "+", [TokenKind.Minus] = "-", [TokenKind.Star] = "*", [TokenKind.Slash] = "/",
        [TokenKind.Percent] = "%", [TokenKind.StarStar] = "**", [TokenKind.At] = "@", [TokenKind.EqEq] = "==",
        [TokenKind.BangEq] = "!=", [TokenKind.Lt] = "<", [TokenKind.Le] = "<=", [TokenKind.Gt] = ">", [TokenKind.Ge] = ">=",
    };

    private Stmt ParseCook()
    {
        var kw = Advance();
        string name;
        string? opSymbol = null;

        if (Cur.Kind == TokenKind.Ident && Cur.Text == "op" && PeekTok().Kind != TokenKind.LParen)
        {
            Advance();
            if (At(TokenKind.LBracket))
            {
                Advance();
                Expect(TokenKind.RBracket, "']' to finish the '[]' operator");
                opSymbol = "[]";
            }
            else if (OverloadSymbols.TryGetValue(Cur.Kind, out var sym))
            {
                Advance();
                opSymbol = sym;
            }
            else throw Fail(Cur, "an operator symbol after 'cook op'");
            name = "op " + opSymbol;
        }
        else if (Cur.Kind is TokenKind.Ident or TokenKind.Birth or TokenKind.Funeral)
        {
            name = Advance().Text;
        }
        else throw Fail(Cur, "a function name after 'cook'");

        var generics = At(TokenKind.Lt) ? ParseGenericParams() : [];

        Expect(TokenKind.LParen, $"'(' to start the parameters of '{name}'");
        var parms = new List<Param>();
        while (!At(TokenKind.RParen))
        {
            parms.Add(ParseParam());
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RParen, "')' to close the parameters");

        TypeRef? ret = Match(TokenKind.DoubleColon) ? ParseType() : null;

        if (Match(TokenKind.Assign))
        {
            var body = ParseExpr();
            return new CookDecl(name, opSymbol, generics, parms, ret, body, null, SpanFrom(kw));
        }
        if (At(TokenKind.Colon))
        {
            var block = ParseBlock($"body of '{name}'");
            return new CookDecl(name, opSymbol, generics, parms, ret, null, block, SpanFrom(kw));
        }
        throw Fail(Cur, $"'=' or ':' to start the body of '{name}'");
    }

    private Param ParseParam()
    {
        var start = Cur;
        if (At(TokenKind.Amp) || At(TokenKind.Tilde))
        {
            bool poke = Advance().Kind == TokenKind.Tilde;
            Expect(TokenKind.Me, "'me' after '&' or '~' in a parameter list");
            return new Param(poke ? ParamKind.PokeMe : ParamKind.PeekMe, "me", null, null, SpanFrom(start));
        }
        if (Match(TokenKind.Me)) return new Param(ParamKind.Me, "me", null, null, SpanFrom(start));

        var name = Expect(TokenKind.Ident, "a parameter name");
        Expect(TokenKind.DoubleColon, $"'::Type' after parameter '{name.Text}'");
        var type = ParseType();
        Expr? def = Match(TokenKind.Assign) ? ParseExpr() : null;
        return new Param(ParamKind.Normal, name.Text, type, def, SpanFrom(start));
    }

    private List<GenericParam> ParseGenericParams()
    {
        Expect(TokenKind.Lt, "'<'");
        var list = new List<GenericParam>();
        do
        {
            var start = Cur;
            if (Match(TokenKind.Dollar))
            {
                var n = Expect(TokenKind.Ident, "a dimension name after '$'");
                list.Add(new GenericParam(n.Text, true, null, SpanFrom(start)));
            }
            else
            {
                var n = Expect(TokenKind.Ident, "a generic parameter name");
                string? bound = Match(TokenKind.Colon) ? Expect(TokenKind.Ident, "a quirk name after ':'").Text : null;
                list.Add(new GenericParam(n.Text, false, bound, SpanFrom(start)));
            }
        } while (Match(TokenKind.Comma));
        ExpectCloseAngle();
        return list;
    }

    // ---- types ----

    private TypeRef ParseType()
    {
        Enter();
        try
        {
            var start = Cur;
            if (At(TokenKind.Amp) || At(TokenKind.Tilde))
            {
                bool poke = Advance().Kind == TokenKind.Tilde;
                var inner = ParseType();
                return new RefType(poke, inner, SpanFrom(start));
            }
            if (Match(TokenKind.Cook))
            {
                Expect(TokenKind.LParen, "'(' after 'cook' in a function type");
                var ps = new List<TypeRef>();
                while (!At(TokenKind.RParen))
                {
                    ps.Add(ParseType());
                    if (!Match(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RParen, "')'");
                TypeRef? r = Match(TokenKind.DoubleColon) ? ParseType() : null;
                return new FuncType(ps, r, SpanFrom(start));
            }

            var nameTok = Expect(TokenKind.Ident, "a type name");
            var args = new List<TypeRef>();
            if (Match(TokenKind.Lt))
            {
                do args.Add(ParseType()); while (Match(TokenKind.Comma));
                ExpectCloseAngle();
            }
            TypeRef type = new NamedType(nameTok.Text, args, SpanFrom(start));

            if (At(TokenKind.LBracket))
            {
                Advance();
                var dims = new List<Dim>();
                do dims.Add(ParseDim()); while (Match(TokenKind.Comma));
                Expect(TokenKind.RBracket, "']' to close the dimensions");
                type = new TensorType(type, dims, SpanFrom(start));
            }
            return type;
        }
        finally { Leave(); }
    }

    private Dim ParseDim()
    {
        var t = Cur;
        if (Match(TokenKind.Int)) return new ConstDim((ulong)t.Value!, t.Span);
        if (Match(TokenKind.Question)) return new DynDim(t.Span);
        if (Match(TokenKind.Dollar))
        {
            var n = Expect(TokenKind.Ident, "a dimension name after '$'");
            return new VarDim(n.Text, SpanFrom(t));
        }
        throw Fail(t, "a dimension: a number, '$Name' or '?'");
    }

    /// <summary>Consumes one '&gt;', splitting '&gt;&gt;' and '&gt;=' when generics close right next to another token.</summary>
    private void ExpectCloseAngle()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Gt:
                Advance();
                return;
            case TokenKind.GreaterGreater:
                SplitCurrent(TokenKind.Gt);
                return;
            case TokenKind.Ge:
                SplitCurrent(TokenKind.Assign);
                return;
            default:
                throw Fail(t, "'>' to close the generic list");
        }
    }

    /// <summary>Replaces the current two-character token by its second half, as if the first '&gt;' were consumed.</summary>
    private void SplitCurrent(TokenKind secondKind)
    {
        var t = Cur;
        var span = new SourceSpan(t.Span.File, t.Span.Line, t.Span.Col + 1, Math.Max(1, t.Span.Length - 1));
        _t[_p] = t with { Kind = secondKind, Text = t.Text[1..], Span = span, Offset = t.Offset + 1, SpaceBefore = false, NewlineBefore = false };
    }

    // ---- later-milestone constructs ----

    private Stmt ParseReserved()
    {
        var kw = Advance();
        int phase = kw.Kind switch
        {
            TokenKind.Pick or TokenKind.Mood => 7,
            TokenKind.Mold or TokenKind.Nuke => 8,
            TokenKind.Quirk or TokenKind.Teach => 9,
            TokenKind.Yolo => 10,
            TokenKind.Brain or TokenKind.Grind => 16,
            TokenKind.Zone => 17,
            _ => 11, // yoink: external packages
        };
        _diag.Report(Templates.NotYetBuilt(kw.Span, kw.Text, phase));

        // Statements without a block: skip the rest of the line.
        if (kw.Kind is TokenKind.Yoink or TokenKind.Nuke)
        {
            while (!AtEnd && !Cur.NewlineBefore) Advance();
            return new ReservedStmt(kw.Text, SpanFrom(kw));
        }

        // Skip the header up to its ':' (a ':' inside <...> is a quirk bound, not the block opener).
        int angle = 0;
        while (!AtEnd && !(At(TokenKind.Colon) && angle == 0))
        {
            if (At(TokenKind.Semicolon)) return new ReservedStmt(kw.Text, SpanFrom(kw));
            if (At(TokenKind.Lt)) angle++;
            else if (At(TokenKind.Gt) && angle > 0) angle--;
            else if (At(TokenKind.GreaterGreater) && angle > 0) angle = Math.Max(0, angle - 2);
            Advance();
        }
        if (AtEnd) return new ReservedStmt(kw.Text, SpanFrom(kw));

        var opener = Advance();
        int depth = 1;
        bool chainColon = false; // the ':' after meh/nah/oops continues a block instead of opening one
        while (!AtEnd)
        {
            var k = Cur.Kind;
            if (k is TokenKind.Meh or TokenKind.Nah or TokenKind.Oops) chainColon = true;
            else if (k == TokenKind.Colon)
            {
                if (chainColon) chainColon = false; else depth++;
            }
            else if (k == TokenKind.Semicolon && --depth == 0)
            {
                Advance();
                return new ReservedStmt(kw.Text, SpanFrom(kw));
            }
            Advance();
        }
        _diag.Report(Templates.MissingSemicolon(opener.Span, opener.Span.Line));
        return new ReservedStmt(kw.Text, SpanFrom(kw));
    }
}
