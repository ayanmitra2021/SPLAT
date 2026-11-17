using Splat.Core;
using Splat.Syntax;

namespace Splat.Semantics;

public enum SymbolKind { Variable, Function }

/// <summary>A name in scope.</summary>
public sealed class Symbol
{
    public string Name { get; }
    public SymbolKind Kind { get; }
    public bool IsMutable { get; }
    public SourceSpan Declared { get; }
    /// <summary>The type; <c>null</c> only for a function whose return type is still being inferred.</summary>
    public SType? Type { get; internal set; }

    // Function-only data.
    internal CookDecl? Decl { get; init; }
    internal Scope? DeclScope { get; init; }
    internal IReadOnlyList<SType>? ParamTypes { get; set; }
    internal bool Checked { get; set; }
    internal bool InProgress { get; set; }

    internal Symbol(string name, SymbolKind kind, SType? type, bool mutable, SourceSpan declared)
    {
        Name = name; Kind = kind; Type = type; IsMutable = mutable; Declared = declared;
    }
}

/// <summary>A lexical scope. Shadowing an outer name is fine; redeclaring in the same scope is an error.</summary>
public sealed class Scope
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.Ordinal);
    public Scope? Parent { get; }
    public Scope(Scope? parent = null) => Parent = parent;

    public Symbol? Lookup(string name)
    {
        for (var s = this; s is not null; s = s.Parent)
            if (s._symbols.TryGetValue(name, out var sym)) return sym;
        return null;
    }

    public Symbol? LookupLocal(string name) => _symbols.GetValueOrDefault(name);
    internal void Add(Symbol s) => _symbols[s.Name] = s;
}

/// <summary>
/// The Phase 5 static pass. Runs once over the whole program before anything executes, so type errors surface before
/// any side effects. Infers bind types, enforces annotations, arithmetic/comparison/boolean typing, function
/// signatures and return types. Never throws: problems go to the <see cref="DiagnosticEngine"/>.
/// </summary>
public sealed class TypeChecker
{
    private sealed class FnCtx
    {
        public required string Name { get; init; }
        public required SType Ret { get; init; }
    }

    private readonly DiagnosticEngine _diag;
    private readonly TypeInfo _info;
    private readonly Scope _globals;
    private FnCtx? _fn;
    private int _loopDepth;
    private HashSet<string> _typeParams = new(StringComparer.Ordinal);

    /// <param name="globals">The scope top-level declarations go into (a REPL passes a fresh child of the session scope).</param>
    /// <param name="info">Where the checker records what it learned; a REPL shares one across entries so earlier functions keep their types.</param>
    public TypeChecker(DiagnosticEngine diagnostics, Scope? globals = null, TypeInfo? info = null)
    {
        _diag = diagnostics;
        _globals = globals ?? new Scope();
        _info = info ?? new TypeInfo();
    }

    public Scope Globals => _globals;

    public static TypeInfo Check(Program program, DiagnosticEngine diagnostics) =>
        new TypeChecker(diagnostics).CheckProgram(program);

    public TypeInfo CheckProgram(Program program)
    {
        CheckStatements(program.Statements, _globals, null);
        return _info;
    }

    // ---- helpers ----

    private void Report(Diagnostic d) => _diag.Report(d);

    private static string N(SType t) => t.Name;

    private SType Mismatch(SourceSpan span, SType found, SType expected)
    {
        Report(Templates.TypeMismatch(span, N(found), N(expected)));
        return SType.Error;
    }

    private SType NotYet(SourceSpan span, string what, int phase)
    {
        Report(Templates.NotYetBuilt(span, what, phase));
        return SType.Error;
    }

    private void Declare(Scope scope, Symbol sym, SourceSpan span)
    {
        var existing = scope.LookupLocal(sym.Name);
        if (existing is not null)
        {
            Report(Templates.Redeclared(span, sym.Name, existing.Declared.Line));
            return;
        }
        scope.Add(sym);
    }

    // ---- types ----

    private SType ResolveType(TypeRef t)
    {
        switch (t)
        {
            case NamedType { Args.Count: 0 } n:
                if (Types.ByName.TryGetValue(n.Name, out var prim)) return prim;
                if (_typeParams.Contains(n.Name)) return SType.Any;
                Report(Templates.UndefinedName(n.Span, n.Name));
                return SType.Error;
            case NamedType n:
                return NotYet(n.Span, $"generic type '{n.Name}<...>'", 7);
            case TensorType tt:
                return NotYet(tt.Span, "tensor types", 12);
            case RefType r:
                return NotYet(r.Span, r.IsPoke ? "poke types ('~T')" : "peek types ('&T')", 17);
            case FuncType f:
                return new FuncSType(f.Params.Select(ResolveType).ToList(), f.Return is null ? Types.Zip : ResolveType(f.Return));
            default:
                return SType.Error;
        }
    }

    // ---- statements ----

    /// <summary>
    /// Checks a statement list. <paramref name="tailExpect"/> is the type the block must produce when it is a function body
    /// (its last expression is the return value); returns true when every path through the list definitely returns.
    /// </summary>
    private bool CheckStatements(IReadOnlyList<Stmt> stmts, Scope scope, SType? tailExpect)
    {
        // Functions are hoisted: their signatures are visible to the whole block, so mutual recursion works.
        foreach (var s in stmts)
            if (s is CookDecl d) DeclareCook(d, scope);

        bool returns = false;
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            bool last = i == stmts.Count - 1;
            bool r = s switch
            {
                ExprStmt e when last && tailExpect is not null => CheckTail(e, scope, tailExpect),
                HmmStmt h when last && tailExpect is not null => CheckHmm(h, scope, tailExpect),
                _ => CheckStatement(s, scope),
            };
            returns |= r;
        }
        return returns;
    }

    private bool CheckTail(ExprStmt e, Scope scope, SType expected)
    {
        var t = Infer(e.Expression, scope, expected);
        if (!Types.Compatible(t, expected)) Mismatch(e.Expression.Span, t, expected);
        return true;
    }

    private bool CheckStatement(Stmt s, Scope scope)
    {
        switch (s)
        {
            case BindStmt b: CheckBind(b, scope); return false;
            case AssignStmt a: CheckAssign(a, scope); return false;
            case ExprStmt e: Infer(e.Expression, scope); return false;
            case CookDecl d:
            {
                var sym = scope.LookupLocal(d.Name);
                if (sym is { Decl: not null } && ReferenceEquals(sym.Decl, d)) EnsureChecked(sym);
                return false;
            }
            case YeetStmt y: CheckYeet(y, scope); return true;
            case RipStmt r:
                if (r.Message is not null)
                {
                    var t = Infer(r.Message, scope, Types.Txt);
                    if (!Types.Compatible(t, Types.Txt)) Mismatch(r.Message.Span, t, Types.Txt);
                }
                return true;
            case BailStmt b:
                if (_loopDepth == 0) Report(Templates.LoopControlOutside(b.Span, "bail"));
                return false;
            case SkipStmt k:
                if (_loopDepth == 0) Report(Templates.LoopControlOutside(k.Span, "skip"));
                return false;
            case HmmStmt h: return CheckHmm(h, scope, null);
            case SpinStmt sp: return CheckSpin(sp, scope);
            default: return false; // ReservedStmt / ErrorStmt: the parser already reported them
        }
    }

    private void CheckBind(BindStmt b, Scope scope)
    {
        SType? declared = b.Type is null ? null : ResolveType(b.Type);
        var vt = Infer(b.Value, scope, declared);
        SType final;
        if (declared is not null)
        {
            if (!Types.Compatible(vt, declared)) Mismatch(b.Value.Span, vt, declared);
            final = declared;
        }
        else final = vt;
        Declare(scope, new Symbol(b.Name, SymbolKind.Variable, final, b.IsMutable, b.NameSpan), b.NameSpan);
    }

    private static TokenKind? CompoundBase(TokenKind op) => op switch
    {
        TokenKind.PlusAssign => TokenKind.Plus,
        TokenKind.MinusAssign => TokenKind.Minus,
        TokenKind.StarAssign => TokenKind.Star,
        TokenKind.SlashAssign => TokenKind.Slash,
        _ => null,
    };

    private void CheckAssign(AssignStmt a, Scope scope)
    {
        if (a.Op is TokenKind.AtAssign) { NotYet(a.Span, "'@='", 13); return; }
        if (a.Op is TokenKind.DotStarAssign or TokenKind.DotSlashAssign) { NotYet(a.Span, $"'{(a.Op == TokenKind.DotStarAssign ? ".*=" : "./=")}'", 12); return; }

        if (a.Target is not NameExpr name)
        {
            if (a.Target is IndexExpr) NotYet(a.Target.Span, "assigning into an index", 12);
            else if (a.Target is MemberExpr) NotYet(a.Target.Span, "assigning to a field", 8);
            else Report(Templates.NotAssignable(a.Target.Span));
            Infer(a.Value, scope);
            return;
        }

        var sym = scope.Lookup(name.Name);
        if (sym is null)
        {
            Report(Templates.UndefinedName(name.Span, name.Name));
            Infer(a.Value, scope);
            return;
        }
        _info.Set(name, sym.Type ?? SType.Error);
        if (sym.Kind != SymbolKind.Variable || !sym.IsMutable)
        {
            Report(Templates.ImmutableAssign(name.Span, name.Name));
            Infer(a.Value, scope);
            return;
        }

        var target = sym.Type ?? SType.Error;
        if (a.Op == TokenKind.Assign)
        {
            var vt = Infer(a.Value, scope, target);
            if (!Types.Compatible(vt, target)) Mismatch(a.Value.Span, vt, target);
            return;
        }

        var baseOp = CompoundBase(a.Op)!.Value;
        var rt = Infer(a.Value, scope, target);
        var result = ArithmeticType(baseOp, target, rt, a.Span, OpText(baseOp) + "=");
        if (!Types.Compatible(result, target)) Mismatch(a.Value.Span, result, target);
    }

    private void CheckYeet(YeetStmt y, Scope scope)
    {
        if (_fn is null)
        {
            Report(Templates.YeetOutside(y.Span));
            if (y.Value is not null) Infer(y.Value, scope);
            return;
        }
        if (y.Value is null)
        {
            if (!Types.Compatible(Types.Zip, _fn.Ret)) Mismatch(y.Span, Types.Zip, _fn.Ret);
            return;
        }
        var t = Infer(y.Value, scope, _fn.Ret);
        if (!Types.Compatible(t, _fn.Ret)) Mismatch(y.Value.Span, t, _fn.Ret);
    }

    private bool CheckHmm(HmmStmt h, Scope scope, SType? tailExpect)
    {
        bool all = true;
        foreach (var br in h.Branches)
        {
            var ct = Infer(br.Condition, scope);
            if (!Types.Compatible(ct, Types.Bit)) Mismatch(br.Condition.Span, ct, Types.Bit);
            all &= CheckStatements(br.Body.Statements, new Scope(scope), tailExpect);
        }
        if (h.Else is not null) all &= CheckStatements(h.Else.Statements, new Scope(scope), tailExpect);
        return h.Else is not null && all;
    }

    private bool CheckSpin(SpinStmt sp, Scope scope)
    {
        var body = new Scope(scope);
        switch (sp.Kind)
        {
            case SpinKind.While:
            {
                var ct = Infer(sp.Subject!, scope);
                if (!Types.Compatible(ct, Types.Bit)) Mismatch(sp.Subject!.Span, ct, Types.Bit);
                break;
            }
            case SpinKind.In:
            {
                var st = Infer(sp.Subject!, scope);
                SType elem = SType.Error;
                if (ElementOf(st) is { } e) elem = e;
                else if (st.IsAny) elem = SType.Any;
                else if (!st.IsError)
                    Report(Templates.TypeMismatch(sp.Subject!.Span, N(st), NotIterable));
                Declare(body, new Symbol(sp.Variable!, SymbolKind.Variable, elem, false, sp.Subject!.Span), sp.Subject.Span);
                break;
            }
        }

        _loopDepth++;
        CheckStatements(sp.Body.Statements, body, null);
        _loopDepth--;

        // `spin:` with no `bail` never falls out the bottom, so a function ending in one needs no trailing return.
        return sp.Kind == SpinKind.Forever && !ContainsBail(sp.Body.Statements);
    }

    private const string NotIterable = "something iterable (a range, a txt, ...)";

    /// <summary>
    /// The one notion of "iterable" in the checker: the element type a value yields in <c>spin</c>, <c>then</c> chains and
    /// <c>in</c>, or null when the type cannot be iterated. Later phases add lists, sets, maps and tensors here.
    /// </summary>
    private static SType? ElementOf(SType t) => t switch
    {
        RangeSType r => r.Elem,
        ChainSType c => c.Elem,
        PrimType { Kind: Prim.Txt } => Types.Char, // a txt yields one char per Unicode scalar
        _ => null,
    };

    /// <summary>The value of an integer literal (optionally negated), if the expression is one.</summary>
    private static bool TryConstInt(Expr e, out Int128 value)
    {
        switch (e)
        {
            case IntLiteral i: value = i.Value; return true;
            case UnaryExpr { Op: TokenKind.Minus, Operand: IntLiteral i }: value = -(Int128)i.Value; return true;
            default: value = 0; return false;
        }
    }

    private static bool ContainsBail(IReadOnlyList<Stmt> stmts)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case BailStmt: return true;
                case HmmStmt h:
                    if (h.Branches.Any(b => ContainsBail(b.Body.Statements)) || (h.Else is not null && ContainsBail(h.Else.Statements))) return true;
                    break;
                // A nested spin owns its own bails.
            }
        }
        return false;
    }

    // ---- functions ----

    private void DeclareCook(CookDecl d, Scope scope)
    {
        if (d.OpSymbol is not null)
        {
            NotYet(d.Span, $"operator overload '{d.OpSymbol}'", 8);
            return;
        }

        var outerParams = _typeParams;
        _typeParams = new HashSet<string>(outerParams, StringComparer.Ordinal);
        foreach (var g in d.Generics) _typeParams.Add(g.Name);

        var ptypes = new List<SType>();
        foreach (var p in d.Params)
        {
            if (p.Kind != ParamKind.Normal)
            {
                NotYet(p.Span, "'me' parameters", 8);
                ptypes.Add(SType.Error);
                continue;
            }
            if (p.Type is null)
            {
                Report(Templates.CannotInfer(p.Span, $"the type of parameter '{p.Name}'", $"Annotate it: {p.Name}::i32 (or another type)."));
                ptypes.Add(SType.Error);
            }
            else ptypes.Add(ResolveType(p.Type));
        }

        SType? ret = d.ReturnType is not null ? ResolveType(d.ReturnType)
            : d.BlockBody is not null ? Types.Zip
            : null; // `cook f(x) = expr` with no annotation: inferred from the body
        _typeParams = outerParams;

        var sym = new Symbol(d.Name, SymbolKind.Function, ret is null ? null : new FuncSType(ptypes, ret), false, d.Span)
        {
            Decl = d,
            DeclScope = scope,
            ParamTypes = ptypes,
        };
        // Generic parameters stay visible while the body is checked.
        _genericScopes[d] = d.Generics.Select(g => g.Name).ToList();
        if (sym.Type is FuncSType ft) _info.SetCook(d, ft);
        Declare(scope, sym, d.Span);
    }

    private readonly Dictionary<CookDecl, List<string>> _genericScopes = new(ReferenceEqualityComparer.Instance);

    /// <summary>Checks a function body at most once (on demand when another function needs an inferred return type).</summary>
    private void EnsureChecked(Symbol sym)
    {
        if (sym.Checked || sym.InProgress || sym.Decl is null) return;
        sym.InProgress = true;
        CheckCookBody(sym);
        sym.InProgress = false;
        sym.Checked = true;
    }

    private void CheckCookBody(Symbol sym)
    {
        var d = sym.Decl!;
        var outerFn = _fn;
        int outerLoop = _loopDepth;
        var outerParams = _typeParams;
        _typeParams = new HashSet<string>(outerParams, StringComparer.Ordinal);
        foreach (var g in _genericScopes.GetValueOrDefault(d) ?? []) _typeParams.Add(g);

        var scope = new Scope(sym.DeclScope);
        var ptypes = sym.ParamTypes!;
        for (int i = 0; i < d.Params.Count; i++)
        {
            var p = d.Params[i];
            if (p.Default is not null)
            {
                var dt = Infer(p.Default, sym.DeclScope!, ptypes[i]);
                if (!Types.Compatible(dt, ptypes[i])) Mismatch(p.Default.Span, dt, ptypes[i]);
            }
            Declare(scope, new Symbol(p.Name, SymbolKind.Variable, ptypes[i], false, p.Span), p.Span);
        }

        _loopDepth = 0;
        if (d.ExprBody is not null)
        {
            var declared = sym.Type is FuncSType f ? f.Ret : null;
            _fn = new FnCtx { Name = d.Name, Ret = declared ?? SType.Any };
            var bt = Infer(d.ExprBody, scope, declared);
            if (declared is null)
            {
                var ft = new FuncSType(ptypes, bt);
                sym.Type = ft;
                _info.SetCook(d, ft);
            }
            else if (!Types.Compatible(bt, declared)) Mismatch(d.ExprBody.Span, bt, declared);
        }
        else if (d.BlockBody is not null)
        {
            var ret = ((FuncSType)sym.Type!).Ret;
            _fn = new FnCtx { Name = d.Name, Ret = ret };
            bool needsValue = !(ret is PrimType { Kind: Prim.Zip }) && !ret.IsError && !ret.IsAny;
            bool returns = CheckStatements(d.BlockBody.Statements, scope, needsValue ? ret : null);
            if (needsValue && !returns) Report(Templates.MissingReturn(d.Span, d.Name, N(ret)));
        }

        _fn = outerFn;
        _loopDepth = outerLoop;
        _typeParams = outerParams;
    }

    /// <summary>Resolves a function symbol's type, checking its body first when the return type is being inferred.</summary>
    private SType FunctionType(Symbol sym, SourceSpan useSpan)
    {
        if (sym.Type is not null) return sym.Type;
        if (sym.InProgress)
        {
            Report(Templates.CannotInfer(useSpan, $"the return type of '{sym.Name}', which calls itself",
                $"Declare it: cook {sym.Name}(...)::T = ..."));
            return SType.Error;
        }
        EnsureChecked(sym);
        return sym.Type ?? SType.Error;
    }

    // ---- expressions ----

    private SType Infer(Expr e, Scope scope, SType? hint = null)
    {
        var t = InferCore(e, scope, hint);
        _info.Set(e, t);
        return t;
    }

    private SType InferCore(Expr e, Scope scope, SType? hint)
    {
        switch (e)
        {
            case IntLiteral i: return IntLit(i, hint, negated: false);
            case FloatLiteral:
                return hint is PrimType { Kind: var k } && Types.IsFloat(k) ? hint! : Types.Mid;
            case CharLiteral: return Types.Char;
            case BoolLiteral: return Types.Bit;
            case UnitLiteral: return Types.Zip;
            case StringTemplate s: return CheckTemplate(s, scope);
            case NameExpr n: return CheckName(n, scope);
            case UnaryExpr u: return CheckUnary(u, scope, hint);
            case BinaryExpr b: return CheckBinary(b, scope, hint);
            case CallExpr c: return CheckCall(c, scope, hint);
            case LambdaExpr l: return CheckLambda(l, scope, hint);
            case StepExpr st: return CheckStep(st, scope, hint);
            case ChainExpr ch: return CheckChain(ch, scope, hint);
            case ParenListExpr pl:
                foreach (var item in pl.Items) Infer(item, scope);
                Report(Templates.UnexpectedToken(pl.Span, "a comma list", "a single expression ('(a, b, c)' lists are only legal right after 'in')"));
                return SType.Error;
            case ErrorExpr: return SType.Error;

            case PostfixExpr p: return NotYet(p.Span, "transpose", 13);
            case IndexExpr ix: return NotYet(ix.Span, "indexing", 12);
            case FullSlice fs: return NotYet(fs.Span, "slicing", 12);
            case ListLiteral ll: return NotYet(ll.Span, "list/tensor literals", 12);
            case MemberExpr m: return NotYet(m.Span, "member access", 8);
            case NormExpr nm: return NotYet(nm.Span, "norms", 14);
            case DerivExpr dv: return NotYet(dv.Span, "'∂'", 15);
            case IntegExpr ig: return NotYet(ig.Span, "'∫'", 14);
            case ReduceExpr rd: return NotYet(rd.Span, rd.IsProduct ? "'∏'" : "'∑'", 14);
            case FitExpr ft: return NotYet(ft.Span, ft.IsJudge ? "'judge'" : "'guess'", 15);
            default: return SType.Error;
        }
    }

    private SType IntLit(IntLiteral lit, SType? hint, bool negated)
    {
        var t = hint is PrimType { Kind: var k } && Types.IsInt(k) ? (PrimType)hint : Types.I32;
        Int128 limit = Types.Max(t.Kind) + (negated && Types.IsSigned(t.Kind) ? 1 : 0);
        if ((Int128)lit.Value > limit)
        {
            Report(Templates.BadLiteral(lit.Span,
                $"{lit.Value} does not fit in a '{t.Name}' (max {Types.Describe(Types.Max(t.Kind))}).",
                t.Kind == Prim.I32 ? "Annotate a wider type, for example 'x::i64 = ...'." : "Use a wider type."));
            return SType.Error;
        }
        return t;
    }

    private SType CheckTemplate(StringTemplate s, Scope scope)
    {
        foreach (var part in s.Parts)
        {
            if (part is not TemplateExpr te) continue;
            var t = Infer(te.Value, scope);
            if (te.Format is null || te.Format.Length == 0) continue;
            if (!FormatSpec.TryParse(te.Format, out var spec, out var err))
            {
                Report(Templates.BadFormat(te.Span, te.Format, err!));
                continue;
            }
            if (t.IsError || t.IsAny) continue;
            if (spec.Precision is not null && !Types.IsFloat(t))
                Report(Templates.BadFormat(te.Span, te.Format, $"sets a precision, which only floats (smol, mid, chonk) have, but this is a '{N(t)}'"));
            else if (spec.Radix is not null && !Types.IsInt(t))
                Report(Templates.BadFormat(te.Span, te.Format, $"asks for base '{spec.Radix}', which only integers have, but this is a '{N(t)}'"));
        }
        return Types.Txt;
    }

    private SType CheckName(NameExpr n, Scope scope)
    {
        if (n.Name is "me" or "elder") return NotYet(n.Span, $"'{n.Name}'", 8);
        var sym = scope.Lookup(n.Name);
        if (sym is null)
        {
            if (Prelude.IsBuiltin(n.Name)) return NotYet(n.Span, $"using '{n.Name}' as a value", 7);
            if (Types.ByName.ContainsKey(n.Name)) return NotYet(n.Span, $"using the conversion '{n.Name}' as a value", 7);
            Report(Templates.UndefinedName(n.Span, n.Name));
            return SType.Error;
        }
        return sym.Kind == SymbolKind.Function ? FunctionType(sym, n.Span) : sym.Type ?? SType.Error;
    }

    private SType CheckUnary(UnaryExpr u, Scope scope, SType? hint)
    {
        switch (u.Op)
        {
            case TokenKind.Minus:
            {
                SType t;
                if (u.Operand is IntLiteral lit)
                {
                    t = IntLit(lit, hint, negated: true);
                    _info.Set(lit, t);
                }
                else t = Infer(u.Operand, scope, hint);
                if (t.IsError || t.IsAny) return t;
                if (!Types.IsNumeric(t) || (Types.IsInt(t) && !Types.IsSigned(t)))
                {
                    Report(Templates.BadOperator(u.Span, "-", N(t), null,
                        t is PrimType { Kind: var k } && Types.IsInt(k) ? $"Unsigned '{N(t)}' cannot be negative; use a signed type such as i32 or i64." : "Negation needs a signed integer or a float."));
                    return SType.Error;
                }
                return t;
            }
            case TokenKind.Bang:
            {
                var t = Infer(u.Operand, scope);
                if (t.IsError || t.IsAny) return Types.Bit;
                if (!t.Equals(Types.Bit))
                {
                    Report(Templates.BadOperator(u.Span, "!", N(t), null, "'!' flips a 'bit'. Compare first, for example 'x != 0'."));
                    return SType.Error;
                }
                return Types.Bit;
            }
            case TokenKind.Amp:
                Infer(u.Operand, scope);
                return NotYet(u.Span, "peek ('&x')", 17);
            case TokenKind.Tilde:
                Infer(u.Operand, scope);
                return NotYet(u.Span, "poke ('~x')", 17);
            default:
                return SType.Error;
        }
    }

    /// <summary>True for expressions whose type comes from context: numeric literals and arithmetic made only of them.</summary>
    private static bool IsFlexible(Expr e) => e switch
    {
        IntLiteral or FloatLiteral => true,
        UnaryExpr { Op: TokenKind.Minus } u => IsFlexible(u.Operand),
        BinaryExpr { Op: TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or TokenKind.Percent or TokenKind.StarStar } b
            => IsFlexible(b.Left) && IsFlexible(b.Right),
        _ => false,
    };

    /// <summary>Infers both operands so that a bare literal adopts the other side's type (<c>n + 1</c> with <c>n::i64</c>).</summary>
    private (SType Left, SType Right) InferPair(Expr left, Expr right, Scope scope, SType? hint)
    {
        if (IsFlexible(left) && !IsFlexible(right))
        {
            var r = Infer(right, scope, hint);
            var l = Infer(left, scope, r);
            return (l, r);
        }
        var lt = Infer(left, scope, hint);
        var rt = Infer(right, scope, lt);
        return (lt, rt);
    }

    private static string OpText(TokenKind k) => k switch
    {
        TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Star => "*", TokenKind.Slash => "/",
        TokenKind.Percent => "%", TokenKind.StarStar => "**", TokenKind.EqEq => "==", TokenKind.BangEq => "!=",
        TokenKind.Lt => "<", TokenKind.Le => "<=", TokenKind.Gt => ">", TokenKind.Ge => ">=",
        TokenKind.And => "and", TokenKind.Or => "or", TokenKind.DotDot => "..", TokenKind.DotDotEq => "..=",
        TokenKind.PipeGreater => "|>", TokenKind.GreaterGreater => ">>", _ => k.ToString(),
    };

    private SType BadPair(SourceSpan span, string op, SType l, SType r)
    {
        string fix = Types.IsNumeric(l) && Types.IsNumeric(r)
            ? $"There is no implicit conversion between '{N(l)}' and '{N(r)}'. Convert explicitly, for example '{N(l)}(x)' or '{N(r)}(y)'."
            : $"Both sides of '{op}' must have the same type; convert one explicitly, or check the operand.";
        Report(Templates.BadOperator(span, op, N(l), N(r), fix));
        return SType.Error;
    }

    private SType ArithmeticType(TokenKind op, SType l, SType r, SourceSpan span, string opText)
    {
        if (l.IsError || r.IsError) return SType.Error;
        if (l.IsAny || r.IsAny) return l.IsAny ? r : l;
        if (!l.Equals(r)) return BadPair(span, opText, l, r);
        if (Types.IsNumeric(l)) return l;
        if (op == TokenKind.Plus && l.Equals(Types.Txt)) return Types.Txt;
        Report(Templates.BadOperator(span, opText, N(l), N(r),
            op == TokenKind.Plus ? "'+' works on numbers of one type, or on two 'txt' values (concatenation). Use a template: \"{a}{b}\"." : "Arithmetic needs numbers of one type."));
        return SType.Error;
    }

    private SType CheckBinary(BinaryExpr b, Scope scope, SType? hint)
    {
        switch (b.Op)
        {
            case TokenKind.And or TokenKind.Or:
            {
                var l = Infer(b.Left, scope);
                var r = Infer(b.Right, scope);
                if (!Types.Compatible(l, Types.Bit)) Mismatch(b.Left.Span, l, Types.Bit);
                if (!Types.Compatible(r, Types.Bit)) Mismatch(b.Right.Span, r, Types.Bit);
                return Types.Bit;
            }
            case TokenKind.PipeGreater:
                return CheckPipe(b, scope, hint);
            case TokenKind.GreaterGreater:
                Infer(b.Left, scope); Infer(b.Right, scope);
                return NotYet(b.Span, "'>>' layer composition", 16);
            case TokenKind.At or TokenKind.Backslash or TokenKind.MiddleDot:
                Infer(b.Left, scope); Infer(b.Right, scope);
                return NotYet(b.Span, $"'{(b.Op == TokenKind.At ? "@" : b.Op == TokenKind.Backslash ? "\\" : "·")}'", 13);
            case TokenKind.DotStar or TokenKind.DotSlash or TokenKind.DotPlus or TokenKind.DotMinus:
                Infer(b.Left, scope); Infer(b.Right, scope);
                return NotYet(b.Span, "elementwise operators", 12);

            case TokenKind.In:
                return CheckMembership(b, scope);

            case TokenKind.DotDot or TokenKind.DotDotEq:
            {
                var elemHint = hint is RangeSType rs ? rs.Elem : null;
                var (l, r) = InferPair(b.Left, b.Right, scope, elemHint);
                if (l.IsError || r.IsError) return SType.Error;
                if (l.IsAny || r.IsAny) return new RangeSType(SType.Any);
                if (!l.Equals(r)) return BadPair(b.Span, OpText(b.Op), l, r);
                if (!Types.IsInt(l))
                {
                    Report(Templates.BadOperator(b.Span, OpText(b.Op), N(l), N(r), "Ranges are made of integers, for example 0..10."));
                    return SType.Error;
                }
                return new RangeSType(l);
            }

            case TokenKind.EqEq or TokenKind.BangEq:
            {
                var (l, r) = InferPair(b.Left, b.Right, scope, null);
                if (l.IsError || r.IsError) return Types.Bit;
                if (l.IsAny || r.IsAny) return Types.Bit;
                if (!l.Equals(r)) { BadPair(b.Span, OpText(b.Op), l, r); return Types.Bit; }
                if (l is not PrimType)
                    Report(Templates.BadOperator(b.Span, OpText(b.Op), N(l), N(r), "Only primitive values (numbers, bit, txt, char, zip) can be compared."));
                return Types.Bit;
            }

            case TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge:
            {
                var (l, r) = InferPair(b.Left, b.Right, scope, null);
                if (l.IsError || r.IsError || l.IsAny || r.IsAny) return Types.Bit;
                if (!l.Equals(r)) { BadPair(b.Span, OpText(b.Op), l, r); return Types.Bit; }
                bool ordered = Types.IsNumeric(l) || l.Equals(Types.Txt) || l.Equals(Types.Char);
                if (!ordered)
                    Report(Templates.BadOperator(b.Span, OpText(b.Op), N(l), N(r), "Ordering works on numbers, 'txt' and 'char'."));
                return Types.Bit;
            }

            default:
            {
                var (l, r) = InferPair(b.Left, b.Right, scope, hint);
                return ArithmeticType(b.Op, l, r, b.Span, OpText(b.Op));
            }
        }
    }

    // ---- Phase 6: stepped ranges, chains, membership ----

    private SType CheckStep(StepExpr s, Scope scope, SType? hint)
    {
        var rt = Infer(s.Range, scope, hint);
        var elem = (rt as RangeSType)?.Elem;
        var st = Infer(s.Step, scope, elem is PrimType ? elem : null);
        if (rt.IsError || st.IsError) return SType.Error;
        if (elem is null) return SType.Error;
        if (!Types.Compatible(st, elem)) Mismatch(s.Step.Span, st, elem);
        else if (TryConstInt(s.Step, out var n) && n <= 0)
            Report(Templates.BadStep(s.Step.Span, n.ToString()));
        return rt;
    }

    private SType CheckChain(ChainExpr c, Scope scope, SType? hint)
    {
        SType? elem = null;
        bool failed = false;
        foreach (var part in c.Parts)
        {
            // Later parts take the first part's element type, so `0..n then 50..100` agree on integer type.
            var pt = Infer(part, scope, elem is null ? hint : new RangeSType(elem));
            if (pt.IsError) { failed = true; continue; }
            var pe = ElementOf(pt);
            if (pe is null)
            {
                if (!pt.IsAny) Report(Templates.TypeMismatch(part.Span, N(pt), NotIterable));
                failed = true;
                continue;
            }
            if (elem is null) elem = pe;
            else if (!Types.Compatible(pe, elem)) { Report(Templates.ChainMismatch(part.Span, N(pe), N(elem))); failed = true; }
        }
        return failed || elem is null ? SType.Error : new ChainSType(elem);
    }

    private SType CheckMembership(BinaryExpr b, Scope scope)
    {
        if (b.Right is ParenListExpr list)
        {
            // `x in (a, b, c)` is `x == a or x == b or x == c`, so every member must have exactly x's type.
            var (lt, first) = InferPair(b.Left, list.Items[0], scope, null);
            _info.Set(list, SType.Any);
            var lhs = lt;
            var rest = list.Items.Skip(1).Select(i => Infer(i, scope, lhs)).Prepend(first).ToList();
            if (lt.IsError) return Types.Bit;
            for (int i = 0; i < rest.Count; i++)
            {
                if (rest[i].IsError || lt.IsAny || rest[i].IsAny) continue;
                if (!lt.Equals(rest[i])) { BadPair(list.Items[i].Span, "in", lt, rest[i]); continue; }
                if (lt is not PrimType)
                    Report(Templates.BadOperator(list.Items[i].Span, "in", N(lt), N(rest[i]), "Only primitive values (numbers, bit, txt, char, zip) can be compared."));
            }
            return Types.Bit;
        }

        SType left, right;
        if (IsFlexible(b.Left))
        {
            right = Infer(b.Right, scope);
            left = Infer(b.Left, scope, ElementOf(right));
        }
        else
        {
            left = Infer(b.Left, scope);
            right = Infer(b.Right, scope, left.IsError || left.IsAny ? null : new RangeSType(left));
        }
        if (left.IsError || right.IsError) return Types.Bit;

        var elem = ElementOf(right);
        if (elem is null)
        {
            if (!right.IsAny)
                Report(Templates.TypeMismatch(b.Right.Span, N(right), "something to look inside (a range, a txt, a chain, or a list like '(a, b, c)')"));
            return Types.Bit;
        }
        if (left.IsAny) return Types.Bit;

        // `char in txt` tests a character; `txt in txt` tests a substring.
        bool ok = right.Equals(Types.Txt) ? left.Equals(Types.Char) || left.Equals(Types.Txt) : Types.Compatible(left, elem);
        if (!ok) Mismatch(b.Left.Span, left, elem);
        return Types.Bit;
    }

    private SType CheckPipe(BinaryExpr b, Scope scope, SType? hint)
    {
        // x |> f      is f(x)
        // x |> f(a)   is f(x, a)
        CallExpr call = b.Right switch
        {
            CallExpr c => new CallExpr(c.Callee, [new Arg(null, b.Left, b.Left.Span), .. c.Args], b.Span),
            _ => new CallExpr(b.Right, [new Arg(null, b.Left, b.Left.Span)], b.Span),
        };
        _info.SetPipe(b, call);
        var t = Infer(call, scope, hint);
        return t;
    }

    private SType CheckLambda(LambdaExpr l, Scope scope, SType? hint)
    {
        var hf = hint as FuncSType;
        var inner = new Scope(scope);
        var ptypes = new List<SType>();
        for (int i = 0; i < l.Params.Count; i++)
        {
            var p = l.Params[i];
            SType pt;
            if (p.Type is not null)
            {
                pt = ResolveType(p.Type);
                if (hf is not null && hf.Params.Count == l.Params.Count && !Types.Compatible(pt, hf.Params[i]))
                    Mismatch(p.Span, pt, hf.Params[i]);
            }
            else if (hf is not null && hf.Params.Count == l.Params.Count) pt = hf.Params[i];
            else if (hf is not null) pt = SType.Error; // wrong parameter count: the caller reports the mismatch once
            else
            {
                Report(Templates.CannotInfer(p.Span, $"the type of lambda parameter '{p.Name}'",
                    $"Annotate it, for example '|{p.Name}::i32| ...', or pass the lambda where a function type is expected."));
                pt = SType.Error;
            }
            ptypes.Add(pt);
            Declare(inner, new Symbol(p.Name, SymbolKind.Variable, pt, false, p.Span), p.Span);
        }

        var outerFn = _fn;
        int outerLoop = _loopDepth;
        _fn = null; _loopDepth = 0;
        var bt = Infer(l.Body, inner, hf?.Ret);
        _fn = outerFn; _loopDepth = outerLoop;
        return new FuncSType(ptypes, bt);
    }

    // ---- calls ----

    private SType CheckCall(CallExpr c, Scope scope, SType? hint)
    {
        // Conversions and prelude functions: only when no user binding shadows the name.
        if (c.Callee is NameExpr cn && scope.Lookup(cn.Name) is null)
        {
            if (Types.ByName.TryGetValue(cn.Name, out var target)) return CheckConversion(c, cn, target, scope);
            if (Prelude.IsBuiltin(cn.Name)) return CheckBuiltin(c, cn, scope);
        }

        // An immediately-invoked lambda (what `x |> |a| ...` desugars to): infer the arguments first so the lambda's
        // parameters can take their types.
        if (c.Callee is LambdaExpr lam)
        {
            var argTypes = c.Args.Select(a => Infer(a.Value, scope)).ToList();
            var ft = Infer(lam, scope, new FuncSType(argTypes, SType.Any));
            _info.Set(c.Callee, ft);
            return FinishCall(c, ft, null, scope, argTypes);
        }

        SType calleeType;
        Symbol? userFn = null;
        if (c.Callee is NameExpr fn && scope.Lookup(fn.Name) is { Kind: SymbolKind.Function } fsym)
        {
            userFn = fsym;
            calleeType = FunctionType(fsym, fn.Span);
            _info.Set(fn, calleeType);
        }
        else calleeType = Infer(c.Callee, scope);

        return FinishCall(c, calleeType, userFn, scope, null);
    }

    private SType FinishCall(CallExpr c, SType calleeType, Symbol? userFn, Scope scope, List<SType>? preInferred)
    {
        if (calleeType.IsError)
        {
            if (preInferred is null) foreach (var a in c.Args) Infer(a.Value, scope);
            return SType.Error;
        }
        if (calleeType.IsAny)
        {
            if (preInferred is null) foreach (var a in c.Args) Infer(a.Value, scope);
            return SType.Any;
        }
        if (calleeType is not FuncSType ft)
        {
            Report(Templates.NotCallable(c.Callee.Span, N(calleeType)));
            if (preInferred is null) foreach (var a in c.Args) Infer(a.Value, scope);
            return SType.Error;
        }

        var decl = userFn?.Decl;
        int total = ft.Params.Count;
        int required = total;
        if (decl is not null)
        {
            required = 0;
            for (int i = 0; i < decl.Params.Count; i++) if (decl.Params[i].Default is null) required = i + 1;
        }

        // Map each argument to a parameter: positional first, then `name = value` by parameter name.
        var map = new int[c.Args.Count];
        var bound = new bool[total];
        bool bad = false;
        int pos = 0;
        for (int i = 0; i < c.Args.Count; i++)
        {
            var a = c.Args[i];
            int target;
            if (a.Name is null) target = pos++;
            else if (decl is null)
            {
                Report(Templates.UnexpectedToken(a.Span, a.Name + " =", "a positional argument (only cook declarations accept named arguments)"));
                map[i] = -1; bad = true; continue;
            }
            else
            {
                target = decl.Params.ToList().FindIndex(p => p.Name == a.Name);
                if (target < 0)
                {
                    Report(Templates.UnexpectedToken(a.Span, a.Name, $"one of the parameters of '{decl.Name}' ({string.Join(", ", decl.Params.Select(p => p.Name))})"));
                    map[i] = -1; bad = true; continue;
                }
            }
            if (target >= total || bound[target])
            {
                if (!bad) Report(Templates.WrongArgCount(c.Span, CalleeName(c), total, c.Args.Count));
                map[i] = -1; bad = true; continue;
            }
            bound[target] = true;
            map[i] = target;
        }
        if (!bad)
        {
            for (int i = 0; i < required; i++)
                if (!bound[i]) { Report(Templates.WrongArgCount(c.Span, CalleeName(c), total, c.Args.Count)); bad = true; break; }
        }

        for (int i = 0; i < c.Args.Count; i++)
        {
            SType at;
            if (preInferred is not null) at = preInferred[i];
            else at = Infer(c.Args[i].Value, scope, map[i] >= 0 ? ft.Params[map[i]] : null);
            if (map[i] >= 0 && !Types.Compatible(at, ft.Params[map[i]])) Mismatch(c.Args[i].Value.Span, at, ft.Params[map[i]]);
        }

        if (!bad) _info.SetArgMap(c, map);
        return bad ? SType.Error : ft.Ret;
    }

    private static string CalleeName(CallExpr c) => c.Callee is NameExpr n ? n.Name : "<function>";

    private SType CheckBuiltin(CallExpr c, NameExpr name, Scope scope)
    {
        if (c.Args.Any(a => a.Name is not null))
            Report(Templates.UnexpectedToken(c.Args.First(a => a.Name is not null).Span, "a named argument", $"positional arguments for '{name.Name}'"));

        if (name.Name == "slurp")
        {
            // slurp() or slurp(prompt): reads one line from stdin and returns it as a txt.
            var sig = new FuncSType(c.Args.Count == 0 ? [] : [Types.Txt], Types.Txt);
            _info.Set(name, sig);
            if (c.Args.Count > 1)
            {
                foreach (var a in c.Args) Infer(a.Value, scope);
                Report(Templates.WrongArgCount(c.Span, "slurp", 1, c.Args.Count));
                return Types.Txt;
            }
            foreach (var a in c.Args)
            {
                var t = Infer(a.Value, scope, Types.Txt);
                if (!Types.Compatible(t, Types.Txt)) Mismatch(a.Value.Span, t, Types.Txt);
            }
            return Types.Txt;
        }

        if (name.Name == "rev")
        {
            if (c.Args.Count != 1)
            {
                foreach (var a in c.Args) Infer(a.Value, scope);
                Report(Templates.WrongArgCount(c.Span, "rev", 1, c.Args.Count));
                return SType.Error;
            }
            var t = Infer(c.Args[0].Value, scope, null);
            _info.Set(name, new FuncSType([t], t));
            if (t.IsError) return SType.Error;
            if (t is RangeSType) return t;
            if (!t.IsAny) Report(Templates.TypeMismatch(c.Args[0].Value.Span, N(t), "a range such as 0..10"));
            return SType.Error;
        }

        if (Prelude.Fixed.TryGetValue(name.Name, out var fixedSig))
        {
            _info.Set(name, fixedSig);
            if (c.Args.Count != fixedSig.Params.Count)
            {
                foreach (var a in c.Args) Infer(a.Value, scope);
                Report(Templates.WrongArgCount(c.Span, name.Name, fixedSig.Params.Count, c.Args.Count));
                return fixedSig.Ret;
            }
            for (int i = 0; i < c.Args.Count; i++)
            {
                var t = Infer(c.Args[i].Value, scope, fixedSig.Params[i]);
                if (!Types.Compatible(t, fixedSig.Params[i])) Mismatch(c.Args[i].Value.Span, t, fixedSig.Params[i]);
            }
            return fixedSig.Ret;
        }

        foreach (var a in c.Args)
        {
            var t = Infer(a.Value, scope);
            if (t is FuncSType)
                Report(Templates.TypeMismatch(a.Value.Span, N(t), "something printable (a number, bit, txt, char or zip)"));
        }
        _info.Set(name, new FuncSType([], Types.Zip));
        return Types.Zip;
    }

    private SType CheckConversion(CallExpr c, NameExpr name, PrimType target, Scope scope)
    {
        _info.Set(name, new FuncSType([SType.Any], target));
        if (c.Args.Count != 1 || c.Args[0].Name is not null)
        {
            foreach (var a in c.Args) Infer(a.Value, scope);
            Report(Templates.WrongArgCount(c.Span, target.Name, 1, c.Args.Count));
            return target;
        }

        // `i64(5)` makes the literal an i64 directly; `mid(3)` converts an i32.
        var from = Infer(c.Args[0].Value, scope, Types.IsInt(target) ? target : null);
        if (from.IsError || from.IsAny) return target;
        if (target.Kind == Prim.Txt && from is RangeSType) return target; // txt(1..4) is "1..4"
        if (from is not PrimType fp)
        {
            Report(Templates.BadOperator(c.Span, $"{target.Name}(...)", N(from), null, "Only primitive values can be converted."));
            return target;
        }

        bool ok = target.Kind switch
        {
            Prim.Txt => true,
            Prim.Char => Types.IsInt(fp.Kind) || fp.Kind == Prim.Char,
            Prim.Bit or Prim.Zip => fp.Kind == target.Kind,
            _ => Types.IsNumeric(fp.Kind) || (fp.Kind == Prim.Char && Types.IsInt(target.Kind)),
        };
        if (!ok)
            Report(Templates.BadOperator(c.Span, $"{target.Name}(...)", N(from), null,
                target.Kind == Prim.Bit ? "There is no conversion to 'bit'; compare instead, for example 'x != 0'."
                : $"There is no conversion from '{N(from)}' to '{target.Name}'."));
        return target;
    }
}
