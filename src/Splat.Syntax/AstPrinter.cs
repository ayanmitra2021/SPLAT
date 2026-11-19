using System.Globalization;
using System.Text;

namespace Splat.Syntax;

/// <summary>An s-expression: an atom or a list. Used for <c>splat ast</c> and for tests.</summary>
public abstract record SNode
{
    public sealed record Atom(string Text) : SNode;
    public sealed record List(IReadOnlyList<SNode> Items) : SNode;

    public static SNode L(params SNode[] items) => new List(items);
    public static SNode L(string head) => new List([new Atom(head)]);
    public static SNode L(string head, IEnumerable<SNode> items) => new List([new Atom(head), .. items]);
    public static SNode A(string text) => new Atom(text);

    /// <summary>Single-line rendering.</summary>
    public string Flat() => this switch
    {
        Atom a => a.Text,
        List l => "(" + string.Join(" ", l.Items.Select(i => i.Flat())) + ")",
        _ => "",
    };

    /// <summary>Indented rendering: a list stays on one line when it fits, otherwise its children go one per line.</summary>
    public string Pretty(int width = 100)
    {
        var sb = new StringBuilder();
        Write(sb, 0, width);
        return sb.ToString();
    }

    private void Write(StringBuilder sb, int indent, int width)
    {
        var flat = Flat();
        if (this is Atom || indent + flat.Length <= width || ((List)this).Items.Count <= 1)
        {
            sb.Append(flat);
            return;
        }
        var l = (List)this;
        sb.Append('(').Append(l.Items[0].Flat());
        foreach (var child in l.Items.Skip(1))
        {
            sb.Append('\n').Append(' ', indent + 2);
            child.Write(sb, indent + 2, width);
        }
        sb.Append(')');
    }
}

/// <summary>Converts the AST into s-expressions (spans are dropped, so two parses compare by text).</summary>
public static class AstPrinter
{
    public static string OpText(TokenKind k) => k switch
    {
        TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Star => "*", TokenKind.Slash => "/",
        TokenKind.Percent => "%", TokenKind.StarStar => "**", TokenKind.At => "@", TokenKind.Backslash => "\\",
        TokenKind.MiddleDot => "·", TokenKind.DotStar => ".*", TokenKind.DotSlash => "./", TokenKind.DotPlus => ".+",
        TokenKind.DotMinus => ".-", TokenKind.DotDot => "..", TokenKind.DotDotEq => "..=", TokenKind.Lt => "<",
        TokenKind.Le => "<=", TokenKind.Gt => ">", TokenKind.Ge => ">=", TokenKind.EqEq => "==", TokenKind.BangEq => "!=",
        TokenKind.Bang => "!", TokenKind.Amp => "&", TokenKind.Tilde => "~", TokenKind.And => "and", TokenKind.Or => "or",
        TokenKind.PipeGreater => "|>", TokenKind.GreaterGreater => ">>", TokenKind.In => "in", TokenKind.Transpose => "'",
        TokenKind.ConjTranspose => "''", TokenKind.Assign => "=", TokenKind.PlusAssign => "+=",
        TokenKind.MinusAssign => "-=", TokenKind.StarAssign => "*=", TokenKind.SlashAssign => "/=",
        TokenKind.AtAssign => "@=", TokenKind.DotStarAssign => ".*=", TokenKind.DotSlashAssign => "./=",
        _ => k.ToString(),
    };

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\n' => "\\n", '\t' => "\\t", '\r' => "\\r", '\0' => "\\0", '"' => "\\\"", '\\' => "\\\\", '{' => "\\{",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    public static string FloatText(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') || s.Contains('e') || s.Contains("Infinity") || s.Contains("NaN") ? s : s + ".0";
    }

    public static string ProgramText(Program p) =>
        string.Join("\n", p.Statements.Select(s => Stmt(s).Pretty())) + (p.Statements.Count > 0 ? "\n" : "");

    // ---- types ----

    public static SNode Type(TypeRef t) => t switch
    {
        NamedType n when n.Args.Count == 0 => SNode.A(n.Name),
        NamedType n => SNode.L(n.Name, n.Args.Select(Type)),
        TensorType tt => SNode.L("tensor", [Type(tt.Element), .. tt.Dims.Select(Dimension)]),
        RefType r => SNode.L(r.IsPoke ? "~" : "&", [Type(r.Inner)]),
        TupleType tu => SNode.L("tuple-type", tu.Items.Select(Type)),
        FuncType f => SNode.L("fn-type", [SNode.L("params", f.Params.Select(Type)), .. f.Return is null ? [] : new[] { Type(f.Return) }]),
        _ => SNode.A("?type"),
    };

    private static SNode Dimension(Dim d) => d switch
    {
        ConstDim c => SNode.A(c.Value.ToString(CultureInfo.InvariantCulture)),
        VarDim v => SNode.A("$" + v.Name),
        _ => SNode.A("?"),
    };

    // ---- expressions ----

    public static SNode Expr(Expr e) => e switch
    {
        IntLiteral i => SNode.A(i.Value.ToString(CultureInfo.InvariantCulture)),
        FloatLiteral f => SNode.A(FloatText(f.Value)),
        CharLiteral c => SNode.A($"c\"{char.ConvertFromUtf32(c.Value).Replace("\n", "\\n").Replace("\"", "\\\"").Replace("\\", "\\\\")}\""),
        BoolLiteral b => SNode.A(b.Value ? "true" : "false"),
        UnitLiteral => SNode.A("()"),
        StringTemplate s => Template(s),
        NameExpr n => SNode.A(n.Name),
        UnaryExpr u => SNode.L(OpText(u.Op), [Expr(u.Operand)]),
        PostfixExpr p => SNode.L(OpText(p.Op), [Expr(p.Operand)]),
        BinaryExpr b => SNode.L(OpText(b.Op), [Expr(b.Left), Expr(b.Right)]),
        CallExpr c => SNode.L("call", [Expr(c.Callee), .. c.Args.Select(Argument)]),
        IndexExpr i => SNode.L("index", [Expr(i.Target), .. i.Indices.Select(Expr)]),
        FullSlice => SNode.A(":"),
        MemberExpr m => SNode.L(".", [Expr(m.Target), SNode.A(m.Name)]),
        ListLiteral l => SNode.L("list", l.Items.Select(Expr)),
        LambdaExpr l => SNode.L("lambda", [SNode.L("params", l.Params.Select(p => p.Type is null ? SNode.A(p.Name) : SNode.L(p.Name, [SNode.L(":", [Type(p.Type)])]))), Expr(l.Body)]),
        NormExpr n => SNode.L("norm", [SNode.A(NormText(n.Kind)), Expr(n.Operand)]),
        DerivExpr d => SNode.L("grad", [Expr(d.Function), Expr(d.Wrt)]),
        IntegExpr i => SNode.L("intg", [
            SNode.L("ranges", i.Ranges.Select(Expr)),
            .. i.Options.Count == 0 ? [] : new[] { SNode.L("opts", i.Options.Select(Argument)) },
            Expr(i.Integrand)]),
        ReduceExpr r => SNode.L(r.IsProduct ? "prod" : "sum", [SNode.A(r.Variable), Expr(r.Range), Expr(r.Body)]),
        FitExpr f => SNode.L(f.IsClassify ? "classify" : "regress", [SNode.L("config", f.Config.Select(Argument)), Expr(f.Features), Expr(f.Targets)]),
        StepExpr s => SNode.L("by", [Expr(s.Range), Expr(s.Step)]),
        ChainExpr c => SNode.L("then", c.Parts.Select(Expr)),
        ParenListExpr l => SNode.L("members", l.Items.Select(Expr)),
        PickExpr p => SNode.L("pick", [Expr(p.Subject), .. p.Arms.Select(Arm)]),
        TupleExpr t => SNode.L("tuple", t.Items.Select(Expr)),
        PairExpr pr => SNode.L("pair", [Expr(pr.Key), Expr(pr.Value)]),
        ErrorExpr => SNode.A("(error)"),
        _ => SNode.A("?expr"),
    };

    private static SNode Arm(PickArm a)
    {
        var pat = Pat(a.Pat);
        if (a.ExprBody is not null) return SNode.L("arm", [pat, Expr(a.ExprBody)]);
        return SNode.L("arm", [pat, Block(a.BlockBody!)]);
    }

    private static SNode Pat(Pattern p) => p switch
    {
        WildcardPat => SNode.A("_"),
        BindPat b => SNode.A(b.Name),
        LiteralPat l => Expr(l.Value),
        VariantPat v when v.Bindings.Count == 0 => SNode.A(v.VariantName),
        VariantPat v => SNode.L(v.VariantName, v.Bindings.Select(Pat)),
        RangePat r => SNode.L(r.Inclusive ? "..=" : "..", [Expr(r.Lo), Expr(r.Hi)]),
        TuplePat t => SNode.L("tuple-pat", t.Items.Select(Pat)),
        _ => SNode.A("?pat"),
    };

    public static string NormText(NormKind k) => k switch
    {
        NormKind.L1 => "1", NormKind.LInf => "inf", NormKind.Frobenius => "F", _ => "2",
    };

    private static SNode Argument(Arg a) => a.Name is null ? Expr(a.Value) : SNode.L("arg", [SNode.A(a.Name), Expr(a.Value)]);

    private static SNode Template(StringTemplate s)
    {
        if (s.Parts.All(p => p is TemplateText))
            return SNode.A(Quote(string.Concat(s.Parts.Cast<TemplateText>().Select(p => p.Value))));
        return SNode.L("str", s.Parts.Select<TemplatePart, SNode>(p => p switch
        {
            TemplateText t => SNode.A(Quote(t.Value)),
            TemplateExpr x when x.Format is null => SNode.L("interp", [Expr(x.Value)]),
            TemplateExpr x => SNode.L("interp", [Expr(x.Value), SNode.A(Quote(x.Format!))]),
            _ => SNode.A("?part"),
        }));
    }

    // ---- statements ----

    public static SNode Block(Block b) => SNode.L("block", b.Statements.Select(Stmt));

    public static SNode Stmt(Stmt s) => s switch
    {
        BindStmt b => SNode.L(b.IsMutable ? "var" : "bind",
            [SNode.A(b.Name), .. b.Type is null ? [] : new[] { SNode.L(":", [Type(b.Type)]) }, Expr(b.Value)]),
        AssignStmt a => SNode.L(OpText(a.Op), [Expr(a.Target), Expr(a.Value)]),
        ExprStmt e => Expr(e.Expression),
        FnDecl c => Fn(c),
        ReturnStmt y => y.Value is null ? SNode.L("ret") : SNode.L("ret", [Expr(y.Value)]),
        RipStmt r => r.Message is null ? SNode.L("rip") : SNode.L("rip", [Expr(r.Message)]),
        BreakStmt => SNode.L("br"),
        ContinueStmt => SNode.L("cont"),
        IfStmt h => SNode.L("if", [
            .. h.Branches.Select(b => SNode.L("if", [Expr(b.Condition), Block(b.Body)])),
            .. h.Else is null ? [] : new[] { SNode.L("else", [Block(h.Else)]) }]),
        LoopStmt sp => sp.Kind switch
        {
            LoopKind.Forever => SNode.L("loop", [Block(sp.Body)]),
            LoopKind.While => SNode.L("loop-while", [Expr(sp.Subject!), Block(sp.Body)]),
            _ => SNode.L("loop-in", [SNode.A(sp.Variable!), Expr(sp.Subject!), Block(sp.Body)]),
        },
        ReservedStmt r => SNode.L("reserved", [SNode.A(r.Keyword)]),
        EnumDecl m => SNode.L("enum", [SNode.A(m.Name),
            .. m.Generics.Count == 0 ? [] : new[] { SNode.L("generics", m.Generics.Select(g => SNode.A(g.Name))) },
            .. m.Variants.Select(v => v.Payloads.Count == 0 ? SNode.A(v.Name) : SNode.L(v.Name, v.Payloads.Select(Type)))]),
        TupleBindStmt tb => SNode.L(tb.IsMutable ? "var-tuple" : "bind-tuple",
            [SNode.L("names", tb.Names.Select(SNode.A)), Expr(tb.Value)]),
        ErrorStmt => SNode.A("(error)"),
        _ => SNode.A("?stmt"),
    };

    private static SNode Fn(FnDecl c)
    {
        var items = new List<SNode> { SNode.A(c.Name) };
        if (c.Generics.Count > 0)
            items.Add(SNode.L("generics", c.Generics.Select(g => SNode.A((g.IsDim ? "$" : "") + g.Name + (g.Bound is null ? "" : ":" + g.Bound)))));
        items.Add(SNode.L("params", c.Params.Select(Parameter)));
        if (c.ReturnType is not null) items.Add(SNode.L("->", [Type(c.ReturnType)]));
        items.Add(c.ExprBody is not null ? SNode.L("=", [Expr(c.ExprBody)]) : Block(c.BlockBody!));
        return SNode.L("fn", items);
    }

    private static SNode Parameter(Param p)
    {
        if (p.Kind != ParamKind.Normal)
            return SNode.A(p.Kind switch { ParamKind.PeekSelf => "&self", ParamKind.PokeSelf => "~self", _ => "self" });
        var items = new List<SNode> { SNode.A(p.Name), Type(p.Type!) };
        if (p.Default is not null) items.Add(Expr(p.Default));
        return SNode.L(items.ToArray());
    }
}
