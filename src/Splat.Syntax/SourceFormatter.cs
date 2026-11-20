using System.Text;

namespace Splat.Syntax;

/// <summary>
/// Prints an AST back as canonical SPLAT source (4-space indent, parentheses only where precedence needs them).
/// Re-parsing the output yields the same tree, which is what the round-trip tests check.
/// </summary>
public static class SourceFormatter
{
    public static string Format(Program p)
    {
        var sb = new StringBuilder();
        foreach (var s in p.Statements) Stmt(sb, s, 0);
        return sb.ToString();
    }

    // ---- statements ----

    private static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 4);

    private static void Body(StringBuilder sb, Block b, int level)
    {
        sb.Append(":\n");
        foreach (var s in b.Statements) Stmt(sb, s, level + 1);
    }

    private static void Stmt(StringBuilder sb, Stmt s, int level)
    {
        Indent(sb, level);
        switch (s)
        {
            case BindStmt b:
                if (b.IsMutable) sb.Append("var ");
                sb.Append(b.Name);
                if (b.Type is null) sb.Append(b.IsMutable ? " = " : " := ");
                else sb.Append("::").Append(Type(b.Type)).Append(" = ");
                sb.Append(Expr(b.Value)).Append('\n');
                break;
            case AssignStmt a:
                sb.Append(Expr(a.Target)).Append(' ').Append(AstPrinter.OpText(a.Op)).Append(' ').Append(Expr(a.Value)).Append('\n');
                break;
            case ExprStmt e:
                sb.Append(Expr(e.Expression)).Append('\n');
                break;
            case FnDecl c:
                Fn(sb, c, level);
                break;
            case ReturnStmt y:
                sb.Append("ret").Append(y.Value is null ? "" : " " + Expr(y.Value)).Append('\n');
                break;
            case RipStmt r:
                sb.Append("rip").Append(r.Message is null ? "" : " " + Expr(r.Message)).Append('\n');
                break;
            case BreakStmt:
                sb.Append("br\n");
                break;
            case ContinueStmt:
                sb.Append("cont\n");
                break;
            case IfStmt h:
                for (int i = 0; i < h.Branches.Count; i++)
                {
                    if (i > 0) Indent(sb, level);
                    sb.Append(i == 0 ? "if " : "elif ").Append(Expr(h.Branches[i].Condition));
                    Body(sb, h.Branches[i].Body, level);
                }
                if (h.Else is not null)
                {
                    Indent(sb, level);
                    sb.Append("el");
                    Body(sb, h.Else, level);
                }
                Indent(sb, level);
                sb.Append(";\n");
                break;
            case LoopStmt sp:
                sb.Append("loop");
                if (sp.Kind == LoopKind.While) sb.Append(" while ").Append(Expr(sp.Subject!));
                else if (sp.Kind == LoopKind.In) sb.Append(' ').Append(sp.Variable).Append(" in ").Append(Expr(sp.Subject!));
                Body(sb, sp.Body, level);
                Indent(sb, level);
                sb.Append(";\n");
                break;
            case EnumDecl m:
                sb.Append("enum ").Append(m.Name);
                if (m.Generics.Count > 0)
                    sb.Append('<').Append(string.Join(", ", m.Generics.Select(GenericText))).Append('>');
                sb.Append(":\n");
                for (int i = 0; i < m.Variants.Count; i++)
                {
                    var v = m.Variants[i];
                    Indent(sb, level + 1);
                    sb.Append(v.Name);
                    if (v.Payloads.Count > 0) sb.Append('(').Append(string.Join(", ", v.Payloads.Select(Type))).Append(')');
                    sb.Append(i + 1 < m.Variants.Count ? ",\n" : "\n");
                }
                Indent(sb, level);
                sb.Append(";\n");
                break;
            case TupleBindStmt tb:
                sb.Append(tb.IsMutable ? "var " : "").Append('(').Append(string.Join(", ", tb.Names)).Append(')')
                  .Append(tb.IsMutable ? " = " : " := ").Append(Expr(tb.Value)).Append('\n');
                break;
            case ClassDecl c:
                sb.Append("class ").Append(c.Name);
                if (c.Generics.Count > 0) sb.Append('<').Append(string.Join(", ", c.Generics.Select(GenericText))).Append('>');
                if (c.Parent is not null) sb.Append(" from ").Append(Type(c.Parent));
                if (c.Traits.Count > 0) sb.Append(" with ").Append(string.Join(", ", c.Traits.Select(Type)));
                sb.Append(":\n");
                foreach (var f in c.Fields)
                {
                    Indent(sb, level + 1);
                    if (f.IsSecret) sb.Append("secret ");
                    sb.Append(f.Name).Append("::").Append(Type(f.Type));
                    if (f.Default is not null) sb.Append(" = ").Append(Expr(f.Default));
                    sb.Append('\n');
                }
                foreach (var m in c.Methods)
                {
                    Indent(sb, level + 1);
                    if (m.IsSecret) sb.Append("secret ");
                    Fn(sb, m.Fn, level + 1);
                }
                Indent(sb, level);
                sb.Append(";\n");
                break;
            case TraitDecl t:
                sb.Append("trait ").Append(t.Name);
                if (t.Generics.Count > 0) sb.Append('<').Append(string.Join(", ", t.Generics.Select(GenericText))).Append('>');
                sb.Append(":\n");
                foreach (var m in t.Methods) { Indent(sb, level + 1); Fn(sb, m, level + 1); }
                Indent(sb, level);
                sb.Append(";\n");
                break;
            case ExtendDecl x:
                sb.Append("extend ").Append(Type(x.Target));
                if (x.Trait is not null) sb.Append(" with ").Append(Type(x.Trait));
                sb.Append(":\n");
                foreach (var m in x.Methods) { Indent(sb, level + 1); Fn(sb, m, level + 1); }
                Indent(sb, level);
                sb.Append(";\n");
                break;
            case DropStmt d:
                sb.Append("drop ").Append(Expr(d.Target)).Append('\n');
                break;
            case TryStmt t:
                sb.Append("try");
                Body(sb, t.Body, level);
                Indent(sb, level);
                sb.Append("catch ").Append(t.CatchName);
                Body(sb, t.Handler, level);
                Indent(sb, level);
                sb.Append(";\n");
                break;
            case ReservedStmt r:
                sb.Append("# (skipped ").Append(r.Keyword).Append(" construct)\n");
                break;
            default:
                sb.Append("# (error)\n");
                break;
        }
    }

    private static void Fn(StringBuilder sb, FnDecl c, int level)
    {
        sb.Append("fn ").Append(c.OpSymbol is null ? c.Name : "op " + c.OpSymbol);
        if (c.Generics.Count > 0)
        {
            sb.Append('<').Append(string.Join(", ", c.Generics.Select(GenericText))).Append('>');
        }
        sb.Append('(').Append(string.Join(", ", c.Params.Select(Param))).Append(')');
        if (c.ReturnType is not null) sb.Append("::").Append(Type(c.ReturnType));
        if (c.ExprBody is not null)
        {
            sb.Append(" = ").Append(Expr(c.ExprBody)).Append('\n');
            return;
        }
        if (c.BlockBody is null) { sb.Append('\n'); return; } // a required trait method
        Body(sb, c.BlockBody, level);
        Indent(sb, level);
        sb.Append(";\n");
    }

    private static string GenericText(GenericParam g) =>
        g.IsDim ? "$" + g.Name : g.Bound is null ? g.Name : g.Name + ": " + Type(g.Bound);

    private static string Param(Param p) => p.Kind switch
    {
        ParamKind.Self => "self",
        ParamKind.PeekSelf => "&self",
        ParamKind.PokeSelf => "~self",
        _ => p.Name + "::" + Type(p.Type!) + (p.Default is null ? "" : " = " + Expr(p.Default)),
    };

    // ---- types ----

    public static string Type(TypeRef t) => t switch
    {
        NamedType n => n.Args.Count == 0 ? n.Name : n.Name + "<" + string.Join(", ", n.Args.Select(Type)) + ">",
        TensorType tt => Type(tt.Element) + "[" + string.Join(", ", tt.Dims.Select(Dim)) + "]",
        RefType r => (r.IsPoke ? "~" : "&") + Type(r.Inner),
        TupleType tu => "(" + string.Join(", ", tu.Items.Select(Type)) + ")",
        FuncType f => "fn(" + string.Join(", ", f.Params.Select(Type)) + ")" + (f.Return is null ? "" : "::" + Type(f.Return)),
        _ => "?",
    };

    private static string Dim(Dim d) => d switch
    {
        ConstDim c => c.Value.ToString(),
        VarDim v => "$" + v.Name,
        _ => "?",
    };

    // ---- expressions ----

    /// <summary>Precedence of an expression's outermost operator (atoms are higher than everything).</summary>
    private static int Prec(Expr e) => e switch
    {
        BinaryExpr b => Parser.TryGetBinary(b.Op, out int bp, out _) ? bp : 0,
        UnaryExpr => Parser.PrecUnary,
        LambdaExpr or IntegExpr => 1,          // bodies run to the right as far as they can: always parenthesise as an operand
        ReduceExpr => Parser.PrecReduceBody - 1, // body takes in '+ -', so it must be wrapped inside any arithmetic operator
        StepExpr => Parser.PrecBy,
        ChainExpr => Parser.PrecThen,
        _ => int.MaxValue,
    };

    private static int PrecReduceBody => Parser.PrecReduceBody;

    private static string Named(Arg a) => a.Name is null ? Expr(a.Value) : a.Name + " = " + Expr(a.Value);

    private static string Lambda(LambdaExpr l) =>
        (l.Params.Count == 0 ? "| |" : "|" + string.Join(", ", l.Params.Select(p => p.Type is null ? p.Name : p.Name + "::" + Type(p.Type))) + "|") + " " + Expr(l.Body);

    private static string Wrap(Expr e, bool parens) => parens ? "(" + Expr(e) + ")" : Expr(e);

    public static string Expr(Expr e)
    {
        switch (e)
        {
            case IntLiteral i: return i.Value.ToString();
            case FloatLiteral f: return AstPrinter.FloatText(f.Value);
            case CharLiteral c:
                return "c" + AstPrinter.Quote(char.ConvertFromUtf32(c.Value)).Replace("\\{", "{");
            case BoolLiteral b: return b.Value ? "true" : "false";
            case UnitLiteral: return "()";
            case StringTemplate s: return Template(s);
            case TemporalLiteral t: return AstPrinter.TemporalText(t);
            case NameExpr n: return n.Name;
            case UnaryExpr u: return AstPrinter.OpText(u.Op) + Wrap(u.Operand, Prec(u.Operand) < Parser.PrecUnary);
            case PostfixExpr p: return Wrap(p.Operand, Prec(p.Operand) < Parser.PrecPostfix) + AstPrinter.OpText(p.Op);
            case BinaryExpr b:
            {
                Parser.TryGetBinary(b.Op, out int bp, out var assoc);
                bool leftParens = assoc == Parser.Assoc.Right ? Prec(b.Left) <= bp : assoc == Parser.Assoc.None ? Prec(b.Left) <= bp : Prec(b.Left) < bp;
                bool rightParens = assoc == Parser.Assoc.Right ? Prec(b.Right) < bp : Prec(b.Right) <= bp;
                string op = AstPrinter.OpText(b.Op);
                bool tight = b.Op is TokenKind.DotDot or TokenKind.DotDotEq or TokenKind.StarStar;
                return Wrap(b.Left, leftParens) + (tight ? op : " " + op + " ") + Wrap(b.Right, rightParens);
            }
            case CallExpr c:
                return Wrap(c.Callee, Prec(c.Callee) < Parser.PrecPostfix) + "(" +
                       string.Join(", ", c.Args.Select(a => a.Name is null ? Expr(a.Value) : a.Name + " = " + Expr(a.Value))) + ")";
            case IndexExpr i:
                return Wrap(i.Target, Prec(i.Target) < Parser.PrecPostfix) + "[" + string.Join(", ", i.Indices.Select(Expr)) + "]";
            case FullSlice: return ":";
            case MemberExpr m: return Wrap(m.Target, Prec(m.Target) < Parser.PrecPostfix) + "." + m.Name;
            case ListLiteral l: return "[" + string.Join(", ", l.Items.Select(Expr)) + "]";
            case StepExpr s: return Wrap(s.Range, Prec(s.Range) < Parser.PrecBy) + " by " + Wrap(s.Step, Prec(s.Step) <= Parser.PrecRange);
            case ChainExpr c: return string.Join(" then ", c.Parts.Select(p => Wrap(p, Prec(p) <= Parser.PrecThen)));
            case ParenListExpr l: return "(" + string.Join(", ", l.Items.Select(Expr)) + ")";
            case TupleExpr t: return "(" + string.Join(", ", t.Items.Select(Expr)) + ")";
            case PairExpr pr: return Expr(pr.Key) + " => " + Expr(pr.Value);
            case PickExpr pk: return Pick(pk);
            case LambdaExpr l: return Lambda(l);
            case NormExpr n:
            {
                var inner = Expr(n.Operand);
                // keep adjacent bars from fusing into a norm opener/closer where they should not
                string pad(string s, bool atStart) => s.Length > 0 && (atStart ? s[0] : s[^1]) == '|' ? " " : "";
                var suffix = n.Kind switch { NormKind.L1 => "_1", NormKind.LInf => "_inf", NormKind.Frobenius => "_F", _ => "" };
                return "||" + pad(inner, true) + inner + pad(inner, false) + "||" + suffix;
            }
            case DerivExpr d: return "grad(" + Expr(d.Function) + ", " + Expr(d.Wrt) + ")";
            case IntegExpr i:
                return "intg[" + string.Join(", ", i.Ranges.Select(Expr).Concat(i.Options.Select(Named))) + "] " + Lambda(i.Integrand);
            case ReduceExpr r:
                return (r.IsProduct ? "prod[" : "sum[") + r.Variable + " in " + Expr(r.Range) + "] " +
                       Wrap(r.Body, Prec(r.Body) < PrecReduceBody);
            case FitExpr f:
                return (f.IsClassify ? "classify(" : "regress(") + string.Join(", ", f.Config.Select(Named)) + ") fit (" +
                       Expr(f.Features) + ", " + Expr(f.Targets) + ")";
            default: return "(error)";
        }
    }

    private static string Pick(PickExpr pk)
    {
        var sb = new StringBuilder("pick ").Append(Expr(pk.Subject)).Append(":\n");
        foreach (var arm in pk.Arms)
        {
            sb.Append("    ").Append(Pattern(arm.Pat));
            if (arm.ExprBody is not null) sb.Append(" => ").Append(Expr(arm.ExprBody)).Append(",\n");
            else
            {
                sb.Append(":\n");
                foreach (var st in arm.BlockBody!.Statements) Stmt(sb, st, 2);
                sb.Append("    ;\n");
            }
        }
        return sb.Append(';').ToString();
    }

    public static string Pattern(Pattern p) => p switch
    {
        WildcardPat => "_",
        BindPat b => b.Name,
        LiteralPat l => Expr(l.Value),
        VariantPat v => v.Bindings.Count == 0 ? v.VariantName : v.VariantName + "(" + string.Join(", ", v.Bindings.Select(Pattern)) + ")",
        RangePat r => Expr(r.Lo) + (r.Inclusive ? "..=" : "..") + Expr(r.Hi),
        TuplePat t => "(" + string.Join(", ", t.Items.Select(Pattern)) + ")",
        _ => "_",
    };

    private static string Template(StringTemplate s)
    {
        var sb = new StringBuilder("\"");
        foreach (var part in s.Parts)
        {
            switch (part)
            {
                case TemplateText t:
                    sb.Append(AstPrinter.Quote(t.Value)[1..^1]);
                    break;
                case TemplateExpr x:
                    sb.Append('{').Append(Expr(x.Value));
                    if (x.Format is not null) sb.Append(':').Append(x.Format);
                    sb.Append('}');
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
