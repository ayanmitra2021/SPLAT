using Splat.Core;

namespace Splat.Syntax;

// Pratt expression parser. The binary table follows spec 8.1 (levels 5-11); Phase 4 adds lambdas, norms, calculus,
// reductions, guess/judge and the data-engine forms on top of this skeleton.
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
    private const int BpCompare = 40;   // level 9
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
            case TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge or TokenKind.EqEq or TokenKind.BangEq:
                bp = BpCompare; return true;
            case TokenKind.And: bp = BpAnd; return true;
            case TokenKind.Or: bp = BpOr; return true;
            case TokenKind.PipeGreater or TokenKind.GreaterGreater: bp = BpPipe; return true;
            default: bp = 0; return false;
        }
    }

    internal static int PrecUnary => BpUnary;
    internal static int PrecPostfix => BpPostfix;

    private static bool CanStartExpr(Token t) => t.Kind is
        TokenKind.Int or TokenKind.Float or TokenKind.String or TokenKind.Char or TokenKind.Ident or TokenKind.Yep
        or TokenKind.Nop or TokenKind.Me or TokenKind.Elder or TokenKind.LParen or TokenKind.LBracket
        or TokenKind.Minus or TokenKind.Bang or TokenKind.Amp or TokenKind.Tilde;

    private Expr ParseExpr(int minBp = 0)
    {
        Enter();
        try
        {
            var left = ParsePrefix();
            while (true)
            {
                var t = Cur;
                switch (t.Kind)
                {
                    case TokenKind.LParen:
                        left = ParseCall(left);
                        continue;
                    case TokenKind.LBracket:
                        left = ParseIndex(left);
                        continue;
                    case TokenKind.Dot:
                    {
                        Advance();
                        var name = Expect(TokenKind.Ident, "a member name after '.'");
                        left = new MemberExpr(left, name.Text, name.Span, _src.Join(left.Span, name.Span));
                        continue;
                    }
                    case TokenKind.Transpose or TokenKind.ConjTranspose:
                        Advance();
                        left = new PostfixExpr(t.Kind, left, _src.Join(left.Span, t.Span));
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
            case TokenKind.Yep:
                Advance();
                return new BoolLiteral(true, t.Span);
            case TokenKind.Nop:
                Advance();
                return new BoolLiteral(false, t.Span);
            case TokenKind.String:
                Advance();
                return ParseTemplate(t);
            case TokenKind.Ident or TokenKind.Me or TokenKind.Elder:
                Advance();
                return new NameExpr(t.Text, t.Span);
            case TokenKind.Minus or TokenKind.Bang or TokenKind.Amp or TokenKind.Tilde:
            {
                Advance();
                var operand = ParseExpr(BpUnary);
                return new UnaryExpr(t.Kind, operand, _src.Join(t.Span, operand.Span));
            }
            case TokenKind.LParen:
            {
                Advance();
                if (Match(TokenKind.RParen)) return new UnitLiteral(SpanFrom(t));
                var inner = ParseExpr();
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
        var args = new List<Arg>();
        while (!At(TokenKind.RParen))
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
                args.Add(new Arg(null, v, v.Span));
            }
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RParen, "')' to close the call");
        return new CallExpr(callee, args, SpanFrom(callee.Span));
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
