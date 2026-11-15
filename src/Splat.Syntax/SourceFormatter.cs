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
                if (b.IsMutable) sb.Append("wag ");
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
            case CookDecl c:
                Cook(sb, c, level);
                break;
            case YeetStmt y:
                sb.Append("yeet").Append(y.Value is null ? "" : " " + Expr(y.Value)).Append('\n');
                break;
            case RipStmt r:
                sb.Append("rip").Append(r.Message is null ? "" : " " + Expr(r.Message)).Append('\n');
                break;
            case BailStmt:
                sb.Append("bail\n");
                break;
            case SkipStmt:
                sb.Append("skip\n");
                break;
            case HmmStmt h:
                for (int i = 0; i < h.Branches.Count; i++)
                {
                    if (i > 0) Indent(sb, level);
                    sb.Append(i == 0 ? "hmm " : "meh ").Append(Expr(h.Branches[i].Condition));
                    Body(sb, h.Branches[i].Body, level);
                }
                if (h.Else is not null)
                {
                    Indent(sb, level);
                    sb.Append("nah");
                    Body(sb, h.Else, level);
                }
                Indent(sb, level);
                sb.Append(";\n");
                break;
            case SpinStmt sp:
                sb.Append("spin");
                if (sp.Kind == SpinKind.While) sb.Append(" while ").Append(Expr(sp.Subject!));
                else if (sp.Kind == SpinKind.In) sb.Append(' ').Append(sp.Variable).Append(" in ").Append(Expr(sp.Subject!));
                Body(sb, sp.Body, level);
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

    private static void Cook(StringBuilder sb, CookDecl c, int level)
    {
        sb.Append("cook ").Append(c.OpSymbol is null ? c.Name : "op " + c.OpSymbol);
        if (c.Generics.Count > 0)
        {
            sb.Append('<').Append(string.Join(", ", c.Generics.Select(g =>
                g.IsDim ? "$" + g.Name : g.Bound is null ? g.Name : g.Name + ": " + g.Bound))).Append('>');
        }
        sb.Append('(').Append(string.Join(", ", c.Params.Select(Param))).Append(')');
        if (c.ReturnType is not null) sb.Append("::").Append(Type(c.ReturnType));
        if (c.ExprBody is not null)
        {
            sb.Append(" = ").Append(Expr(c.ExprBody)).Append('\n');
            return;
        }
        Body(sb, c.BlockBody!, level);
        Indent(sb, level);
        sb.Append(";\n");
    }

    private static string Param(Param p) => p.Kind switch
    {
        ParamKind.Me => "me",
        ParamKind.PeekMe => "&me",
        ParamKind.PokeMe => "~me",
        _ => p.Name + "::" + Type(p.Type!) + (p.Default is null ? "" : " = " + Expr(p.Default)),
    };

    // ---- types ----

    public static string Type(TypeRef t) => t switch
    {
        NamedType n => n.Args.Count == 0 ? n.Name : n.Name + "<" + string.Join(", ", n.Args.Select(Type)) + ">",
        TensorType tt => Type(tt.Element) + "[" + string.Join(", ", tt.Dims.Select(Dim)) + "]",
        RefType r => (r.IsPoke ? "~" : "&") + Type(r.Inner),
        FuncType f => "cook(" + string.Join(", ", f.Params.Select(Type)) + ")" + (f.Return is null ? "" : "::" + Type(f.Return)),
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
        _ => int.MaxValue,
    };

    private static string Wrap(Expr e, bool parens) => parens ? "(" + Expr(e) + ")" : Expr(e);

    public static string Expr(Expr e)
    {
        switch (e)
        {
            case IntLiteral i: return i.Value.ToString();
            case FloatLiteral f: return AstPrinter.FloatText(f.Value);
            case CharLiteral c:
                return "c" + AstPrinter.Quote(char.ConvertFromUtf32(c.Value)).Replace("\\{", "{");
            case BoolLiteral b: return b.Value ? "yep" : "nop";
            case UnitLiteral: return "()";
            case StringTemplate s: return Template(s);
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
            default: return "(error)";
        }
    }

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
