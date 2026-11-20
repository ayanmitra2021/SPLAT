using Splat.Syntax;

namespace Splat.Semantics;

/// <summary>
/// Is an expression free of side effects and of anything that depends on state outside its own variables? Phase 30 lowers the
/// body of a <c>∑</c>/<c>∏</c> to the numeric engine only when it is pure (spec 13.6.1), so the checker records the answer now.
/// The test is deliberately conservative: unknown calls, lambdas, <c>pick</c>, templates and object access are all "not pure".
/// </summary>
public static class Purity
{
    private static readonly HashSet<string> PureFns = new(StringComparer.Ordinal)
    {
        "sqrt", "exp", "ln", "sin", "cos", "tan", "tanh", "erf", "floor", "ceil", "abs", "pow", "min", "max",
    };

    public static bool IsPure(Expr e, Scope scope) => e switch
    {
        IntLiteral or FloatLiteral or BoolLiteral or CharLiteral or UnitLiteral or TemporalLiteral => true,
        NameExpr n => scope.Lookup(n.Name) is { Kind: SymbolKind.Variable },
        UnaryExpr { Op: TokenKind.Minus or TokenKind.Bang } u => IsPure(u.Operand, scope),
        PostfixExpr p => IsPure(p.Operand, scope),
        NormExpr nm => IsPure(nm.Operand, scope),
        BinaryExpr b when b.Op is not (TokenKind.PipeGreater or TokenKind.GreaterGreater) => IsPure(b.Left, scope) && IsPure(b.Right, scope),
        IndexExpr ix => IsPure(ix.Target, scope) && ix.Indices.All(i => i is FullSlice || IsPure(i, scope)),
        CallExpr { Callee: NameExpr cn } c when scope.Lookup(cn.Name) is null && (PureFns.Contains(cn.Name) || Types.ByName.ContainsKey(cn.Name)) =>
            c.Args.All(a => IsPure(a.Value, scope)),
        _ => false,
    };
}
