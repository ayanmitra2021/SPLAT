using Splat.Core;

namespace Splat.Syntax;

// AST for the Milestone 1 language. Every node carries a SourceSpan. Operators are stored as the TokenKind of
// their lexeme, so later phases switch on a closed set instead of comparing strings.

public abstract record Node(SourceSpan Span);

// ---- Program ----

public sealed record Program(IReadOnlyList<Stmt> Statements, SourceSpan Span) : Node(Span);

/// <summary>A sequence of statements opened by ':' and closed by ';'. The span covers the opener through the last statement.</summary>
public sealed record Block(IReadOnlyList<Stmt> Statements, SourceSpan Span) : Node(Span);

// ---- Types ----

public abstract record TypeRef(SourceSpan Span) : Node(Span);

/// <summary>A named type with optional generic arguments: <c>i32</c>, <c>Maybe&lt;i32&gt;</c>.</summary>
public sealed record NamedType(string Name, IReadOnlyList<TypeRef> Args, SourceSpan Span) : TypeRef(Span);

/// <summary>An element type with dimensions: <c>mid[3, $K, ?]</c>.</summary>
public sealed record TensorType(TypeRef Element, IReadOnlyList<Dim> Dims, SourceSpan Span) : TypeRef(Span);

/// <summary><c>&amp;T</c> (peek) or <c>~T</c> (poke).</summary>
public sealed record RefType(bool IsPoke, TypeRef Inner, SourceSpan Span) : TypeRef(Span);

/// <summary><c>cook (A, B)::R</c>.</summary>
public sealed record FuncType(IReadOnlyList<TypeRef> Params, TypeRef? Return, SourceSpan Span) : TypeRef(Span);

public abstract record Dim(SourceSpan Span) : Node(Span);
public sealed record ConstDim(ulong Value, SourceSpan Span) : Dim(Span);
public sealed record VarDim(string Name, SourceSpan Span) : Dim(Span);
public sealed record DynDim(SourceSpan Span) : Dim(Span);

// ---- Expressions ----

public abstract record Expr(SourceSpan Span) : Node(Span);

public sealed record IntLiteral(ulong Value, SourceSpan Span) : Expr(Span);
public sealed record FloatLiteral(double Value, SourceSpan Span) : Expr(Span);
/// <summary>Unicode scalar value of a <c>c"x"</c> literal.</summary>
public sealed record CharLiteral(int Value, SourceSpan Span) : Expr(Span);
public sealed record BoolLiteral(bool Value, SourceSpan Span) : Expr(Span);
public sealed record UnitLiteral(SourceSpan Span) : Expr(Span);

/// <summary>A string literal, possibly with interpolated parts. A plain string has only <see cref="TemplateText"/> parts.</summary>
public sealed record StringTemplate(IReadOnlyList<TemplatePart> Parts, SourceSpan Span) : Expr(Span);

public abstract record TemplatePart(SourceSpan Span) : Node(Span);
public sealed record TemplateText(string Value, SourceSpan Span) : TemplatePart(Span);
public sealed record TemplateExpr(Expr Value, string? Format, SourceSpan Span) : TemplatePart(Span);

/// <summary>An identifier, including <c>me</c> and <c>elder</c>.</summary>
public sealed record NameExpr(string Name, SourceSpan Span) : Expr(Span);

/// <summary>Prefix operator: <c>-</c>, <c>!</c>, <c>&amp;</c> (peek), <c>~</c> (poke).</summary>
public sealed record UnaryExpr(TokenKind Op, Expr Operand, SourceSpan Span) : Expr(Span);

/// <summary>Postfix <c>'</c> (<see cref="TokenKind.Transpose"/>) or <c>''</c> (<see cref="TokenKind.ConjTranspose"/>).</summary>
public sealed record PostfixExpr(TokenKind Op, Expr Operand, SourceSpan Span) : Expr(Span);

/// <summary>Binary operator, including ranges (<c>..</c>, <c>..=</c>), <c>and</c>/<c>or</c>, <c>|&gt;</c> and <c>&gt;&gt;</c>.</summary>
public sealed record BinaryExpr(TokenKind Op, Expr Left, Expr Right, SourceSpan Span) : Expr(Span);

public sealed record Arg(string? Name, Expr Value, SourceSpan Span) : Node(Span);
public sealed record CallExpr(Expr Callee, IReadOnlyList<Arg> Args, SourceSpan Span) : Expr(Span);

public sealed record IndexExpr(Expr Target, IReadOnlyList<Expr> Indices, SourceSpan Span) : Expr(Span);
/// <summary>A bare <c>:</c> inside an index: the whole axis (<c>A[:, 0]</c>).</summary>
public sealed record FullSlice(SourceSpan Span) : Expr(Span);

public sealed record MemberExpr(Expr Target, string Name, SourceSpan NameSpan, SourceSpan Span) : Expr(Span);

/// <summary>Bracket literal <c>[a, b, c]</c>; how it relates to tensors is decided in Phase 12.</summary>
public sealed record ListLiteral(IReadOnlyList<Expr> Items, SourceSpan Span) : Expr(Span);

public sealed record LambdaParam(string Name, TypeRef? Type, SourceSpan Span) : Node(Span);

/// <summary><c>|a, b| body</c>. The body extends as far right as an expression can.</summary>
public sealed record LambdaExpr(IReadOnlyList<LambdaParam> Params, Expr Body, SourceSpan Span) : Expr(Span);

public enum NormKind { L2, L1, LInf, Frobenius }

/// <summary><c>||v||</c> (L2), <c>||v||_1</c>, <c>||v||_inf</c>, <c>||A||_F</c>. Evaluated in Milestone 3.</summary>
public sealed record NormExpr(Expr Operand, NormKind Kind, SourceSpan Span) : Expr(Span);

/// <summary><c>∂(f, x)</c> / <c>grad(f, x)</c>. Evaluated in Milestone 4.</summary>
public sealed record DerivExpr(Expr Function, Expr Wrt, SourceSpan Span) : Expr(Span);

/// <summary><c>∫[a..b, c..d, tol = 1e-8] |x, y| body</c>. Each range is a <see cref="BinaryExpr"/> with <c>..</c>/<c>..=</c>.</summary>
public sealed record IntegExpr(IReadOnlyList<Expr> Ranges, IReadOnlyList<Arg> Options, LambdaExpr Integrand, SourceSpan Span) : Expr(Span);

/// <summary><c>∑[i in 0..N] body</c> or <c>∏[i in 0..N] body</c>.</summary>
public sealed record ReduceExpr(bool IsProduct, string Variable, SourceSpan VariableSpan, Expr Range, Expr Body, SourceSpan Span) : Expr(Span);

/// <summary><c>guess(ridge = 0.05) fit (X, y)</c> (regression) or <c>judge(svm(kernel = "rbf")) fit (X, y)</c> (classification).</summary>
public sealed record FitExpr(bool IsJudge, IReadOnlyList<Arg> Config, Expr Features, Expr Targets, SourceSpan Span) : Expr(Span);

/// <summary>Placeholder left where an expression failed to parse (already reported).</summary>
public sealed record ErrorExpr(SourceSpan Span) : Expr(Span);

// ---- Statements ----

public abstract record Stmt(SourceSpan Span) : Node(Span);

/// <summary><c>x := e</c>, <c>x::T = e</c> (immutable) or <c>wag x [::T] = e</c> (mutable).</summary>
public sealed record BindStmt(string Name, SourceSpan NameSpan, TypeRef? Type, Expr Value, bool IsMutable, SourceSpan Span) : Stmt(Span);

/// <summary>Reassignment: <c>=</c>, <c>+=</c>, <c>-=</c>, <c>*=</c>, <c>/=</c>, <c>@=</c>, <c>.*=</c>, <c>./=</c>.</summary>
public sealed record AssignStmt(Expr Target, TokenKind Op, Expr Value, SourceSpan Span) : Stmt(Span);

public sealed record ExprStmt(Expr Expression, SourceSpan Span) : Stmt(Span);

public enum ParamKind { Normal, Me, PeekMe, PokeMe }

public sealed record Param(ParamKind Kind, string Name, TypeRef? Type, Expr? Default, SourceSpan Span) : Node(Span);

/// <summary>Generic parameter: <c>$R</c> (dimension variable), <c>T</c>, or <c>T: Quirk</c>.</summary>
public sealed record GenericParam(string Name, bool IsDim, string? Bound, SourceSpan Span) : Node(Span);

/// <summary>
/// <c>cook name&lt;G&gt;(params)::R = expr</c> or <c>: ... ;</c>. For an operator overload, <see cref="OpSymbol"/> holds the
/// symbol (<c>+</c>, <c>[]</c>, ...) and <see cref="Name"/> is <c>op +</c>. Exactly one of the two bodies is set.
/// </summary>
public sealed record CookDecl(
    string Name, string? OpSymbol, IReadOnlyList<GenericParam> Generics, IReadOnlyList<Param> Params,
    TypeRef? ReturnType, Expr? ExprBody, Block? BlockBody, SourceSpan Span) : Stmt(Span);

public sealed record YeetStmt(Expr? Value, SourceSpan Span) : Stmt(Span);
public sealed record RipStmt(Expr? Message, SourceSpan Span) : Stmt(Span);
public sealed record BailStmt(SourceSpan Span) : Stmt(Span);
public sealed record SkipStmt(SourceSpan Span) : Stmt(Span);

public sealed record HmmBranch(Expr Condition, Block Body, SourceSpan Span) : Node(Span);
/// <summary>An <c>hmm</c> chain: first branch is <c>hmm</c>, the rest are <c>meh</c>; one shared closing ';'.</summary>
public sealed record HmmStmt(IReadOnlyList<HmmBranch> Branches, Block? Else, SourceSpan Span) : Stmt(Span);

public enum SpinKind { Forever, While, In }
/// <summary><c>spin: ;</c>, <c>spin while c: ;</c> or <c>spin i in e: ;</c>.</summary>
public sealed record SpinStmt(SpinKind Kind, string? Variable, Expr? Subject, Block Body, SourceSpan Span) : Stmt(Span);

/// <summary>
/// A construct of a later milestone (<c>mold</c>, <c>pick</c>, <c>zone</c>, ...). The parser reports it and skips it;
/// the owning phase replaces this with real nodes.
/// </summary>
public sealed record ReservedStmt(string Keyword, SourceSpan Span) : Stmt(Span);

/// <summary>Placeholder left where a statement failed to parse (already reported).</summary>
public sealed record ErrorStmt(SourceSpan Span) : Stmt(Span);
