namespace Splat.Syntax;

/// <summary>Generic traversal helpers: every statement or expression nested anywhere under a node.</summary>
public static class AstWalk
{
    /// <summary>The direct sub-expressions of an expression (lambda bodies and expression arms of a <c>pick</c> included).</summary>
    public static IEnumerable<Expr> Children(Expr e)
    {
        switch (e)
        {
            case StringTemplate st: return st.Parts.OfType<TemplateExpr>().Select(p => p.Value);
            case UnaryExpr u: return [u.Operand];
            case PostfixExpr p: return [p.Operand];
            case BinaryExpr b: return [b.Left, b.Right];
            case CallExpr c: return c.Args.Select(a => a.Value).Prepend(c.Callee);
            case IndexExpr ix: return ix.Indices.Prepend(ix.Target);
            case MemberExpr m: return [m.Target];
            case ListLiteral l: return l.Items;
            case TupleExpr t: return t.Items;
            case ParenListExpr pl: return pl.Items;
            case PairExpr pr: return [pr.Key, pr.Value];
            case LambdaExpr lam: return [lam.Body];
            case StepExpr s: return [s.Range, s.Step];
            case ChainExpr ch: return ch.Parts;
            case NormExpr n: return [n.Operand];
            case PickExpr pk: return pk.Arms.Where(a => a.ExprBody is not null).Select(a => a.ExprBody!).Prepend(pk.Subject);
            default: return [];
        }
    }

    /// <summary>The expression and every expression below it.</summary>
    public static IEnumerable<Expr> Exprs(Expr e)
    {
        yield return e;
        foreach (var c in Children(e))
            foreach (var d in Exprs(c))
                yield return d;
    }

    /// <summary>Every statement in the list, including those nested in blocks (and in block arms of a <c>pick</c> within expressions).</summary>
    public static IEnumerable<Stmt> Statements(IEnumerable<Stmt> stmts)
    {
        foreach (var s in stmts)
        {
            yield return s;
            foreach (var inner in Nested(s))
                foreach (var d in Statements(inner)) yield return d;
            // Block arms of a pick expression hold statements too.
            foreach (var root in Own(s))
                foreach (var e in Exprs(root))
                    if (e is PickExpr pk)
                        foreach (var arm in pk.Arms)
                            if (arm.BlockBody is not null)
                                foreach (var d in Statements(arm.BlockBody.Statements)) yield return d;
        }
    }

    private static IEnumerable<IReadOnlyList<Stmt>> Nested(Stmt s)
    {
        switch (s)
        {
            case IfStmt h:
                foreach (var b in h.Branches) yield return b.Body.Statements;
                if (h.Else is not null) yield return h.Else.Statements;
                break;
            case LoopStmt l: yield return l.Body.Statements; break;
            case FnDecl { BlockBody: { } fb }: yield return fb.Statements; break;
            case TryStmt t: yield return t.Body.Statements; yield return t.Handler.Statements; break;
        }
    }

    /// <summary>The expressions that belong directly to a statement (not to statements nested in it).</summary>
    public static IEnumerable<Expr> Own(Stmt s)
    {
        switch (s)
        {
            case BindStmt b: yield return b.Value; break;
            case AssignStmt a: yield return a.Target; yield return a.Value; break;
            case ExprStmt e: yield return e.Expression; break;
            case ReturnStmt { Value: { } v }: yield return v; break;
            case RipStmt { Message: { } m }: yield return m; break;
            case IfStmt h: foreach (var b in h.Branches) yield return b.Condition; break;
            case LoopStmt { Subject: { } sub }: yield return sub; break;
            case TupleBindStmt tb: yield return tb.Value; break;
            case DropStmt d: yield return d.Target; break;
            case FnDecl { ExprBody: { } fe }: yield return fe; break;
        }
    }

    /// <summary>Every expression anywhere under these statements.</summary>
    public static IEnumerable<Expr> ExprsIn(IEnumerable<Stmt> stmts)
    {
        foreach (var s in Statements(stmts))
            foreach (var root in Own(s))
                foreach (var e in Exprs(root)) yield return e;
    }
}
