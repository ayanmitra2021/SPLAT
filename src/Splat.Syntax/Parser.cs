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

    /// <summary>Names that may be called without parentheses as a statement: <c>print "hi {name}"</c>.</summary>
    private static readonly HashSet<string> CommandNames = new(StringComparer.Ordinal) { "print", "eprint", "readl" };

    private readonly SourceText _src;
    private readonly DiagnosticEngine _diag;
    private readonly List<Token> _t;
    private int _p;
    private int _depth;
    private int _lastErrorOffset = -1;
    private int _syntheticLoopVars;

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
        TokenKind.Var or TokenKind.Fn or TokenKind.Return or TokenKind.If or TokenKind.Loop or TokenKind.Break
        or TokenKind.Continue or TokenKind.Pick or TokenKind.Class or TokenKind.Trait or TokenKind.Extend or TokenKind.Enum
        or TokenKind.Try or TokenKind.Arena or TokenKind.Drop or TokenKind.Import or TokenKind.Network or TokenKind.Train
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
    /// (<c>;</c>, <c>elif</c>, <c>el</c>, end of file). An identifier that starts a new line also counts.
    /// </summary>
    private void Synchronize()
    {
        while (!AtEnd)
        {
            var k = Cur.Kind;
            if (k is TokenKind.Semicolon or TokenKind.Elif or TokenKind.Else or TokenKind.Catch) return;
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
                case TokenKind.Var: return ParseVar();
                case TokenKind.Fn: return ParseFn();
                case TokenKind.Return: return ParseReturn();
                case TokenKind.Rip: return ParseRip();
                case TokenKind.If: return ParseIf();
                case TokenKind.Loop: return ParseLoop();
                case TokenKind.Break: Advance(); return new BreakStmt(t.Span);
                case TokenKind.Continue: Advance(); return new ContinueStmt(t.Span);
                case TokenKind.Elif or TokenKind.Else:
                    return ParseOrphanBranch();
                case TokenKind.Enum:
                    return ParseEnum();
                case TokenKind.Pick:
                    return ParseExpressionStatement(); // pick is a prefix expression
                case TokenKind.Class or TokenKind.Trait or TokenKind.Extend
                    or TokenKind.Try or TokenKind.Arena or TokenKind.Drop or TokenKind.Import or TokenKind.Network
                    or TokenKind.Train:
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

    private Stmt ParseVar()
    {
        var kw = Advance();
        var name = Expect(TokenKind.Ident, "a variable name after 'var'");
        TypeRef? type = null;
        if (Match(TokenKind.DoubleColon)) type = ParseType();
        if (At(TokenKind.ColonAssign)) throw Fail(Cur, "'=' ('var' variables use '=', not ':=')");
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

        // Statement-form call without parentheses: `print "Got {val}", x`.
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

        // Tuple destructuring: (a, b) := expr
        if (expr is TupleExpr te && At(TokenKind.ColonAssign))
        {
            Advance();
            var names = new List<string>();
            foreach (var item in te.Items)
            {
                if (item is NameExpr ne) names.Add(ne.Name);
                else { _diag.Report(Templates.UnexpectedToken(item.Span, item.GetType().Name, "a variable name in a tuple destructure")); names.Add("_"); }
            }
            var value = ParseExpr();
            return new TupleBindStmt(names, value, false, SpanFrom(start));
        }

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
            TokenKind.String or TokenKind.Int or TokenKind.Float or TokenKind.Char or TokenKind.Ident or TokenKind.True
            or TokenKind.False or TokenKind.Self or TokenKind.Super or TokenKind.Bang or TokenKind.Amp or TokenKind.Tilde;

    // ---- ret / rip ----

    /// <summary>An optional trailing expression must start on the same line as its keyword.</summary>
    private bool ValueFollowsOnSameLine() => !Cur.NewlineBefore && CanStartExpr(Cur);

    private Stmt ParseReturn()
    {
        var kw = Advance();
        Expr? value = ValueFollowsOnSameLine() ? ParseExpr() : null;
        return new ReturnStmt(value, SpanFrom(kw));
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

    /// <summary>Statements up to (not including) ';', end of file, and optionally 'elif'/'el'.</summary>
    private List<Stmt> ParseStatements(bool stopAtBranch)
    {
        var stmts = new List<Stmt>();
        while (!AtEnd && !At(TokenKind.Semicolon))
        {
            if (stopAtBranch && Cur.Kind is TokenKind.Elif or TokenKind.Else) break;
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

    // ---- if / elif / el ----

    private Stmt ParseIf()
    {
        var kw = Advance();
        var branches = new List<IfBranch>();
        Block? elseBlock = null;
        Token lastOpener;

        var (cond, opener) = ParseHeaderThenOpen("if", "condition");
        lastOpener = opener;
        var body = MakeBlock(opener, ParseStatements(stopAtBranch: true));
        branches.Add(new IfBranch(cond, body, _src.Join(kw.Span, body.Span)));

        while (Cur.Kind is TokenKind.Elif or TokenKind.Else)
        {
            var bk = Advance();
            if (bk.Kind == TokenKind.Elif)
            {
                var (c, o) = ParseHeaderThenOpen("elif", "condition");
                lastOpener = o;
                var b = MakeBlock(o, ParseStatements(stopAtBranch: true));
                if (elseBlock is not null) _diag.Report(Templates.OrphanBranch(bk.Span, "elif"));
                else branches.Add(new IfBranch(c, b, _src.Join(bk.Span, b.Span)));
            }
            else
            {
                var o = ExpectBlockOpen("'el' block");
                lastOpener = o;
                var b = MakeBlock(o, ParseStatements(stopAtBranch: true));
                if (elseBlock is not null) _diag.Report(Templates.OrphanBranch(bk.Span, "el"));
                else elseBlock = b;
            }
        }

        CloseBlock(lastOpener);
        return new IfStmt(branches, elseBlock, SpanFrom(kw));
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

    /// <summary>An 'elif'/'el' with no 'if': report it, then swallow its header, body and closing ';' so no cascade follows.</summary>
    private Stmt ParseOrphanBranch()
    {
        var kw = Advance();
        _diag.Report(Templates.OrphanBranch(kw.Span, kw.Text));
        if (kw.Kind == TokenKind.Elif) SkipToBlockOpen();
        if (Match(TokenKind.Colon))
        {
            ParseStatements(stopAtBranch: true);
            if (!At(TokenKind.Elif) && !At(TokenKind.Else)) Match(TokenKind.Semicolon);
        }
        return new ErrorStmt(SpanFrom(kw));
    }

    // ---- loop ----

    private Stmt ParseLoop()
    {
        var kw = Advance();
        if (At(TokenKind.Colon))
        {
            var body = ParseBlock("'loop' block");
            return new LoopStmt(LoopKind.Forever, null, null, body, SpanFrom(kw));
        }
        if (Match(TokenKind.While))
        {
            var (cond, opener) = ParseHeaderThenOpen("loop while", "condition");
            return FinishLoop(kw, LoopKind.While, null, cond, opener);
        }
        if (At(TokenKind.LParen))
        {
            // `loop (k, v) in m:` is the same as `loop k, v in m:`.
            var open = Advance();
            var vars = new List<string>();
            do vars.Add(Expect(TokenKind.Ident, "a loop variable name").Text); while (Match(TokenKind.Comma));
            Expect(TokenKind.RParen, "')' to close the loop variables");
            Expect(TokenKind.In, "'in' after the loop variables");
            var (subject, opener) = ParseHeaderThenOpen("loop", "collection");
            return vars.Count == 1
                ? FinishLoop(kw, LoopKind.In, vars[0], subject, opener)
                : FinishMultiVarLoop(kw, vars, subject, opener, open);
        }
        if (At(TokenKind.Ident) && (PeekTok().Kind == TokenKind.In || PeekTok().Kind == TokenKind.Comma))
        {
            var v = Advance();
            // Multi-variable: loop k, v in map — desugar to loop __mv in map: (k, v) := __mv; body ;
            if (At(TokenKind.Comma))
            {
                var names = new List<string> { v.Text };
                while (Match(TokenKind.Comma))
                    names.Add(Expect(TokenKind.Ident, "a variable name after ','").Text);
                Expect(TokenKind.In, "'in' after the variable list");
                var (subject, opener) = ParseHeaderThenOpen("loop", "range");
                return FinishMultiVarLoop(kw, names, subject, opener, v);
            }
            Advance(); // 'in'
            var (subj2, opener2) = ParseHeaderThenOpen("loop", "range");
            return FinishLoop(kw, LoopKind.In, v.Text, subj2, opener2);
        }
        throw Fail(Cur, "'i in range', 'while condition' or ':' after 'loop'");
    }

    private LoopStmt FinishMultiVarLoop(Token kw, List<string> names, Expr subject, Token opener, Token firstVar)
    {
        // Desugar loop k, v in m: body ; into loop __mv in m: (k, v) := __mv; body ;
        Enter();
        try
        {
            var syntheticName = $"__mv_{_syntheticLoopVars++}";
            var stmts = ParseStatements(stopAtBranch: false);
            var nameRef = new NameExpr(syntheticName, opener.Span);
            var destructure = new TupleBindStmt(names, nameRef, false, opener.Span);
            var allStmts = new List<Stmt> { destructure };
            allStmts.AddRange(stmts);
            var block = MakeBlock(opener, allStmts);
            CloseBlock(opener);
            return new LoopStmt(LoopKind.In, syntheticName, subject, block, SpanFrom(kw));
        }
        finally { Leave(); }
    }

    private LoopStmt FinishLoop(Token kw, LoopKind kind, string? variable, Expr subject, Token opener)
    {
        Enter();
        try
        {
            var stmts = ParseStatements(stopAtBranch: false);
            var block = MakeBlock(opener, stmts);
            CloseBlock(opener);
            return new LoopStmt(kind, variable, subject, block, SpanFrom(kw));
        }
        finally { Leave(); }
    }

    // ---- fn ----

    private static readonly Dictionary<TokenKind, string> OverloadSymbols = new()
    {
        [TokenKind.Plus] = "+", [TokenKind.Minus] = "-", [TokenKind.Star] = "*", [TokenKind.Slash] = "/",
        [TokenKind.Percent] = "%", [TokenKind.StarStar] = "**", [TokenKind.At] = "@", [TokenKind.EqEq] = "==",
        [TokenKind.BangEq] = "!=", [TokenKind.Lt] = "<", [TokenKind.Le] = "<=", [TokenKind.Gt] = ">", [TokenKind.Ge] = ">=",
    };

    private Stmt ParseFn()
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
            else throw Fail(Cur, "an operator symbol after 'fn op'");
            name = "op " + opSymbol;
        }
        else if (Cur.Kind is TokenKind.Ident or TokenKind.Init or TokenKind.Deinit)
        {
            name = Advance().Text;
        }
        else throw Fail(Cur, "a function name after 'fn'");

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
            return new FnDecl(name, opSymbol, generics, parms, ret, body, null, SpanFrom(kw));
        }
        if (At(TokenKind.Colon))
        {
            var block = ParseBlock($"body of '{name}'");
            return new FnDecl(name, opSymbol, generics, parms, ret, null, block, SpanFrom(kw));
        }
        throw Fail(Cur, $"'=' or ':' to start the body of '{name}'");
    }

    private Param ParseParam()
    {
        var start = Cur;
        if (At(TokenKind.Amp) || At(TokenKind.Tilde))
        {
            bool poke = Advance().Kind == TokenKind.Tilde;
            Expect(TokenKind.Self, "'self' after '&' or '~' in a parameter list");
            return new Param(poke ? ParamKind.PokeSelf : ParamKind.PeekSelf, "self", null, null, SpanFrom(start));
        }
        if (Match(TokenKind.Self)) return new Param(ParamKind.Self, "self", null, null, SpanFrom(start));

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
                string? bound = Match(TokenKind.Colon) ? Expect(TokenKind.Ident, "a trait name after ':'").Text : null;
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
            if (Match(TokenKind.Fn))
            {
                Expect(TokenKind.LParen, "'(' after 'fn' in a function type");
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

            if (At(TokenKind.LParen))
            {
                // `()` is unit, `(T)` just groups, `(A, B, ...)` is a tuple type.
                Advance();
                if (Match(TokenKind.RParen)) return new NamedType("unit", [], SpanFrom(start));
                var items = new List<TypeRef> { ParseType() };
                while (Match(TokenKind.Comma))
                {
                    if (At(TokenKind.RParen)) break;
                    items.Add(ParseType());
                }
                Expect(TokenKind.RParen, "')' to close the tuple type");
                return items.Count == 1 ? items[0] : new TupleType(items, SpanFrom(start));
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

    // ---- Phase 7: enum, pick, patterns ----

    private Stmt ParseEnum()
    {
        var kw = Advance(); // 'enum'
        var name = Expect(TokenKind.Ident, "an enum name after 'enum'");
        var generics = At(TokenKind.Lt) ? ParseGenericParams() : (List<GenericParam>)[];
        var opener = ExpectBlockOpen("'enum' body");
        var variants = new List<EnumVariant>();
        while (!At(TokenKind.Semicolon) && !AtEnd)
        {
            var vname = Expect(TokenKind.Ident, "a variant name");
            var payloads = new List<TypeRef>();
            if (Match(TokenKind.LParen))
            {
                if (!At(TokenKind.RParen))
                    do { payloads.Add(ParseType()); } while (Match(TokenKind.Comma));
                Expect(TokenKind.RParen, "')' to close the variant payloads");
            }
            variants.Add(new EnumVariant(vname.Text, payloads, SpanFrom(vname)));
            if (!Match(TokenKind.Comma)) break;
        }
        CloseBlock(opener);
        return new EnumDecl(name.Text, generics, variants, SpanFrom(kw));
    }

    private Expr ParsePick()
    {
        var kw = Advance(); // 'pick'
        var subject = ParseExpr();
        var opener = ExpectBlockOpen("'pick' arms");
        var arms = new List<PickArm>();
        Enter();
        try
        {
            while (!At(TokenKind.Semicolon) && !AtEnd)
            {
                int before = _p;
                var armStart = Cur;
                try
                {
                    var pat = ParsePattern();
                    if (At(TokenKind.Colon))
                    {
                        // Block arm: `Pat: statements ;`. The block closes with its own ';'; a comma after it is optional.
                        var bodyOpen = Advance();
                        var block = MakeBlock(bodyOpen, ParseStatements(stopAtBranch: false));
                        CloseBlock(bodyOpen);
                        arms.Add(new PickArm(pat, null, block, SpanFrom(armStart)));
                        Match(TokenKind.Comma);
                        continue;
                    }
                    Expect(TokenKind.FatArrow, "'=>' (or ':' to open a block) after the pattern");
                    var body = ParseArmBody();
                    arms.Add(new PickArm(pat, body, null, SpanFrom(armStart)));
                }
                catch (ParseAbort)
                {
                    SkipArm();
                    if (_p == before && !At(TokenKind.Semicolon) && !AtEnd) Advance();
                }

                if (Match(TokenKind.Comma)) continue;
                if (At(TokenKind.Semicolon) || AtEnd) break;
                // Arms must be separated by commas; report it once and keep reading arms.
                _diag.Report(Templates.UnexpectedToken(Cur.Span, Describe(Cur), "',' between 'pick' arms (or ';' to close the 'pick')"));
                _lastErrorOffset = Cur.Offset;
                if (_p == before) Advance();
            }
        }
        finally { Leave(); }
        CloseBlock(opener);
        return new PickExpr(subject, arms, SpanFrom(kw));
    }

    /// <summary>
    /// An arm's expression. A prelude command call (<c>print "Got {v}"</c>) is allowed, as in the spec's own example, but
    /// takes exactly one argument because the ',' after it separates arms.
    /// </summary>
    private Expr ParseArmBody()
    {
        if (Cur.Kind == TokenKind.Ident && CommandNames.Contains(Cur.Text) && StartsCommandArgument(PeekTok()))
        {
            var name = Advance();
            var arg = ParseExpr();
            return new CallExpr(new NameExpr(name.Text, name.Span), [new Arg(null, arg, arg.Span)], SpanFrom(name));
        }
        return ParseExpr();
    }

    /// <summary>Skips to the ',' or ';' that ends the current arm, ignoring those inside brackets.</summary>
    private void SkipArm()
    {
        int depth = 0;
        while (!AtEnd)
        {
            var k = Cur.Kind;
            if (depth == 0 && k is TokenKind.Comma or TokenKind.Semicolon) return;
            if (k is TokenKind.LParen or TokenKind.LBracket) depth++;
            else if (k is TokenKind.RParen or TokenKind.RBracket && depth > 0) depth--;
            Advance();
        }
    }

    /// <summary>An integer, char or negated-integer/float literal usable as a pattern or range bound.</summary>
    private bool TryParsePoint(out Expr point)
    {
        var t = Cur;
        point = null!;
        switch (t.Kind)
        {
            case TokenKind.Int:
                Advance();
                point = new IntLiteral((ulong)t.Value!, t.Span);
                return true;
            case TokenKind.Char:
                Advance();
                point = new CharLiteral((int)t.Value!, t.Span);
                return true;
            case TokenKind.Minus when PeekTok().Kind is TokenKind.Int or TokenKind.Float:
            {
                Advance();
                var n = Advance();
                Expr lit = n.Kind == TokenKind.Int ? new IntLiteral((ulong)n.Value!, n.Span) : new FloatLiteral((double)n.Value!, n.Span);
                point = new UnaryExpr(TokenKind.Minus, lit, SpanFrom(t));
                return true;
            }
            default:
                return false;
        }
    }

    private Pattern ParsePattern()
    {
        var t = Cur;

        // Wildcard.
        if (t.Kind == TokenKind.Ident && t.Text == "_")
        {
            Advance();
            return new WildcardPat(t.Span);
        }
        if (t.Kind == TokenKind.True) { Advance(); return new LiteralPat(new BoolLiteral(true, t.Span), t.Span); }
        if (t.Kind == TokenKind.False) { Advance(); return new LiteralPat(new BoolLiteral(false, t.Span), t.Span); }

        // Integer / char / negative literal, possibly the start of a range pattern.
        if (TryParsePoint(out var lo))
        {
            if (At(TokenKind.DotDot) || At(TokenKind.DotDotEq))
            {
                bool inclusive = Advance().Kind == TokenKind.DotDotEq;
                if (!TryParsePoint(out var hi)) throw Fail(Cur, "an integer or char after '..' in a range pattern");
                return new RangePat(lo, hi, inclusive, SpanFrom(t));
            }
            return new LiteralPat(lo, SpanFrom(t));
        }
        if (t.Kind == TokenKind.Float) { Advance(); return new LiteralPat(new FloatLiteral((double)t.Value!, t.Span), t.Span); }
        if (t.Kind == TokenKind.String) { Advance(); return new LiteralPat(ParseTemplate(t), t.Span); }

        // `()` is the unit literal; `(a, b)` is a tuple pattern; `(p)` just groups.
        if (t.Kind == TokenKind.LParen && PeekTok().Kind == TokenKind.RParen)
        {
            Advance();
            Advance();
            return new LiteralPat(new UnitLiteral(SpanFrom(t)), SpanFrom(t));
        }
        if (t.Kind == TokenKind.LParen)
        {
            Advance();
            Enter();
            try
            {
                var first = ParsePattern();
                if (At(TokenKind.Comma))
                {
                    var items = new List<Pattern> { first };
                    while (Match(TokenKind.Comma))
                    {
                        if (At(TokenKind.RParen)) break;
                        items.Add(ParsePattern());
                    }
                    Expect(TokenKind.RParen, "')' to close the tuple pattern");
                    return new TuplePat(items, SpanFrom(t));
                }
                Expect(TokenKind.RParen, "')'");
                return first;
            }
            finally { Leave(); }
        }

        // A variant (`Got(x)`, `Nada`) or a binding. Convention: a capitalised name is a variant, anything else binds.
        if (t.Kind == TokenKind.Ident)
        {
            Advance();
            if (At(TokenKind.LParen))
            {
                Advance();
                Enter();
                try
                {
                    var bindings = new List<Pattern>();
                    while (!At(TokenKind.RParen))
                    {
                        bindings.Add(ParsePattern());
                        if (!Match(TokenKind.Comma)) break;
                    }
                    Expect(TokenKind.RParen, "')' to close the variant pattern");
                    return new VariantPat(null, t.Text, bindings, SpanFrom(t));
                }
                finally { Leave(); }
            }
            if (char.IsUpper(t.Text[0])) return new VariantPat(null, t.Text, [], t.Span);
            return new BindPat(t.Text, t.Span);
        }
        throw Fail(t, "a pattern (_, a variant like Got(x), a name to bind, or a literal)");
    }

    // ---- later-milestone constructs ----

    private Stmt ParseReserved()
    {
        var kw = Advance();
        int phase = kw.Kind switch
        {
            TokenKind.Class or TokenKind.Drop => 8,
            TokenKind.Trait or TokenKind.Extend => 9,
            TokenKind.Try => 10,
            TokenKind.Network or TokenKind.Train => 16,
            TokenKind.Arena => 17,
            _ => 11, // imp: external packages
        };
        _diag.Report(Templates.NotYetBuilt(kw.Span, kw.Text, phase));

        // Statements without a block: skip the rest of the line.
        if (kw.Kind is TokenKind.Import or TokenKind.Drop)
        {
            while (!AtEnd && !Cur.NewlineBefore) Advance();
            return new ReservedStmt(kw.Text, SpanFrom(kw));
        }

        // Skip the header up to its ':' (a ':' inside <...> is a trait bound, not the block opener).
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
        bool chainColon = false; // the ':' after elif/el/catch continues a block instead of opening one
        while (!AtEnd)
        {
            var k = Cur.Kind;
            if (k is TokenKind.Elif or TokenKind.Else or TokenKind.Catch) chainColon = true;
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
