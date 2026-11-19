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

/// <summary>An element type with dimensions: <c>f32[3, $K, ?]</c>.</summary>
public sealed record TensorType(TypeRef Element, IReadOnlyList<Dim> Dims, SourceSpan Span) : TypeRef(Span);

/// <summary><c>&amp;T</c> (peek) or <c>~T</c> (poke).</summary>
public sealed record RefType(bool IsPoke, TypeRef Inner, SourceSpan Span) : TypeRef(Span);

/// <summary><c>fn (A, B)::R</c>.</summary>
public sealed record FuncType(IReadOnlyList<TypeRef> Params, TypeRef? Return, SourceSpan Span) : TypeRef(Span);

/// <summary>A tuple type <c>(A, B)</c> of two or more element types.</summary>
public sealed record TupleType(IReadOnlyList<TypeRef> Items, SourceSpan Span) : TypeRef(Span);

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

/// <summary>An identifier, including <c>self</c> and <c>super</c>.</summary>
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

/// <summary><c>regress(ridge = 0.05) fit (X, y)</c> (regression) or <c>classify(svm(kernel = "rbf")) fit (X, y)</c> (classification).</summary>
public sealed record FitExpr(bool IsClassify, IReadOnlyList<Arg> Config, Expr Features, Expr Targets, SourceSpan Span) : Expr(Span);

/// <summary><c>a..b by n</c> / <c>a..=b by n</c>: a range stepped by <c>n</c>. <see cref="Range"/> is always a <c>..</c>/<c>..=</c> binary.</summary>
public sealed record StepExpr(Expr Range, Expr Step, SourceSpan Span) : Expr(Span);

/// <summary><c>a then b then c</c>: disjoint iterables visited one after another (two or more parts).</summary>
public sealed record ChainExpr(IReadOnlyList<Expr> Parts, SourceSpan Span) : Expr(Span);

/// <summary>The parenthesised list after <c>in</c>: <c>x in (a, b, c)</c>. Legal only as the right side of <c>in</c>.</summary>
public sealed record ParenListExpr(IReadOnlyList<Expr> Items, SourceSpan Span) : Expr(Span);

/// <summary>Placeholder left where an expression failed to parse (already reported).</summary>
public sealed record ErrorExpr(SourceSpan Span) : Expr(Span);

// ---- Statements ----

public abstract record Stmt(SourceSpan Span) : Node(Span);

/// <summary><c>x := e</c>, <c>x::T = e</c> (immutable) or <c>var x [::T] = e</c> (mutable).</summary>
public sealed record BindStmt(string Name, SourceSpan NameSpan, TypeRef? Type, Expr Value, bool IsMutable, SourceSpan Span) : Stmt(Span);

/// <summary>Reassignment: <c>=</c>, <c>+=</c>, <c>-=</c>, <c>*=</c>, <c>/=</c>, <c>@=</c>, <c>.*=</c>, <c>./=</c>.</summary>
public sealed record AssignStmt(Expr Target, TokenKind Op, Expr Value, SourceSpan Span) : Stmt(Span);

public sealed record ExprStmt(Expr Expression, SourceSpan Span) : Stmt(Span);

public enum ParamKind { Normal, Self, PeekSelf, PokeSelf }

public sealed record Param(ParamKind Kind, string Name, TypeRef? Type, Expr? Default, SourceSpan Span) : Node(Span);

/// <summary>Generic parameter: <c>$R</c> (dimension variable), <c>T</c>, or <c>T: Trait</c>.</summary>
public sealed record GenericParam(string Name, bool IsDim, string? Bound, SourceSpan Span) : Node(Span);

/// <summary>
/// <c>fn name&lt;G&gt;(params)::R = expr</c> or <c>: ... ;</c>. For an operator overload, <see cref="OpSymbol"/> holds the
/// symbol (<c>+</c>, <c>[]</c>, ...) and <see cref="Name"/> is <c>op +</c>. Exactly one of the two bodies is set.
/// </summary>
public sealed record FnDecl(
    string Name, string? OpSymbol, IReadOnlyList<GenericParam> Generics, IReadOnlyList<Param> Params,
    TypeRef? ReturnType, Expr? ExprBody, Block? BlockBody, SourceSpan Span) : Stmt(Span);

public sealed record ReturnStmt(Expr? Value, SourceSpan Span) : Stmt(Span);
public sealed record RipStmt(Expr? Message, SourceSpan Span) : Stmt(Span);
public sealed record BreakStmt(SourceSpan Span) : Stmt(Span);
public sealed record ContinueStmt(SourceSpan Span) : Stmt(Span);

public sealed record IfBranch(Expr Condition, Block Body, SourceSpan Span) : Node(Span);
/// <summary>An <c>if</c> chain: first branch is <c>if</c>, the rest are <c>elif</c>; one shared closing ';'.</summary>
public sealed record IfStmt(IReadOnlyList<IfBranch> Branches, Block? Else, SourceSpan Span) : Stmt(Span);

public enum LoopKind { Forever, While, In }
/// <summary><c>loop: ;</c>, <c>loop while c: ;</c> or <c>loop i in e: ;</c>.</summary>
public sealed record LoopStmt(LoopKind Kind, string? Variable, Expr? Subject, Block Body, SourceSpan Span) : Stmt(Span);

/// <summary>
/// A construct of a later milestone (<c>class</c>, <c>pick</c>, <c>arena</c>, ...). The parser reports it and skips it;
/// the owning phase replaces this with real nodes.
/// </summary>
public sealed record ReservedStmt(string Keyword, SourceSpan Span) : Stmt(Span);

/// <summary>Placeholder left where a statement failed to parse (already reported).</summary>
public sealed record ErrorStmt(SourceSpan Span) : Stmt(Span);

// ---- Phase 7: enum, pick, collections ----

/// <summary>One variant inside a <c>enum</c> declaration: <c>Leaf</c> or <c>Node(T, T)</c>.</summary>
public sealed record EnumVariant(string Name, IReadOnlyList<TypeRef> Payloads, SourceSpan Span) : Node(Span);

/// <summary><c>enum Tree&lt;T&gt;: Leaf, Node(T, T) ;</c></summary>
public sealed record EnumDecl(string Name, IReadOnlyList<GenericParam> Generics, IReadOnlyList<EnumVariant> Variants, SourceSpan Span) : Stmt(Span);

// ---- Patterns ----

public abstract record Pattern(SourceSpan Span) : Node(Span);

/// <summary>The <c>_</c> wildcard: matches anything, binds nothing.</summary>
public sealed record WildcardPat(SourceSpan Span) : Pattern(Span);

/// <summary>An integer, float, bool, string or unit literal used as a pattern.</summary>
public sealed record LiteralPat(Expr Value, SourceSpan Span) : Pattern(Span);

/// <summary>A bare identifier that is not a known variant: binds the matched value to a new name.</summary>
public sealed record BindPat(string Name, SourceSpan Span) : Pattern(Span);

/// <summary>A variant pattern <c>Got(x)</c> or unit variant <c>Nada</c>.</summary>
public sealed record VariantPat(string? TypeName, string VariantName, IReadOnlyList<Pattern> Bindings, SourceSpan Span) : Pattern(Span);

/// <summary>A range pattern <c>0..10</c> or <c>0..=10</c>.</summary>
public sealed record RangePat(Expr Lo, Expr Hi, bool Inclusive, SourceSpan Span) : Pattern(Span);

/// <summary>A tuple pattern <c>(a, b)</c>.</summary>
public sealed record TuplePat(IReadOnlyList<Pattern> Items, SourceSpan Span) : Pattern(Span);

// ---- Pick ----

/// <summary>One arm of a <c>pick</c> expression: <c>Pat =&gt; expr</c> or <c>Pat: stmts</c>.</summary>
public sealed record PickArm(Pattern Pat, Expr? ExprBody, Block? BlockBody, SourceSpan Span) : Node(Span);

/// <summary>
/// <c>pick subject: Arm, Arm ;</c>. This is an expression (its value is the winning arm's body value).
/// </summary>
public sealed record PickExpr(Expr Subject, IReadOnlyList<PickArm> Arms, SourceSpan Span) : Expr(Span);

/// <summary>A <c>key => value</c> pair; only legal as an argument of <c>map(...)</c>.</summary>
public sealed record PairExpr(Expr Key, Expr Value, SourceSpan Span) : Expr(Span);

/// <summary>Tuple expression <c>(a, b)</c> (two or more items). Single-item <c>(x)</c> stays grouping.</summary>
public sealed record TupleExpr(IReadOnlyList<Expr> Items, SourceSpan Span) : Expr(Span);

/// <summary>Tuple destructuring bind: <c>(k, v) := expr</c>.</summary>
public sealed record TupleBindStmt(IReadOnlyList<string> Names, Expr Value, bool IsMutable, SourceSpan Span) : Stmt(Span);
