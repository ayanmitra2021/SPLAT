using Splat.Core;

namespace Splat.Syntax;

// Pratt expression parser. The binary table follows spec 8.1 (levels 5-11); Phase 4 adds lambdas, norms, calculus,
// reductions, regress/classify and the data-engine forms on top of this skeleton.
public sealed partial class Parser
{
    // Binding powers (higher binds tighter). Gaps leave room for the spec's other levels.
    private const int BpPostfix = 120;  // level 1-2: ' '' () [] .
    private const int BpPower = 100;    // level 3: **  (right-associative, tighter than unary minus)
    private const int BpUnary = 90;     // level 4: - ! & ~
    private const int BpHadamard = 80;  // level 5: @ .* ./ · \
    private const int BpMul = 70;       // level 6: * / % .+ .-
    private const int BpAdd = 60;       // level 7: + -
    private const int BpRange = 50;     // level 8: .. ..=  (non-associative)
    private const int BpBy = 48;        // `a..b by n`: looser than ranges, tighter than `then`
    private const int BpThen = 45;      // `a then b`: looser than ranges/by, tighter than `in` and comparisons
    private const int BpCompare = 40;   // level 9 (also `x in S`)
    private const int BpAnd = 32;       // level 10: 'and' binds tighter than 'or' (decision recorded in docs/decisions.md)
    private const int BpOr = 30;
    private const int BpPipe = 20;      // level 11: |> >>

    internal enum Assoc { Left, Right, None }

    /// <summary>Binary operator table shared by the parser and the source formatter.</summary>
    internal static bool TryGetBinary(TokenKind k, out int bp, out Assoc assoc)
    {
        assoc = Assoc.Left;
        switch (k)
        {
            case TokenKind.StarStar: bp = BpPower; assoc = Assoc.Right; return true;
            case TokenKind.At or TokenKind.DotStar or TokenKind.DotSlash or TokenKind.MiddleDot or TokenKind.Backslash:
                bp = BpHadamard; return true;
            case TokenKind.Star or TokenKind.Slash or TokenKind.Percent or TokenKind.DotPlus or TokenKind.DotMinus:
                bp = BpMul; return true;
            case TokenKind.Plus or TokenKind.Minus: bp = BpAdd; return true;
            case TokenKind.DotDot or TokenKind.DotDotEq: bp = BpRange; assoc = Assoc.None; return true;
            case TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge or TokenKind.EqEq or TokenKind.BangEq
                or TokenKind.In:
                bp = BpCompare; return true;
            case TokenKind.And: bp = BpAnd; return true;
            case TokenKind.Or: bp = BpOr; return true;
            case TokenKind.PipeGreater or TokenKind.GreaterGreater: bp = BpPipe; return true;
            default: bp = 0; return false;
        }
    }

    internal static int PrecBy => BpBy;
    internal static int PrecThen => BpThen;
    internal static int PrecRange => BpRange;
    internal static int PrecUnary => BpUnary;
    internal static int PrecPostfix => BpPostfix;
    internal static int PrecReduceBody => BpAdd;

    private static bool CanStartExpr(Token t) => t.Kind is
        TokenKind.Int or TokenKind.Float or TokenKind.String or TokenKind.Char or TokenKind.Temporal or TokenKind.Ident or TokenKind.True
        or TokenKind.False or TokenKind.Self or TokenKind.Super or TokenKind.LParen or TokenKind.LBracket
        or TokenKind.Minus or TokenKind.Bang or TokenKind.Amp or TokenKind.Tilde or TokenKind.Pipe
        or TokenKind.Pick or TokenKind.Grad or TokenKind.Intg or TokenKind.Sum or TokenKind.Prod
        or TokenKind.Regress or TokenKind.Classify;

    private Expr ParseExpr(int minBp = 0)
    {
        Enter();
        try
        {
            return ParseInfix(ParsePrefix(), minBp);
        }
        finally { Leave(); }
    }

    /// <summary>`by` and `then` are contextual: ordinary identifiers everywhere except right after an expression on the same line.</summary>
    private static bool IsWord(Token t, string word) =>
        t.Kind == TokenKind.Ident && !t.NewlineBefore && t.Text == word;

    /// <summary>The operator loop of the Pratt parser, continuing from an already-parsed left operand.</summary>
    private Expr ParseInfix(Expr left, int minBp)
    {
        while (true)
        {
            var t = Cur;
            switch (t.Kind)
            {
                // A '(' or '[' that starts a new line begins a new statement instead of calling or indexing the value
                // before it, so `(a, b) := f()` and `[1, 2].push(3)` can start a line.
                case TokenKind.LParen when !t.NewlineBefore:
                    left = ParseCall(left);
                    continue;
                case TokenKind.LBracket when !t.NewlineBefore:
                    left = ParseIndex(left);
                    continue;
                case TokenKind.Dot:
                {
                    Advance();
                    var name = At(TokenKind.Init) || At(TokenKind.Deinit) ? Advance() : Expect(TokenKind.Ident, "a member name after '.'");
                    left = new MemberExpr(left, name.Text, name.Span, _src.Join(left.Span, name.Span));
                    continue;
                }
                case TokenKind.Transpose or TokenKind.ConjTranspose:
                    Advance();
                    left = new PostfixExpr(t.Kind, left, _src.Join(left.Span, t.Span));
                    continue;
                case TokenKind.In when BpCompare >= minBp:
                {
                    Advance();
                    var set = ParseMembershipSet();
                    left = new BinaryExpr(TokenKind.In, left, set, _src.Join(left.Span, set.Span));
                    continue;
                }
            }

            if (IsWord(t, "by") && BpBy >= minBp)
            {
                if (left is not BinaryExpr { Op: TokenKind.DotDot or TokenKind.DotDotEq })
                    throw Fail(t, "a range ('a..b') before 'by'; only a range can be stepped");
                Advance();
                var step = ParseExpr(BpRange + 1);
                left = new StepExpr(left, step, _src.Join(left.Span, step.Span));
                continue;
            }

            if (IsWord(t, "then") && BpThen >= minBp)
            {
                Advance();
                var next = ParseExpr(BpThen + 1);
                var parts = left is ChainExpr c ? [.. c.Parts, next] : new List<Expr> { left, next };
                left = new ChainExpr(parts, _src.Join(parts[0].Span, next.Span));
                continue;
            }

            if (!TryGetBinary(t.Kind, out int bp, out var assoc) || bp < minBp) break;
            Advance();
            var right = ParseExpr(assoc == Assoc.Right ? bp : bp + 1);
            left = new BinaryExpr(t.Kind, left, right, _src.Join(left.Span, right.Span));

            if (assoc == Assoc.None && Cur.Kind is TokenKind.DotDot or TokenKind.DotDotEq)
                throw Fail(Cur, "an operator that can follow a range (ranges do not chain; use parentheses)");
        }
        return left;
    }

    /// <summary>
    /// The right side of <c>in</c>. A parenthesised comma list <c>(a, b, c)</c> is the membership list, legal only here;
    /// a single parenthesised expression is ordinary grouping.
    /// </summary>
    private Expr ParseMembershipSet()
    {
        Enter();
        try
        {
            if (!At(TokenKind.LParen)) return ParseExpr(BpCompare + 1);
            var open = Advance();
            if (Match(TokenKind.RParen)) return ParseInfix(new UnitLiteral(SpanFrom(open)), BpCompare + 1);
            var first = ParseExpr();
            if (At(TokenKind.Comma))
            {
                var items = new List<Expr> { first };
                while (Match(TokenKind.Comma))
                {
                    if (At(TokenKind.RParen)) break;
                    items.Add(ParseExpr());
                }
                Expect(TokenKind.RParen, "')' to close the list after 'in'");
                return ParseInfix(new ParenListExpr(items, SpanFrom(open)), BpCompare + 1);
            }
            Expect(TokenKind.RParen, "')' to close the parenthesis");
            return ParseInfix(first, BpCompare + 1);
        }
        finally { Leave(); }
    }

    private Expr ParsePrefix()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Int:
                Advance();
                return new IntLiteral((ulong)t.Value!, t.Span);
            case TokenKind.Float:
                Advance();
                return new FloatLiteral((double)t.Value!, t.Span);
            case TokenKind.Char:
                Advance();
                return new CharLiteral((int)t.Value!, t.Span);
            case TokenKind.Temporal:
            {
                Advance();
                var tv = (TemporalValue)t.Value!;
                return new TemporalLiteral(tv.Kind, tv.Value, t.Span);
            }
            case TokenKind.True:
                Advance();
                return new BoolLiteral(true, t.Span);
            case TokenKind.False:
                Advance();
                return new BoolLiteral(false, t.Span);
            case TokenKind.String:
                Advance();
                return ParseTemplate(t);
            case TokenKind.Ident or TokenKind.Self or TokenKind.Super:
                Advance();
                return new NameExpr(t.Text, t.Span);
            case TokenKind.Pipe:
                return AtNormOpen() ? ParseNorm() : ParseLambda();
            case TokenKind.Grad when PeekTok().Kind == TokenKind.LParen:
                return ParseDeriv();
            case TokenKind.Intg when PeekTok().Kind == TokenKind.LBracket:
                return ParseInteg();
            case TokenKind.Sum or TokenKind.Prod when PeekTok().Kind == TokenKind.LBracket:
                return ParseReduce();
            case TokenKind.Regress or TokenKind.Classify:
                return ParseFit();
            case TokenKind.Grad or TokenKind.Intg or TokenKind.Sum or TokenKind.Prod:
                // Not followed by '(' / '[': an ordinary name, so `sum(v)` and `map(xs, sum)` work.
                Advance();
                return new NameExpr(t.Text, t.Span);
            case TokenKind.Minus or TokenKind.Bang or TokenKind.Amp or TokenKind.Tilde:
            {
                Advance();
                var operand = ParseExpr(BpUnary);
                return new UnaryExpr(t.Kind, operand, _src.Join(t.Span, operand.Span));
            }
            case TokenKind.Pick:
                return ParsePick();
            case TokenKind.LParen:
            {
                Advance();
                if (Match(TokenKind.RParen)) return new UnitLiteral(SpanFrom(t));
                var inner = ParseExpr();
                // Tuple detection: (a, b, ...) with two or more items
                if (At(TokenKind.Comma))
                {
                    var items = new List<Expr> { inner };
                    while (Match(TokenKind.Comma))
                    {
                        if (At(TokenKind.RParen)) break; // trailing comma
                        items.Add(ParseExpr());
                    }
                    Expect(TokenKind.RParen, "')' to close the tuple");
                    return new TupleExpr(items, SpanFrom(t));
                }
                Expect(TokenKind.RParen, "')' to close the parenthesis");
                return inner;
            }
            case TokenKind.LBracket:
            {
                Advance();
                var items = new List<Expr>();
                while (!At(TokenKind.RBracket))
                {
                    items.Add(ParseExpr());
                    if (!Match(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RBracket, "']' to close the list");
                return new ListLiteral(items, SpanFrom(t));
            }
            default:
                throw Fail(t, "an expression");
        }
    }

    private Expr ParseCall(Expr callee)
    {
        Advance(); // '('
        var args = ParseArgs(TokenKind.RParen);
        Expect(TokenKind.RParen, "')' to close the call");
        return new CallExpr(callee, args, SpanFrom(callee.Span));
    }

    /// <summary>Comma-separated positional or named (<c>name = value</c>) arguments, up to (not including) <paramref name="close"/>.</summary>
    private List<Arg> ParseArgs(TokenKind close)
    {
        var args = new List<Arg>();
        while (!At(close))
        {
            var start = Cur;
            if (At(TokenKind.Ident) && PeekTok().Kind == TokenKind.Assign)
            {
                Advance();
                Advance();
                var v = ParseExpr();
                args.Add(new Arg(start.Text, v, SpanFrom(start)));
            }
            else
            {
                var v = ParseExpr();
                // `key => value`: a pair, used by map(...)
                if (At(TokenKind.FatArrow))
                {
                    Advance();
                    var pv = ParseExpr();
                    v = new PairExpr(v, pv, _src.Join(v.Span, pv.Span));
                }
                args.Add(new Arg(null, v, v.Span));
            }
            if (!Match(TokenKind.Comma)) break;
        }
        return args;
    }

    // ---- lambdas and norms (both start with '|') ----

    /// <summary>
    /// Prefix '|' rule: two bars with no whitespace between them open a norm (<c>||v||</c>); otherwise the bar opens a
    /// lambda (<c>|x| e</c>). A lambda with no parameters is written with a space: <c>| | e</c>.
    /// </summary>
    private bool AtNormOpen() =>
        At(TokenKind.Pipe) && PeekTok() is { Kind: TokenKind.Pipe, SpaceBefore: false, NewlineBefore: false };

    private Expr ParseLambda()
    {
        var open = Advance(); // '|'
        var ps = new List<LambdaParam>();
        while (!At(TokenKind.Pipe))
        {
            var n = Expect(TokenKind.Ident, "a lambda parameter name or the closing '|'");
            TypeRef? pt = Match(TokenKind.DoubleColon) ? ParseType() : null;
            ps.Add(new LambdaParam(n.Text, pt, pt is null ? n.Span : _src.Join(n.Span, pt.Span)));
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.Pipe, "'|' to close the lambda parameters");
        var body = ParseExpr();
        return new LambdaExpr(ps, body, _src.Join(open.Span, body.Span));
    }

    private Expr ParseNorm()
    {
        var open = Advance();
        Advance(); // the two opening bars
        var inner = ParseExpr();
        if (!AtNormOpen()) throw Fail(Cur, "'||' to close the norm");
        Advance();
        var close = Advance();

        var kind = NormKind.L2;
        var s = Cur;
        if (s.Kind == TokenKind.Ident && !s.SpaceBefore && !s.NewlineBefore && s.Text.StartsWith('_'))
        {
            kind = s.Text switch
            {
                "_1" => NormKind.L1,
                "_2" => NormKind.L2,
                "_inf" => NormKind.LInf,
                "_F" => NormKind.Frobenius,
                _ => throw Fail(s, "a norm kind: '_1', '_2', '_inf' or '_F'"),
            };
            close = Advance();
        }
        return new NormExpr(inner, kind, _src.Join(open.Span, close.Span));
    }

    // ---- calculus, reductions, AI ----

    /// <summary>∂(f, x) / grad(f, x). Without the '(' it is an ordinary name.</summary>
    private Expr ParseDeriv()
    {
        var kw = Advance();
        Expect(TokenKind.LParen, "'(' after 'grad'");
        var fn = ParseExpr();
        Expect(TokenKind.Comma, "',' and the variable to differentiate by");
        var wrt = ParseExpr();
        Expect(TokenKind.RParen, "')' to close grad(...)");
        return new DerivExpr(fn, wrt, SpanFrom(kw));
    }

    /// <summary>∫[a..b, c..d, tol = 1e-8] |x, y| body</summary>
    private Expr ParseInteg()
    {
        var kw = Advance();
        Advance(); // '['
        var ranges = new List<Expr>();
        var options = new List<Arg>();
        while (!At(TokenKind.RBracket))
        {
            var start = Cur;
            if (At(TokenKind.Ident) && PeekTok().Kind == TokenKind.Assign)
            {
                Advance();
                Advance();
                var v = ParseExpr();
                options.Add(new Arg(start.Text, v, SpanFrom(start)));
            }
            else
            {
                var r = ParseExpr();
                if (r is not BinaryExpr { Op: TokenKind.DotDot or TokenKind.DotDotEq })
                {
                    _diag.Report(Templates.UnexpectedToken(r.Span, "an expression", "a range like 'a..b' to integrate over"));
                    throw new ParseAbort();
                }
                ranges.Add(r);
            }
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RBracket, "']' to close the integration ranges");
        if (ranges.Count == 0) throw Fail(Prev, "at least one range like 'a..b'");
        if (!At(TokenKind.Pipe) || AtNormOpen()) throw Fail(Cur, "a lambda like '|x| expr' after the integration ranges");
        var lambda = (LambdaExpr)ParseLambda();
        return new IntegExpr(ranges, options, lambda, SpanFrom(kw));
    }

    /// <summary>∑[i in 0..N] body / ∏[...]. The body is parsed above the range/comparison levels, so it takes in <c>+ - * /</c>.</summary>
    private Expr ParseReduce()
    {
        var kw = Advance();
        Advance(); // '['
        var v = Expect(TokenKind.Ident, "a loop variable, as in 'sum[i in 0..N]'");
        Expect(TokenKind.In, "'in' after the loop variable");
        var range = ParseExpr();
        Expect(TokenKind.RBracket, "']' to close the reduction head");
        var body = ParseExpr(BpAdd);
        return new ReduceExpr(kw.Kind == TokenKind.Prod, v.Text, v.Span, range, body, SpanFrom(kw));
    }

    /// <summary>regress(cfg) fit (X, y) / classify(cfg) fit (X, y)</summary>
    private Expr ParseFit()
    {
        var kw = Advance();
        Expect(TokenKind.LParen, $"'(' after '{kw.Text}', as in '{kw.Text}(ridge = 0.05) fit (X, y)'");
        var cfg = ParseArgs(TokenKind.RParen);
        Expect(TokenKind.RParen, "')' to close the model options");
        Expect(TokenKind.Fit, "'fit' and the training data, as in 'fit (X, y)'");
        Expect(TokenKind.LParen, "'(' after 'fit'");
        var x = ParseExpr();
        Expect(TokenKind.Comma, "',' between the features and the targets");
        var y = ParseExpr();
        Expect(TokenKind.RParen, "')' to close fit (...)");
        return new FitExpr(kw.Kind == TokenKind.Classify, cfg, x, y, SpanFrom(kw));
    }

    private Expr ParseIndex(Expr target)
    {
        Advance(); // '['
        var items = new List<Expr>();
        while (!At(TokenKind.RBracket))
        {
            if (At(TokenKind.SliceColon))
            {
                var c = Advance();
                items.Add(new FullSlice(c.Span));
            }
            else items.Add(ParseExpr());
            if (At(TokenKind.SliceColon)) throw Fail(Cur, "',' or ']' (write a slice as a range: 'A[1..3]')");
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RBracket, "']' to close the index");
        if (items.Count == 0) throw Fail(Prev, "at least one index");
        return new IndexExpr(target, items, SpanFrom(target.Span));
    }

    // ---- string templates ----

    private Expr ParseTemplate(Token str)
    {
        var parts = new List<TemplatePart>();
        foreach (var part in str.Parts!)
        {
            switch (part)
            {
                case TextPart tp:
                    parts.Add(new TemplateText(tp.Value, tp.Span));
                    break;
                case ExprPart ep:
                {
                    var toks = new Lexer(_src, _diag, ep.Offset, ep.Offset + ep.Source.Length).Tokenize();
                    var sub = new Parser(_src, _diag, toks) { _depth = _depth };
                    Expr e;
                    if (toks.Count == 1)
                    {
                        _diag.Report(Templates.UnexpectedToken(ep.Span, "{}", "an expression inside the braces"));
                        e = new ErrorExpr(ep.Span);
                    }
                    else e = sub.ParseWholeExpression();
                    parts.Add(new TemplateExpr(e, ep.Format, ep.Span));
                    break;
                }
            }
        }
        return new StringTemplate(parts, str.Span);
    }
}
