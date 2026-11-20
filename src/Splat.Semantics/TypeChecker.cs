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
    internal FnDecl? Decl { get; init; }
    internal Scope? DeclScope { get; init; }
    internal IReadOnlyList<SType>? ParamTypes { get; set; }
    internal bool Checked { get; set; }
    internal bool InProgress { get; set; }

    // Method-only data (Phases 8-9).
    internal SType? SelfType { get; init; }
    internal ClassInfo? ClassCtx { get; init; }
    internal ParamKind SelfKind { get; init; }

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
public sealed partial class TypeChecker
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
    // Phase 7: enums and their variants (shared across REPL entries through the registry chain).
    private readonly EnumRegistry _enums;

    /// <param name="globals">The scope top-level declarations go into (a REPL passes a fresh child of the session scope).</param>
    /// <param name="info">Where the checker records what it learned; a REPL shares one across entries so earlier functions keep their types.</param>
    /// <param name="enums">The registry this program's enums are added to; a REPL passes a child of the previous entry's.</param>
    public TypeChecker(DiagnosticEngine diagnostics, Scope? globals = null, TypeInfo? info = null, EnumRegistry? enums = null)
    {
        _diag = diagnostics;
        _globals = globals ?? new Scope();
        _info = info ?? new TypeInfo();
        _enums = enums ?? new EnumRegistry(EnumRegistry.Builtin);
    }

    /// <summary>The enum registry this checker adds to (a REPL commits it when the entry succeeds).</summary>
    public EnumRegistry Enums => _enums;

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
        if (found is TensorSType fa && expected is TensorSType ea && fa.Elem.Equals(ea.Elem) && fa.Rank == ea.Rank)
        {
            // Same element type and rank: the shapes are what disagree.
            Report(Templates.GeometryCrime(span, $"Tried to shove a '{N(found)}' into a '{N(expected)}' slot. The shapes disagree ({ShapeDifference(fa, ea)}).",
                $"Pass a tensor of shape {N(expected)}, or declare the slot with '?' for the dimension that varies."));
            return SType.Error;
        }
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
            case NamedType nn:
                return ResolveNamed(nn);
            case TupleType tup:
                return new TupleSType(tup.Items.Select(ResolveType).ToList());
            case TensorType tt:
                return ResolveTensorType(tt);
            case RefType r:
                return NotYet(r.Span, r.IsPoke ? "poke types ('~T')" : "peek types ('&T')", 17);
            case FuncType f:
                return new FuncSType(f.Params.Select(ResolveType).ToList(), f.Return is null ? Types.Unit : ResolveType(f.Return));
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
        DeclareEnums(stmts);
        var typeDecls = DeclareTypes(stmts, scope);
        foreach (var s in stmts)
            if (s is FnDecl d) DeclareFn(d, scope);
        ValidateTypes(typeDecls);

        bool returns = false;
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            bool last = i == stmts.Count - 1;
            bool r = s switch
            {
                ExprStmt e when last && tailExpect is not null => CheckTail(e, scope, tailExpect),
                IfStmt h when last && tailExpect is not null => CheckIf(h, scope, tailExpect),
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
        else NoteGuard(e.Expression, t, expected);
        return true;
    }

    private bool CheckStatement(Stmt s, Scope scope)
    {
        switch (s)
        {
            case BindStmt b: CheckBind(b, scope); return false;
            case AssignStmt a: CheckAssign(a, scope); return false;
            case ExprStmt e: Infer(e.Expression, scope); return false;
            case FnDecl d:
            {
                var sym = scope.LookupLocal(d.Name);
                if (sym is { Decl: not null } && ReferenceEquals(sym.Decl, d)) EnsureChecked(sym);
                return false;
            }
            case ReturnStmt y: CheckReturn(y, scope); return true;
            case RipStmt r:
                if (r.Message is not null)
                {
                    var t = Infer(r.Message, scope, Types.Txt);
                    if (!Types.Compatible(t, Types.Txt)) Mismatch(r.Message.Span, t, Types.Txt);
                }
                return true;
            case BreakStmt b:
                if (_loopDepth == 0) Report(Templates.LoopControlOutside(b.Span, "br"));
                return false;
            case ContinueStmt k:
                if (_loopDepth == 0) Report(Templates.LoopControlOutside(k.Span, "cont"));
                return false;
            case IfStmt h: return CheckIf(h, scope, null);
            case LoopStmt sp: return CheckLoop(sp, scope);
            case EnumDecl: return false; // declared (hoisted) before the block was checked
            case TupleBindStmt tb: CheckTupleBind(tb, scope); return false;
            case ClassDecl cd: CheckClassBodies(cd); return false;
            case TraitDecl td: CheckTraitBodies(td); return false;
            case ExtendDecl ed: CheckExtendBodies(ed); return false;
            case DropStmt dr: CheckDrop(dr, scope); return false;
            case TryStmt ty: return CheckTry(ty, scope);
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
            else NoteGuard(b.Value, vt, declared);
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
        TokenKind.AtAssign => TokenKind.At,
        TokenKind.DotStarAssign => TokenKind.DotStar,
        TokenKind.DotSlashAssign => TokenKind.DotSlash,
        _ => null,
    };

    private void CheckAssign(AssignStmt a, Scope scope)
    {
        if (a.Target is not NameExpr name)
        {
            if (a.Target is IndexExpr aix) { CheckIndexAssign(a, aix, scope); return; }
            if (a.Target is MemberExpr fme) { CheckFieldAssign(a, fme, scope); return; }
            Report(Templates.NotAssignable(a.Target.Span));
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

    private void CheckReturn(ReturnStmt y, Scope scope)
    {
        if (_fn is null)
        {
            Report(Templates.ReturnOutside(y.Span));
            if (y.Value is not null) Infer(y.Value, scope);
            return;
        }
        if (y.Value is null)
        {
            if (!Types.Compatible(Types.Unit, _fn.Ret)) Mismatch(y.Span, Types.Unit, _fn.Ret);
            return;
        }
        var t = Infer(y.Value, scope, _fn.Ret);
        if (!Types.Compatible(t, _fn.Ret)) Mismatch(y.Value.Span, t, _fn.Ret);
        else NoteGuard(y.Value, t, _fn.Ret);
    }

    private bool CheckIf(IfStmt h, Scope scope, SType? tailExpect)
    {
        bool all = true;
        foreach (var br in h.Branches)
        {
            var ct = Infer(br.Condition, scope);
            if (!Types.Compatible(ct, Types.Bool)) Mismatch(br.Condition.Span, ct, Types.Bool);
            all &= CheckStatements(br.Body.Statements, new Scope(scope), tailExpect);
        }
        if (h.Else is not null) all &= CheckStatements(h.Else.Statements, new Scope(scope), tailExpect);
        return h.Else is not null && all;
    }

    private bool CheckLoop(LoopStmt sp, Scope scope)
    {
        var body = new Scope(scope);
        switch (sp.Kind)
        {
            case LoopKind.While:
            {
                var ct = Infer(sp.Subject!, scope);
                if (!Types.Compatible(ct, Types.Bool)) Mismatch(sp.Subject!.Span, ct, Types.Bool);
                break;
            }
            case LoopKind.In:
            {
                var st = Infer(sp.Subject!, scope);
                RequireStep(sp.Subject!, st);
                SType elem = SType.Error;
                if (ElementOf(st) is { } e) elem = e;
                else if (st.IsAny) elem = SType.Any;
                else if (!st.IsError)
                    Report(Templates.TypeMismatch(sp.Subject!.Span, N(st), NotIterable));
                Declare(body, new Symbol(sp.Variable!, SymbolKind.Variable, elem, false, sp.Subject!.Span), sp.Subject.Span);
                break;
            }
        }

        // Mutation-while-iterating: subject name must not be directly assigned inside the body.
        if (sp.Kind == LoopKind.In)
        {
            foreach (var root in SubjectRoots(sp.Subject!, scope).Distinct())
                if (scope.Lookup(root) is { IsMutable: true } && sp.Variable != root)
                    CheckNoMutation(sp.Body.Statements, root);
        }

        _loopDepth++;
        CheckStatements(sp.Body.Statements, body, null);
        _loopDepth--;

        // `loop:` with no `br` never falls out the bottom, so a function ending in one needs no trailing return.
        return sp.Kind == LoopKind.Forever && !ContainsBreak(sp.Body.Statements);
    }

    private const string NotIterable = "something iterable (a range, txt, List, Set, or Map)";

    /// <summary>
    /// The one notion of "iterable" in the checker: the element type a value yields in <c>loop</c>, <c>then</c> chains and
    /// <c>in</c>, or null when the type cannot be iterated. Later phases add lists, sets, maps and tensors here.
    /// </summary>
    private static SType? ElementOf(SType t) => Types.ElementOf(t);

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

    private static bool ContainsBreak(IReadOnlyList<Stmt> stmts)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case BreakStmt: return true;
                case TryStmt ty:
                    if (ContainsBreak(ty.Body.Statements) || ContainsBreak(ty.Handler.Statements)) return true;
                    break;
                case IfStmt h:
                    if (h.Branches.Any(b => ContainsBreak(b.Body.Statements)) || (h.Else is not null && ContainsBreak(h.Else.Statements))) return true;
                    break;
                // A nested loop owns its own breaks.
            }
        }
        return false;
    }

    // ---- functions ----

    private void DeclareFn(FnDecl d, Scope scope)
    {
        if (d.OpSymbol is not null)
        {
            Report(Templates.UnexpectedToken(d.Span, $"fn op {d.OpSymbol}", "an operator overload inside a class or extend block"));
            return;
        }

        var saved = PushGenerics(d.Generics);
        var genv = CurrentGenerics();

        var ptypes = new List<SType>();
        foreach (var p in d.Params)
        {
            if (p.Kind != ParamKind.Normal)
            {
                Report(Templates.SelfOutside(p.Span, "self"));
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
            : d.BlockBody is not null ? Types.Unit
            : null; // `fn f(x) = expr` with no annotation: inferred from the body
        PopGenerics(saved);

        var sym = new Symbol(d.Name, SymbolKind.Function, ret is null ? null : new FuncSType(ptypes, ret), false, d.Span)
        {
            Decl = d,
            DeclScope = scope,
            ParamTypes = ptypes,
        };
        // Generic parameters (and their bounds) stay visible while the body is checked.
        _fnGeneric[d] = genv;
        var declBounds = d.Generics.Where(g => genv.Bounds.ContainsKey(g.Name)).Select(g => (g.Name, genv.Bounds[g.Name])).ToList();
        if (declBounds.Count > 0) _declBounds[d] = declBounds;
        if (sym.Type is FuncSType ft) _info.SetFn(d, ft);
        Declare(scope, sym, d.Span);
    }

    /// <summary>Checks a function body at most once (on demand when another function needs an inferred return type).</summary>
    private void EnsureChecked(Symbol sym)
    {
        if (sym.Checked || sym.InProgress || sym.Decl is null) return;
        sym.InProgress = true;
        CheckFnBody(sym);
        sym.InProgress = false;
        sym.Checked = true;
    }

    private void CheckFnBody(Symbol sym)
    {
        var d = sym.Decl!;
        var outerFn = _fn;
        int outerLoop = _loopDepth;
        var outerParams = _typeParams;
        var outerBounds = _typeBounds;
        var outerClass = _curClass;
        var outerMethod = _curMethod;
        if (_fnGeneric.TryGetValue(d, out var genv)) { _typeParams = genv.Params; _typeBounds = genv.Bounds; }
        _curClass = sym.ClassCtx;
        _curMethod = sym.SelfType is null ? null : sym;

        var scope = new Scope(sym.DeclScope);
        var ptypes = sym.ParamTypes!;
        int pi = 0;
        foreach (var p in d.Params)
        {
            if (p.Kind != ParamKind.Normal)
            {
                // `self`: writable only through ~self (and in init/deinit).
                bool writable = p.Kind == ParamKind.PokeSelf || d.Name is "init" or "deinit";
                Declare(scope, new Symbol("self", SymbolKind.Variable, sym.SelfType ?? SType.Error, writable, p.Span), p.Span);
                continue;
            }
            var ptype = pi < ptypes.Count ? ptypes[pi] : SType.Error;
            pi++;
            if (p.Default is not null)
            {
                var dt = Infer(p.Default, sym.DeclScope!, ptype);
                if (!Types.Compatible(dt, ptype)) Mismatch(p.Default.Span, dt, ptype);
            }
            Declare(scope, new Symbol(p.Name, SymbolKind.Variable, ptype, false, p.Span), p.Span);
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
                _info.SetFn(d, ft);
            }
            else if (!Types.Compatible(bt, declared)) Mismatch(d.ExprBody.Span, bt, declared);
        }
        else if (d.BlockBody is not null)
        {
            var ret = ((FuncSType)sym.Type!).Ret;
            _fn = new FnCtx { Name = d.Name, Ret = ret };
            bool needsValue = !(ret is PrimType { Kind: Prim.Unit }) && !ret.IsError && !ret.IsAny;
            bool returns = CheckStatements(d.BlockBody.Statements, scope, needsValue ? ret : null);
            if (needsValue && !returns) Report(Templates.MissingReturn(d.Span, d.Name, N(ret)));
        }

        _fn = outerFn;
        _loopDepth = outerLoop;
        _typeParams = outerParams;
        _typeBounds = outerBounds;
        _curClass = outerClass;
        _curMethod = outerMethod;
    }

    /// <summary>Resolves a function symbol's type, checking its body first when the return type is being inferred.</summary>
    private SType FunctionType(Symbol sym, SourceSpan useSpan)
    {
        if (sym.Type is not null) return sym.Type;
        if (sym.InProgress)
        {
            Report(Templates.CannotInfer(useSpan, $"the return type of '{sym.Name}', which calls itself",
                $"Declare it: fn {sym.Name}(...)::T = ..."));
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
                return ScalarHint(hint) is PrimType { Kind: var k } && Types.IsFloat(k) ? ScalarHint(hint)! : Types.F32;
            case CharLiteral: return Types.Char;
            case BoolLiteral: return Types.Bool;
            case UnitLiteral: return Types.Unit;
            case StringTemplate s: return CheckTemplate(s, scope);
            case NameExpr n: return CheckName(n, scope, hint);
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
            case PairExpr pr:
                Infer(pr.Key, scope); Infer(pr.Value, scope);
                Report(Templates.UnexpectedToken(pr.Span, "=>", "a key => value pair only inside map(...)"));
                return SType.Error;

            case TupleExpr te: return CheckTuple(te, scope, hint);
            case ListLiteral ll: return CheckListLiteral(ll, scope, hint);
            case IndexExpr ix: return CheckIndex(ix, scope, hint);
            case PickExpr pk: return CheckPick(pk, scope, hint);
            case PostfixExpr p: return CheckTranspose(p, scope);
            case FullSlice fs:
                Report(Templates.UnexpectedToken(fs.Span, ":", "a ':' only inside an index, as in A[:, 0]"));
                return SType.Error;
            case MemberExpr m: return CheckMember(m, scope);
            case TemporalLiteral tl: return TemporalType(tl);
            case NormExpr nm: return CheckNorm(nm, scope, hint);
            case DerivExpr dv: return NotYet(dv.Span, "'∂'", 15);
            case IntegExpr ig: return CheckIntegral(ig, scope, hint);
            case ReduceExpr rd: return CheckReduce(rd, scope, hint);
            case FitExpr ft: return NotYet(ft.Span, ft.IsClassify ? "'classify'" : "'regress'", 15);
            default: return SType.Error;
        }
    }

    private SType IntLit(IntLiteral lit, SType? hint, bool negated)
    {
        hint = ScalarHint(hint);
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
            if (CheckTemporalFormat(te, t)) continue;
            if (!FormatSpec.TryParse(te.Format, out var spec, out var err))
            {
                Report(Templates.BadFormat(te.Span, te.Format, err!));
                continue;
            }
            if (t.IsError || t.IsAny) continue;
            if (spec.Precision is not null && !Types.IsFloat(t) && !(t is TensorSType { Elem: var tel } && Types.IsFloat(tel)))
                Report(Templates.BadFormat(te.Span, te.Format, $"sets a precision, which only floats (f16, f32, f64) have, but this is a '{N(t)}'"));
            else if (spec.Radix is not null && !Types.IsInt(t))
                Report(Templates.BadFormat(te.Span, te.Format, $"asks for base '{spec.Radix}', which only integers have, but this is a '{N(t)}'"));
        }
        return Types.Txt;
    }

    private SType CheckName(NameExpr n, Scope scope, SType? hint = null)
    {
        if (n.Name == "self")
        {
            if (scope.Lookup("self") is { } selfSym) return selfSym.Type ?? SType.Error;
            Report(Templates.SelfOutside(n.Span, "self"));
            return SType.Error;
        }
        if (n.Name == "super")
        {
            Report(Templates.UnexpectedToken(n.Span, "super", "'super.method(...)' ('super' is not a value on its own)"));
            return SType.Error;
        }
        var sym = scope.Lookup(n.Name);
        if (sym is null && _enums.FindVariant(n.Name) is { } variantRef) return CheckVariantName(variantRef, hint, n.Span);
        if (sym is null)
        {
            if (_enums.FindClass(n.Name) is not null)
            {
                Report(Templates.UnexpectedToken(n.Span, n.Name, $"a call such as {n.Name}(...); class names are not first-class values"));
                return SType.Error;
            }
            if (Prelude.IsBuiltin(n.Name)) return NotYet(n.Span, $"using '{n.Name}' as a value", 12);
            if (Types.ByName.ContainsKey(n.Name)) return NotYet(n.Span, $"using the conversion '{n.Name}' as a value", 12);
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
                if (t is TensorSType nt && Types.IsFloat(nt.Elem)) return nt;
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
                if (t.IsError || t.IsAny) return Types.Bool;
                if (!t.Equals(Types.Bool))
                {
                    Report(Templates.BadOperator(u.Span, "!", N(t), null, "'!' flips a 'bool'. Compare first, for example 'x != 0'."));
                    return SType.Error;
                }
                return Types.Bool;
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
        TokenKind.PipeGreater => "|>", TokenKind.GreaterGreater => ">>",
        TokenKind.At => "@", TokenKind.DotStar => ".*", TokenKind.DotSlash => "./", TokenKind.DotPlus => ".+", TokenKind.DotMinus => ".-",
        TokenKind.Backslash => "\\", TokenKind.MiddleDot => "·", _ => k.ToString(),
    };

    private SType BadPair(SourceSpan span, string op, SType l, SType r)
    {
        string fix = Types.IsNumeric(l) && Types.IsNumeric(r)
            ? $"There is no implicit conversion between '{N(l)}' and '{N(r)}'. Convert explicitly, for example '{N(l)}(x)' or '{N(r)}(y)'."
            : $"Both sides of '{op}' must have the same type; convert one explicitly, or check the operand.";
        Report(Templates.BadOperator(span, op, N(l), N(r), fix));
        return SType.Error;
    }

    /// <summary>True when an error was already reported somewhere inside the type (a <c>List&lt;error&gt;</c>, say).</summary>
    private static bool ContainsError(SType t) => t switch
    {
        _ when t.IsError => true,
        ListSType l => ContainsError(l.Elem),
        SetSType s => ContainsError(s.Elem),
        MapSType m => ContainsError(m.Key) || ContainsError(m.Val),
        TupleSType tu => tu.Items.Any(ContainsError),
        EnumInstanceSType mi => mi.Args.Any(ContainsError),
        ClassSType c => c.Args.Any(ContainsError),
        TraitSType tr => tr.Args.Any(ContainsError),
        IterSType it => ContainsError(it.Elem),
        _ => false,
    };

    private SType ArithmeticType(TokenKind op, SType l, SType r, SourceSpan span, string opText)
    {
        if (ContainsError(l) || ContainsError(r)) return SType.Error;
        if (l is ClassSType && TryOverload(op, l, r, span, out var overloaded)) return overloaded;
        if (l.IsAny || r.IsAny) return l.IsAny ? r : l;
        if (l is TensorSType || r is TensorSType) return TensorArithmetic(op, l, r, span, opText);
        if (Types.IsTemporal(l) || Types.IsTemporal(r)) return TemporalArithmetic(op, l, r, span, opText);
        // A float raised to an integer power keeps the float type (the spec's own `||self||_F ** 2`).
        if (op == TokenKind.StarStar && Types.IsFloat(l) && Types.IsInt(r)) return l;
        if (!l.Equals(r)) return BadPair(span, opText, l, r);
        if (Types.IsNumeric(l)) return l;
        if (op == TokenKind.Plus && l.Equals(Types.Txt)) return Types.Txt;
        if (op == TokenKind.Plus && l is ListSType) return l; // concatenation
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
                if (!Types.Compatible(l, Types.Bool)) Mismatch(b.Left.Span, l, Types.Bool);
                if (!Types.Compatible(r, Types.Bool)) Mismatch(b.Right.Span, r, Types.Bool);
                return Types.Bool;
            }
            case TokenKind.PipeGreater:
                return CheckPipe(b, scope, hint);
            case TokenKind.GreaterGreater:
                Infer(b.Left, scope); Infer(b.Right, scope);
                return NotYet(b.Span, "'>>' layer composition", 16);
            case TokenKind.At or TokenKind.Backslash or TokenKind.MiddleDot:
            {
                var al = Infer(b.Left, scope);
                var ar = Infer(b.Right, scope, al is ClassSType ? null : al);
                if (b.Op == TokenKind.At && al is ClassSType && TryOverload(b.Op, al, ar, b.Span, out var ores)) return ores;
                return CheckTensorOperator(b, al, ar);
            }
            case TokenKind.DotStar or TokenKind.DotSlash or TokenKind.DotPlus or TokenKind.DotMinus:
            {
                var (dl, dr) = InferPair(b.Left, b.Right, scope, hint);
                return CheckTensorOperator(b, dl, dr);
            }

            case TokenKind.In:
                return CheckMembership(b, scope);

            case TokenKind.DotDot or TokenKind.DotDotEq:
            {
                var elemHint = hint is RangeSType rs ? rs.Elem : null;
                var (l, r) = InferPair(b.Left, b.Right, scope, elemHint);
                if (l.IsError || r.IsError) return SType.Error;
                if (Types.IsTemporalPoint(l) || Types.IsTemporalPoint(r))
                    return l.IsAny || r.IsAny ? new RangeSType(SType.Any) : CheckTemporalRange(b, l, r);
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
                if (ContainsError(l) || ContainsError(r)) return Types.Bool;
                if (l is ClassSType && TryOverload(b.Op, l, r, b.Span, out var oeq)) return oeq;
                if (l.IsAny || r.IsAny) return Types.Bool;
                if (l is TensorSType lte && r is TensorSType rte)
                {
                    // Whole-tensor comparison: any two tensors of one element type may be compared; different shapes are simply unequal.
                    if (!lte.Elem.Equals(rte.Elem)) BadPair(b.Span, OpText(b.Op), l, r);
                    return Types.Bool;
                }
                if (!(l.Equals(r) || (Types.Compatible(l, r) && Types.Compatible(r, l)))) { BadPair(b.Span, OpText(b.Op), l, r); return Types.Bool; }
                if (!Types.IsEquatable(l))
                    Report(Templates.BadOperator(b.Span, OpText(b.Op), N(l), N(r), l is ClassSType
                        ? $"Define 'fn op ==(self, other::{N(l)})::bool' in the class to compare its objects."
                        : "Only values made of numbers, bool, txt, char, unit, tuples, lists, sets, maps, enums and dates/times can be compared."));
                return Types.Bool;
            }

            case TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge:
            {
                var (l, r) = InferPair(b.Left, b.Right, scope, null);
                if (l.IsError || r.IsError) return Types.Bool;
                if (l is ClassSType && b.Op is TokenKind.Lt or TokenKind.Gt && TryOverload(b.Op, l, r, b.Span, out var olt)) return olt;
                if (l.IsAny || r.IsAny) return Types.Bool;
                if (l is TensorSType || r is TensorSType)
                {
                    Report(Templates.BadOperator(b.Span, OpText(b.Op), N(l), N(r),
                        "A tensor has no single order. Reduce it first: 'max(A) < 3.0', 'min(A) >= 0.0', or compare elements with indexing."));
                    return Types.Bool;
                }
                if (!l.Equals(r)) { BadPair(b.Span, OpText(b.Op), l, r); return Types.Bool; }
                bool ordered = Types.IsNumeric(l) || l.Equals(Types.Txt) || l.Equals(Types.Char) || Types.IsTemporal(l);
                if (!ordered)
                    Report(Templates.BadOperator(b.Span, OpText(b.Op), N(l), N(r), "Ordering works on numbers, 'txt' and 'char'."));
                return Types.Bool;
            }

            default:
            {
                var (l, r) = InferPair(b.Left, b.Right, scope, hint);
                if (b.Op == TokenKind.StarStar && Types.IsInt(l) && Types.IsInt(r) && TryConstInt(b.Right, out var exp) && exp < 0)
                {
                    Report(Templates.BadOperator(b.Span, "**", N(l), N(r),
                        "An integer cannot be raised to a negative power (the answer is not an integer). Use a float base, for example 2.0 ** -1.0."));
                    return SType.Error;
                }
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
        if (Types.IsTemporalPoint(elem)) return CheckTemporalStep(s, elem, st);
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
            RequireStep(part, pt);
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
            if (lt.IsError) return Types.Bool;
            for (int i = 0; i < rest.Count; i++)
            {
                if (rest[i].IsError || lt.IsAny || rest[i].IsAny) continue;
                if (!lt.Equals(rest[i])) { BadPair(list.Items[i].Span, "in", lt, rest[i]); continue; }
                if (lt is not PrimType)
                    Report(Templates.BadOperator(list.Items[i].Span, "in", N(lt), N(rest[i]), "Only primitive values (numbers, bool, txt, char, unit) can be compared."));
            }
            return Types.Bool;
        }

        SType left, right;
        if (IsFlexible(b.Left))
        {
            right = Infer(b.Right, scope);
            left = Infer(b.Left, scope, MembershipElement(right));
        }
        else
        {
            left = Infer(b.Left, scope);
            right = Infer(b.Right, scope, left.IsError || left.IsAny ? null : new RangeSType(left));
        }
        if (left.IsError || right.IsError) return Types.Bool;

        var elem = MembershipElement(right);
        if (elem is null)
        {
            if (!right.IsAny)
                Report(Templates.TypeMismatch(b.Right.Span, N(right), "something to look inside (a range, txt, chain, List, Set, Map, or a literal like '(a, b, c)')"));
            return Types.Bool;
        }
        if (left.IsAny) return Types.Bool;

        // `char in txt` tests a character; `txt in txt` tests a substring.
        bool ok = right.Equals(Types.Txt) ? left.Equals(Types.Char) || left.Equals(Types.Txt) : Types.Compatible(left, elem);
        if (!ok) Mismatch(b.Left.Span, left, elem);
        return Types.Bool;
    }

    /// <summary>What <c>x in S</c> compares <c>x</c> with: a map is searched by key, everything else by element.</summary>
    private static SType? MembershipElement(SType right) =>
        right is MapSType mp ? mp.Key : right is TensorSType ts ? ts.Elem : ElementOf(right);

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
        // Variant constructors: Got(x), Node(l, v, r).
        if (c.Callee is NameExpr vn && scope.Lookup(vn.Name) is null && _enums.FindVariant(vn.Name) is { } vref)
            return CheckConstructorCall(c, vn, vref, scope, hint);
        // Class construction: Dog("Rex").
        if (c.Callee is NameExpr cnm && scope.Lookup(cnm.Name) is null && _enums.FindClass(cnm.Name) is { } cls)
            return CheckConstruct(c, cnm, cls, scope, hint);
        // Methods: xs.push(1), d.speak(), super.init(...), Socket.tcp(...).
        if (c.Callee is MemberExpr me) return CheckMethodCall(c, me, scope);

        // Conversions and prelude functions: only when no user binding shadows the name.
        if (c.Callee is NameExpr cn && scope.Lookup(cn.Name) is null)
        {
            // The prelude wins over a conversion of the same name: `zip(a, b)` pairs two iterables, though `zip` is also a type.
            if (Prelude.IsBuiltin(cn.Name)) return CheckBuiltin(c, cn, scope, hint);
            if (Types.ByName.TryGetValue(cn.Name, out var target)) return CheckConversion(c, cn, target, scope);
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

        return FinishCall(c, calleeType, userFn, scope, null, hint);
    }

    private SType FinishCall(CallExpr c, SType calleeType, Symbol? userFn, Scope scope, List<SType>? preInferred, SType? hint = null)
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
                Report(Templates.UnexpectedToken(a.Span, a.Name + " =", "a positional argument (only fn declarations accept named arguments)"));
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

        // A generic callee: every type parameter becomes an inference variable for this call site, bound by unifying the
        // arguments (and the expected type) with the parameter types. That verifies the instantiation.
        Dictionary<string, SType>? vars = null;
        IReadOnlyList<SType> ptypes = ft.Params;
        IReadOnlyList<SType>? hintParams = null;
        SType retType = ft.Ret;
        var dimNames = new List<string>();
        foreach (var p in ft.Params) Shapes.CollectVars(p, dimNames);
        Shapes.CollectVars(ft.Ret, dimNames);
        if (decl is not null && (decl.Generics.Count > 0 || dimNames.Count > 0))
        {
            vars = decl.Generics.Where(g => !g.IsDim).ToDictionary(g => g.Name, g => (SType)new InferVarSType(g.Name), StringComparer.Ordinal);
            // Shape monomorphisation: every `$Dim` of the signature gets a fresh inference dimension for THIS call site.
            foreach (var dn in dimNames.Concat(decl.Generics.Where(g => g.IsDim).Select(g => g.Name)))
                vars[dn] = new DimBindingSType(new InferSDim(dn));
            ptypes = ft.Params.Select(p => Types.Substitute(p, vars)).ToList();
            retType = Types.Substitute(ft.Ret, vars);
            hintParams = HintInstance(decl.Generics.Select(g => g.Name).ToList(), ft.Params, ft.Ret, hint);
        }

        for (int pass = 0; pass < (vars is null ? 1 : 2); pass++)
        {
            for (int i = 0; i < c.Args.Count; i++)
            {
                if (vars is not null && (c.Args[i].Value is LambdaExpr) != (pass == 1)) continue;
                SType at;
                if (preInferred is not null) at = preInferred[i];
                else at = Infer(c.Args[i].Value, scope, map[i] >= 0 ? (vars is null ? ptypes[map[i]] : ArgHint(ptypes[map[i]], hintParams?[map[i]])) : null);
                if (map[i] >= 0 && !Types.Unify(ptypes[map[i]], at))
                    Mismatch(c.Args[i].Value.Span, at, vars is null ? ptypes[map[i]] : Types.Resolve(ptypes[map[i]]));
                else if (map[i] >= 0) NoteGuard(c.Args[i].Value, at, Types.Resolve(ptypes[map[i]]));
            }
        }

        if (vars is not null && decl is not null) EnforceBounds(c, decl, vars);
        if (!bad) _info.SetArgMap(c, map);
        if (bad) return SType.Error;
        if (vars is null) return retType;
        _info.SetTypeArgs(c, decl!.Generics.Select(g => vars[g.Name] is DimBindingSType ? SType.Any : Types.Resolve(vars[g.Name])).ToList());
        return Types.Resolve(retType);
    }

    private static string CalleeName(CallExpr c) => c.Callee is NameExpr n ? n.Name : "<function>";

    private SType CheckBuiltin(CallExpr c, NameExpr name, Scope scope, SType? hint = null)
    {
        if (name.Name == "set") return CheckSetCall(c, name, scope, hint);
        if (name.Name == "map") return CheckMapCall(c, name, scope, hint);
        if (Prelude.Tensor.Contains(name.Name)) return CheckTensorBuiltin(c, name, scope, hint);

        if (c.Args.Any(a => a.Name is not null))
            Report(Templates.UnexpectedToken(c.Args.First(a => a.Name is not null).Span, "a named argument", $"positional arguments for '{name.Name}'"));

        if (name.Name == "readl")
        {
            // readl() or readl(prompt): reads one line from stdin and returns it as a txt.
            var sig = new FuncSType(c.Args.Count == 0 ? [] : [Types.Txt], Types.Txt);
            _info.Set(name, sig);
            if (c.Args.Count > 1)
            {
                foreach (var a in c.Args) Infer(a.Value, scope);
                Report(Templates.WrongArgCount(c.Span, "readl", 1, c.Args.Count));
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
            RequireStep(c.Args[0].Value, t);
            if (t.IsError || t.IsAny) return t;
            if (t is RangeSType || t is ListSType) return t;
            Report(Templates.TypeMismatch(c.Args[0].Value.Span, N(t), "a range or List"));
            return SType.Error;
        }

        if (name.Name == "to_json")
        {
            if (c.Args.Count != 1)
            {
                foreach (var a in c.Args) Infer(a.Value, scope);
                Report(Templates.WrongArgCount(c.Span, "to_json", 1, c.Args.Count));
                return Types.Txt;
            }
            var jt = Infer(c.Args[0].Value, scope);
            _info.Set(name, new FuncSType([jt], Types.Txt));
            if (jt is FuncSType) Report(Templates.TypeMismatch(c.Args[0].Value.Span, N(jt), "data (a function has no JSON form)"));
            return Types.Txt;
        }

        if (name.Name == "len")
        {
            if (c.Args.Count != 1)
            {
                foreach (var a in c.Args) Infer(a.Value, scope);
                Report(Templates.WrongArgCount(c.Span, "len", 1, c.Args.Count));
                return Types.Usize;
            }
            var t = Infer(c.Args[0].Value, scope);
            _info.Set(name, new FuncSType([t], Types.Usize));
            if (!t.IsError && !t.IsAny && t is not PrimType { Kind: Prim.Txt } && t is not ListSType && t is not SetSType && t is not MapSType && t is not TensorSType)
                Report(Templates.TypeMismatch(c.Args[0].Value.Span, N(t), "txt, List, Set, Map, or a tensor"));
            return Types.Usize;
        }

        if (name.Name == "enumerate")
        {
            if (c.Args.Count != 1)
            {
                foreach (var a in c.Args) Infer(a.Value, scope);
                Report(Templates.WrongArgCount(c.Span, "enumerate", 1, c.Args.Count));
                return new ListSType(new TupleSType([Types.Usize, SType.Any]));
            }
            var t = Infer(c.Args[0].Value, scope);
            RequireStep(c.Args[0].Value, t);
            var elem = ElementOf(t) ?? SType.Any;
            _info.Set(name, new FuncSType([t], new ListSType(new TupleSType([Types.Usize, elem]))));
            return new ListSType(new TupleSType([Types.Usize, elem]));
        }

        if (name.Name == "zip")
        {
            if (c.Args.Count != 2)
            {
                foreach (var a in c.Args) Infer(a.Value, scope);
                Report(Templates.WrongArgCount(c.Span, "zip", 2, c.Args.Count));
                return new ListSType(new TupleSType([SType.Any, SType.Any]));
            }
            var t1 = Infer(c.Args[0].Value, scope);
            var t2 = Infer(c.Args[1].Value, scope);
            RequireStep(c.Args[0].Value, t1);
            RequireStep(c.Args[1].Value, t2);
            var e1 = ElementOf(t1) ?? SType.Any;
            var e2 = ElementOf(t2) ?? SType.Any;
            _info.Set(name, new FuncSType([t1, t2], new ListSType(new TupleSType([e1, e2]))));
            return new ListSType(new TupleSType([e1, e2]));
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
                Report(Templates.TypeMismatch(a.Value.Span, N(t), "something printable (a number, bool, txt, char or unit)"));
        }
        _info.Set(name, new FuncSType([], Types.Unit));
        return Types.Unit;
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

        // `i64(5)` makes the literal an i64 directly; `f32(3)` converts an i32.
        var from = Infer(c.Args[0].Value, scope, Types.IsInt(target) ? target : null);
        if (from.IsError || from.IsAny) return target;
        if (from is TensorSType fts && Types.IsFloat(target) && Types.IsFloat(fts.Elem)) return fts.WithElem(target); // f64(A)
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
            Prim.Bool or Prim.Unit => fp.Kind == target.Kind,
            _ => Types.IsNumeric(fp.Kind) || (fp.Kind == Prim.Char && Types.IsInt(target.Kind)),
        };
        if (!ok)
            Report(Templates.BadOperator(c.Span, $"{target.Name}(...)", N(from), null,
                target.Kind == Prim.Bool ? "There is no conversion to 'bool'; compare instead, for example 'x != 0'."
                : $"There is no conversion from '{N(from)}' to '{target.Name}'."));
        return target;
    }

    // ---- Phase 7: enums ----

    /// <summary>
    /// Registers every <c>enum</c> of a statement list before anything else in it is checked, in two passes so a enum may
    /// mention any enum in the block (including itself: <c>Node(Tree&lt;T&gt;, T, Tree&lt;T&gt;)</c>).
    /// </summary>
    private void DeclareEnums(IReadOnlyList<Stmt> stmts)
    {
        List<(EnumDecl Decl, EnumSType Enum)>? declared = null;
        foreach (var s in stmts)
        {
            if (s is not EnumDecl md) continue;
            var enumType = new EnumSType(md.Name, md.Generics.Select(g => g.Name).ToList(), new List<EnumVariantInfo>(), md.Span.Line);
            if (!_enums.AddEnum(enumType))
            {
                Report(Templates.Redeclared(md.Span, md.Name, _enums.FindEnum(md.Name)?.DeclaredLine ?? 0));
                continue;
            }
            (declared ??= []).Add((md, enumType));
        }
        if (declared is null) return;

        foreach (var (md, enumType) in declared)
        {
            var outer = _typeParams;
            _typeParams = new HashSet<string>(outer, StringComparer.Ordinal);
            foreach (var g in md.Generics) _typeParams.Add(g.Name);

            var list = (List<EnumVariantInfo>)enumType.Variants;
            foreach (var v in md.Variants)
            {
                if (list.Any(x => x.Name == v.Name))
                {
                    Report(Templates.Redeclared(v.Span, v.Name, md.Span.Line));
                    continue;
                }
                list.Add(new EnumVariantInfo(v.Name, v.Payloads.Select(ResolveType).ToList()));
            }
            _typeParams = outer;

            foreach (var dup in _enums.AddVariants(enumType))
                Report(Templates.Redeclared(md.Span, dup, _enums.FindVariant(dup)?.Enum.DeclaredLine ?? 0));
        }
    }

    private SType CheckedSet(SType elem, SourceSpan span)
    {
        if (!Types.IsHashable(elem)) { Report(Templates.NotHashable(span, N(elem))); return new SetSType(SType.Error); }
        return new SetSType(elem);
    }

    private SType CheckedMap(SType key, SType val, SourceSpan span)
    {
        if (!Types.IsHashable(key)) { Report(Templates.NotHashable(span, N(key))); return new MapSType(SType.Error, val); }
        return new MapSType(key, val);
    }

    /// <summary>A variant name used as a value: a unit variant (<c>Nada</c>) or a constructor function.</summary>
    private SType CheckVariantName(VariantRef vr, SType? hint, SourceSpan span)
    {
        var enumType = vr.Enum;
        if (vr.Variant.Payloads.Count == 0)
        {
            if (hint is EnumInstanceSType hm && ReferenceEquals(hm.Base, enumType)) return hm;
            return new EnumInstanceSType(enumType, enumType.TypeParams.Select(_ => (SType)SType.Any).ToList());
        }
        // A constructor must be called; it is not a first-class function value (yet).
        Report(Templates.UnexpectedToken(span, vr.Variant.Name,
            $"a call such as {vr.Variant.Name}({string.Join(", ", vr.Variant.Payloads.Select(_ => "..."))}); variant constructors are not first-class values"));
        return SType.Error;
    }

    /// <summary><c>Got(x)</c>: a generic call whose type arguments are inferred from the payloads and the expected type.</summary>
    private SType CheckConstructorCall(CallExpr c, NameExpr name, VariantRef vr, Scope scope, SType? hint)
    {
        var enumType = vr.Enum;
        var vars = enumType.TypeParams.ToDictionary(p => p, p => (SType)new InferVarSType(p), StringComparer.Ordinal);
        var ptypes = vr.Variant.Payloads.Select(p => Types.Substitute(p, vars)).ToList();
        SType ret = new EnumInstanceSType(enumType, enumType.TypeParams.Select(p => vars[p]).ToList());
        _info.Set(name, new FuncSType(ptypes, ret));

        if (c.Args.Any(a => a.Name is not null))
            Report(Templates.UnexpectedToken(c.Args.First(a => a.Name is not null).Span, "a named argument", $"positional payloads for '{name.Name}'"));
        if (c.Args.Count != ptypes.Count)
        {
            foreach (var a in c.Args) Infer(a.Value, scope);
            Report(Templates.WrongVariantArity(c.Span, name.Name, ptypes.Count, c.Args.Count));
            return Types.Resolve(ret);
        }
        var hinted = HintInstance(enumType.TypeParams, vr.Variant.Payloads, ret0: new EnumInstanceSType(enumType, enumType.TypeParams.Select(p => (SType)new TypeParamSType(p)).ToList()), hint);
        InferArgsAgainst(c.Args.Select(a => a.Value).ToList(), ptypes, hinted, scope);
        return Types.Resolve(ret);
    }

    /// <summary>
    /// Infers each argument against its parameter type and unifies the two, binding inference variables. Lambdas go last so
    /// the other arguments can fix the type parameters their own parameters will take.
    /// </summary>
    private void InferArgsAgainst(IReadOnlyList<Expr> args, IReadOnlyList<SType> ptypes, IReadOnlyList<SType>? hintParams, Scope scope)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if ((args[i] is LambdaExpr) != (pass == 1)) continue;
                var at = Infer(args[i], scope, ArgHint(ptypes[i], hintParams?[i]));
                if (!Types.Unify(ptypes[i], at)) Mismatch(args[i].Span, at, Types.Resolve(ptypes[i]));
            }
        }
    }

    /// <summary>
    /// What the surrounding context says the parameter types should be, worked out on a throwaway copy of the call's type
    /// variables. It only guides how literals are typed (<c>x::Maybe&lt;i64&gt; = Got(5)</c> makes the 5 an i64); the real
    /// variables are bound by the arguments alone, so a wrong guess shows up as one mismatch on the result, not on each argument.
    /// </summary>
    private static IReadOnlyList<SType>? HintInstance(IReadOnlyList<string> generics, IReadOnlyList<SType> rawParams, SType ret0, SType? hint)
    {
        if (hint is null || hint.IsAny || hint.IsError) return null;
        var vars = generics.ToDictionary(g => g, g => (SType)new InferVarSType(g), StringComparer.Ordinal);
        Types.Unify(Types.Substitute(ret0, vars), hint);
        return rawParams.Select(p => Types.Substitute(p, vars)).ToList();
    }

    private static SType ArgHint(SType realParam, SType? hintParam)
    {
        var real = Types.Resolve(realParam);
        return hintParam is null || !ContainsAny(real) ? real : Types.Resolve(hintParam);
    }

    private static bool ContainsAny(SType t) => t switch
    {
        _ when t.IsAny => true,
        ListSType l => ContainsAny(l.Elem),
        SetSType s => ContainsAny(s.Elem),
        MapSType m => ContainsAny(m.Key) || ContainsAny(m.Val),
        TupleSType tu => tu.Items.Any(ContainsAny),
        EnumInstanceSType mi => mi.Args.Any(ContainsAny),
        ClassSType c => c.Args.Any(ContainsAny),
        TraitSType tr => tr.Args.Any(ContainsAny),
        IterSType it => ContainsAny(it.Elem),
        _ => false,
    };

    // ---- Phase 7: tuples and lists ----

    private void CheckTupleBind(TupleBindStmt tb, Scope scope)
    {
        var vt = Infer(tb.Value, scope);
        var items = new List<SType>();
        if (vt is TupleSType ts)
        {
            if (ts.Items.Count != tb.Names.Count)
                Report(Templates.TypeMismatch(tb.Value.Span, N(vt), $"a tuple with {tb.Names.Count} element(s)"));
            else items = ts.Items.ToList();
        }
        else if (!vt.IsError && !vt.IsAny)
            Report(Templates.TypeMismatch(tb.Value.Span, N(vt), "a tuple"));

        for (int i = 0; i < tb.Names.Count; i++)
        {
            var elemType = i < items.Count ? items[i] : SType.Any;
            if (tb.Names[i] == "_") continue;
            Declare(scope, new Symbol(tb.Names[i], SymbolKind.Variable, elemType, tb.IsMutable, tb.Span), tb.Span);
        }
    }

    private SType CheckListLiteral(ListLiteral ll, Scope scope, SType? hint)
    {
        if (hint is TensorSType th) return CheckTensorList(ll, th, scope);
        var elemHint = (hint as ListSType)?.Elem;
        if (ll.Items.Count == 0)
        {
            if (elemHint is not null) return new ListSType(elemHint);
            if (hint is null || hint is ListSType)
            {
                Report(Templates.CannotInfer(ll.Span, "the element type of '[]'", "Annotate it: xs::List<i32> = []"));
                return new ListSType(SType.Error);
            }
            return new ListSType(SType.Any);
        }
        var elem = UnifyElements(ll.Items, scope, elemHint, out bool failed);
        return new ListSType(failed ? SType.Error : elem ?? SType.Any);
    }

    /// <summary>The common type of a list of expressions (each must match the first, or the hint).</summary>
    private SType? UnifyElements(IReadOnlyList<Expr> items, Scope scope, SType? elemHint, out bool failed)
    {
        SType? elem = elemHint;
        failed = false;
        foreach (var item in items)
        {
            var t = Infer(item, scope, elem ?? elemHint);
            if (t.IsError) { failed = true; continue; }
            if (elem is null) elem = t;
            else if (!t.IsAny && !elem.IsAny && !Types.Compatible(t, elem)) { Mismatch(item.Span, t, elem); failed = true; }
            else if (ContainsAny(elem) && !ContainsAny(t) && Types.Compatible(t, elem)) elem = t;
        }
        return elem;
    }

    private SType CheckTuple(TupleExpr te, Scope scope, SType? hint)
    {
        var th = hint as TupleSType;
        var items = new List<SType>();
        for (int i = 0; i < te.Items.Count; i++)
        {
            var h = th is not null && i < th.Items.Count ? th.Items[i] : null;
            items.Add(Infer(te.Items[i], scope, h));
        }
        return new TupleSType(items);
    }

    private SType CheckIndex(IndexExpr ix, Scope scope, SType? hint)
    {
        // f32[1, 2, 3] and f32[[1, 2], [3, 4]]: a number-type name followed by a bracketed list is a tensor literal.
        if (ix.Target is NameExpr { Name: var elemName } && scope.Lookup(elemName) is null
            && Types.ByName.TryGetValue(elemName, out var litElem) && Types.IsNumeric(litElem))
            return CheckPrefixedTensorLiteral(ix, litElem, scope);

        var target = Infer(ix.Target, scope);
        if (target is TensorSType ttarget) return CheckTensorIndex(ix, ttarget, scope);
        if (ix.Indices.Count != 1)
        {
            foreach (var i in ix.Indices) if (i is not FullSlice) Infer(i, scope);
            return NotYet(ix.Span, "multi-index", 12);
        }
        var idx = ix.Indices[0];
        if (idx is FullSlice) return NotYet(idx.Span, "slicing", 12);

        switch (target)
        {
            case ListSType l:
            {
                var it = Infer(idx, scope, IndexHint(idx));
                if (it is RangeSType { Elem: var re } && (Types.IsInt(re) || re.IsAny)) return new ListSType(l.Elem); // a slice
                if (!it.IsError && !it.IsAny && !Types.IsInt(it))
                    Report(Templates.TypeMismatch(idx.Span, N(it), "an integer index or a range"));
                return l.Elem;
            }
            case MapSType m:
            {
                var kt = Infer(idx, scope, m.Key);
                if (!Types.Compatible(kt, m.Key)) Mismatch(idx.Span, kt, m.Key);
                return m.Val;
            }
            case PrimType { Kind: Prim.Txt }:
            {
                var it = Infer(idx, scope, Types.Usize);
                if (!it.IsError && !it.IsAny && !Types.IsInt(it)) Report(Templates.TypeMismatch(idx.Span, N(it), "an integer index"));
                return Types.Char;
            }
            case ClassSType cs:
            {
                var r = TryIndexOverload(ix, cs, scope);
                if (r is not null) return r;
                Infer(idx, scope);
                Report(Templates.BadOperator(ix.Span, "[]", N(target), null, $"Define 'fn op [](self, i::usize)::T' in '{cs.Info.Name}' to index its objects."));
                return SType.Error;
            }
            case NativeSType { Name: "Json" }:
            {
                var it = Infer(idx, scope);
                if (!it.IsError && !it.IsAny && !Types.IsInt(it) && !it.Equals(Types.Txt))
                    Report(Templates.TypeMismatch(idx.Span, N(it), "a txt key or an integer index"));
                return Types.JsonT;
            }
            default:
                Infer(idx, scope);
                if (target.IsAny) return SType.Any;
                if (target.IsError) return SType.Error;
                return NotYet(ix.Span, $"indexing '{N(target)}'", 12);
        }
    }

    /// <summary>A bare integer index adopts <c>usize</c>; a range index (<c>xs[1..3]</c>) adopts a <c>usize</c> range.</summary>
    private static SType IndexHint(Expr idx) =>
        idx is BinaryExpr { Op: TokenKind.DotDot or TokenKind.DotDotEq } or StepExpr ? new RangeSType(Types.Usize) : Types.Usize;

    // ---- Phase 7: pick and patterns ----

    private SType CheckPick(PickExpr pk, Scope scope, SType? hint)
    {
        var subject = Infer(pk.Subject, scope);
        var tys = new[] { subject };
        var rows = new List<Pat[]>();
        int errorsBefore = _diag.ErrorCount;
        SType? result = null;

        foreach (var arm in pk.Arms)
        {
            var armScope = new Scope(scope);
            var pat = CheckPattern(arm.Pat, subject, armScope);
            if (!subject.IsError && _diag.ErrorCount == errorsBefore && !PatternMatrix.Useful(rows, [pat], tys))
                Report(Templates.UnreachableArm(arm.Pat.Span));
            rows.Add([pat]);

            SType armType;
            if (arm.ExprBody is not null) armType = Infer(arm.ExprBody, armScope, hint ?? result);
            else
            {
                CheckStatements(arm.BlockBody!.Statements, armScope, null);
                armType = Types.Unit;
            }

            if (armType.IsError) continue;
            if (result is null) result = armType;
            else if (Types.Compatible(armType, result))
            {
                if (ContainsAny(result) && !ContainsAny(armType)) result = armType;
            }
            else Mismatch(arm.ExprBody?.Span ?? arm.Span, armType, result);
        }

        // Exhaustiveness: nothing may be left over. Skipped when the patterns themselves were already reported as wrong.
        if (!subject.IsError && !subject.IsAny && _diag.ErrorCount == errorsBefore)
        {
            var missing = PatternMatrix.Witnesses(rows, tys, 4);
            if (missing.Count > 0)
                Report(Templates.NonExhaustivePick(pk.Span, string.Join(", ", missing.Select(w => PatternMatrix.Show(w[0])))));
        }

        return result ?? Types.Unit;
    }

    private Pat CheckPattern(Pattern p, SType t, Scope scope)
    {
        switch (p)
        {
            case WildcardPat:
                return PWild.I;

            case BindPat bp:
                Declare(scope, new Symbol(bp.Name, SymbolKind.Variable, t, false, bp.Span), bp.Span);
                return PWild.I;

            case LiteralPat lp:
                return CheckLiteralPattern(lp, t, scope);

            case RangePat rp:
                return CheckRangePattern(rp, t, scope);

            case TuplePat tp:
            {
                if (t is TupleSType ts && ts.Items.Count == tp.Items.Count)
                    return new PCtor("(,)", tp.Items.Select((item, i) => CheckPattern(item, ts.Items[i], scope)).ToList());
                if (!t.IsAny && !t.IsError)
                    Report(Templates.TypeMismatch(tp.Span, $"a tuple pattern with {tp.Items.Count} part(s)", N(t)));
                foreach (var item in tp.Items) CheckPattern(item, SType.Any, scope);
                return PWild.I;
            }

            case VariantPat vp:
                return CheckVariantPattern(vp, t, scope);

            default:
                return PWild.I;
        }
    }

    private Pat CheckVariantPattern(VariantPat vp, SType t, Scope scope)
    {
        EnumInstanceSType? inst = t as EnumInstanceSType;
        EnumVariantInfo? variant = null;

        if (inst is not null)
        {
            variant = inst.Base.Variants.FirstOrDefault(v => v.Name == vp.VariantName);
            if (variant is null)
                Report(Templates.VariantNotFound(vp.Span, inst.Base.EnumName, vp.VariantName));
        }
        else if (t.IsAny || t.IsError)
        {
            if (_enums.FindVariant(vp.VariantName) is { } vr)
            {
                variant = vr.Variant;
                inst = new EnumInstanceSType(vr.Enum, vr.Enum.TypeParams.Select(_ => (SType)SType.Any).ToList());
            }
            else if (!t.IsError) Report(Templates.VariantNotFound(vp.Span, "any enum", vp.VariantName));
        }
        else
        {
            var owner = _enums.FindVariant(vp.VariantName);
            if (owner is null) Report(Templates.VariantNotFound(vp.Span, N(t), vp.VariantName));
            else Mismatch(vp.Span, new EnumInstanceSType(owner.Enum, owner.Enum.TypeParams.Select(_ => (SType)SType.Any).ToList()), t);
        }

        if (variant is null || inst is null)
        {
            foreach (var b in vp.Bindings) CheckPattern(b, SType.Any, scope);
            return PWild.I;
        }
        if (vp.Bindings.Count != variant.Payloads.Count)
        {
            Report(Templates.WrongVariantArity(vp.Span, vp.VariantName, variant.Payloads.Count, vp.Bindings.Count));
            foreach (var b in vp.Bindings) CheckPattern(b, SType.Any, scope);
            return PWild.I;
        }
        var subs = new List<Pat>();
        for (int i = 0; i < vp.Bindings.Count; i++)
            subs.Add(CheckPattern(vp.Bindings[i], inst.Substitute(variant.Payloads[i]), scope));
        return new PCtor(vp.VariantName, subs);
    }

    /// <summary>The integer or char value of a literal pattern bound, if the expression is one.</summary>
    private static bool TryConstPoint(Expr e, out Int128 value, out bool isChar)
    {
        isChar = false;
        switch (e)
        {
            case CharLiteral c: value = c.Value; isChar = true; return true;
            default: return TryConstInt(e, out value);
        }
    }

    private Pat CheckLiteralPattern(LiteralPat lp, SType t, Scope scope)
    {
        var lt = Infer(lp.Value, scope, t.IsAny || t.IsError ? null : t);
        if (lt.IsError) return PWild.I;
        if (!Types.Compatible(lt, t)) { Mismatch(lp.Span, lt, t); return PWild.I; }

        switch (lp.Value)
        {
            case BoolLiteral b: return new PCtor(b.Value ? "true" : "false", []);
            case UnitLiteral: return new PCtor("()", []);
            case FloatLiteral f: return new PLit(f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case StringTemplate st:
                if (st.Parts.Any(x => x is TemplateExpr))
                {
                    Report(Templates.UnexpectedToken(st.Span, "an interpolated string", "a plain string literal in a pattern"));
                    return PWild.I;
                }
                return new PLit(AstPrinter.Quote(string.Concat(st.Parts.Cast<TemplateText>().Select(x => x.Value))));
            default:
                if (TryConstPoint(lp.Value, out var v, out bool isChar)) return new PRange(v, v, isChar);
                Report(Templates.UnexpectedToken(lp.Span, "an expression", "a literal in a pattern"));
                return PWild.I;
        }
    }

    private Pat CheckRangePattern(RangePat rp, SType t, Scope scope)
    {
        var lo = Infer(rp.Lo, scope, t.IsAny || t.IsError ? null : t);
        var hi = Infer(rp.Hi, scope, t.IsAny || t.IsError ? null : t);
        if (lo.IsError || hi.IsError) return PWild.I;
        if (!Types.Compatible(lo, t)) { Mismatch(rp.Lo.Span, lo, t); return PWild.I; }
        if (!Types.Compatible(hi, t)) { Mismatch(rp.Hi.Span, hi, t); return PWild.I; }
        if (!TryConstPoint(rp.Lo, out var a, out bool isChar) || !TryConstPoint(rp.Hi, out var b, out _))
        {
            Report(Templates.UnexpectedToken(rp.Span, "an expression", "integer or char literals as range bounds"));
            return PWild.I;
        }
        if (!isChar && !Types.IsInt(lo) && !lo.IsAny)
        {
            Report(Templates.TypeMismatch(rp.Span, N(lo), "an integer or char range"));
            return PWild.I;
        }
        if (!rp.Inclusive) b -= 1;
        if (a > b)
        {
            Report(Templates.BadLiteral(rp.Span, "This range pattern is empty, so it can never match.", rp.Inclusive ? "Put the smaller bound first." : "Use 'a..b' with a < b, or 'a..=b'."));
            return PWild.I;
        }
        return new PRange(a, b, isChar);
    }

    // ---- Phase 7: collection methods ----

    private static readonly HashSet<string> MutatingMethods = new(StringComparer.Ordinal) { "push", "pop", "insert", "remove", "add" };

    private static NameExpr? RootName(Expr e) => e switch
    {
        NameExpr n => n,
        IndexExpr ix => RootName(ix.Target),
        MemberExpr m => RootName(m.Target),
        _ => null,
    };

    /// <summary>Mutating calls and index assignment need a <c>var</c> variable at the root of the receiver.</summary>
    private void RequireMutable(Expr target, Scope scope)
    {
        var root = RootName(target);
        if (root is null) { Report(Templates.ImmutableMutation(target.Span, "a temporary value")); return; }
        var sym = scope.Lookup(root.Name);
        if (sym is null) return; // already reported as undefined
        if (sym.Kind != SymbolKind.Variable || !sym.IsMutable)
            Report(root.Name == "self" ? Templates.ImmutableSelf(target.Span, _curMethod?.Name ?? "method") : Templates.ImmutableMutation(target.Span, root.Name));
    }

    private SType CheckMethodCall(CallExpr c, MemberExpr m, Scope scope)
    {
        if (m.Target is NameExpr { Name: "super" }) return CheckSuperCall(c, m, scope);
        if (m.Target is NameExpr tn && scope.Lookup(tn.Name) is null && NativeMembers.StaticsOf(tn.Name) is { } statics)
            return CheckStaticCall(c, m, tn.Name, statics, scope);

        var recv = Infer(m.Target, scope);
        void InferAll() { foreach (var a in c.Args) Infer(a.Value, scope); }

        if (recv.IsError) { InferAll(); return SType.Error; }
        if (recv.IsAny) { InferAll(); return SType.Any; }
        if (recv is TensorSType tensorRecv && CheckTensorMethod(c, m, tensorRecv, scope) is { } tensorResult) return tensorResult;

        // name -> (parameter types, return type, mutates)
        (SType[] Params, SType Ret, bool Mutates)? sig = null;
        switch (recv)
        {
            case PrimType { Kind: Prim.Txt }:
                sig = m.Name switch
                {
                    "upper" or "lower" or "trim" => ([], Types.Txt, false),
                    "len" => ([], Types.Usize, false),
                    "split" => ([Types.Txt], new ListSType(Types.Txt), false),
                    "lines" => ([], new ListSType(Types.Txt), false),
                    _ => null,
                };
                break;
            case ListSType l:
                sig = m.Name switch
                {
                    "len" => ([], Types.Usize, false),
                    "push" => ([l.Elem], Types.Unit, true),
                    "pop" => ([], EnumRegistry.MaybeOf(l.Elem), true),
                    "insert" => ([Types.Usize, l.Elem], Types.Unit, true),
                    "remove" => ([Types.Usize], l.Elem, true),
                    _ => null,
                };
                break;
            case SetSType s:
                sig = m.Name switch
                {
                    "len" => ([], Types.Usize, false),
                    "add" => ([s.Elem], Types.Unit, true),
                    "remove" => ([s.Elem], Types.Bool, true),
                    _ => null,
                };
                break;
            case MapSType mp:
                sig = m.Name switch
                {
                    "len" => ([], Types.Usize, false),
                    "remove" => ([mp.Key], EnumRegistry.MaybeOf(mp.Val), true),
                    "get" => ([mp.Key], EnumRegistry.MaybeOf(mp.Val), false),
                    _ => null,
                };
                break;
            default:
            {
                if (NativeMembers.MethodsOf(recv) is { } nm) return CheckNativeMethod(c, m, recv, nm, scope);
                var user = CheckUserMethod(c, m, recv, scope);
                if (user is not null) return user;
                InferAll();
                Report(Templates.UndefinedName(m.NameSpan, $"{N(recv)}.{m.Name}"));
                return SType.Error;
            }
        }

        if (sig is null)
        {
            // Not a built-in collection method: an `extend List<T>` block may provide it.
            var ext = CheckUserMethod(c, m, recv, scope);
            if (ext is not null) return ext;
            InferAll();
            Report(Templates.UndefinedName(m.NameSpan, $"{N(recv)}.{m.Name}"));
            return SType.Error;
        }

        var (ps, ret, mutates) = sig.Value;
        if (mutates) RequireMutable(m.Target, scope);
        _info.Set(m, new FuncSType(ps, ret));

        if (c.Args.Count != ps.Length || c.Args.Any(a => a.Name is not null))
        {
            InferAll();
            Report(Templates.WrongArgCount(c.Span, $"{N(recv)}.{m.Name}", ps.Length, c.Args.Count));
            return ret;
        }
        for (int i = 0; i < ps.Length; i++)
        {
            var at = Infer(c.Args[i].Value, scope, ps[i]);
            if (!Types.Compatible(at, ps[i])) Mismatch(c.Args[i].Value.Span, at, ps[i]);
        }
        return ret;
    }

    /// <summary><c>xs[i] = v</c>, <c>m[k] = v</c> and the compound forms.</summary>
    private void CheckIndexAssign(AssignStmt a, IndexExpr ix, Scope scope)
    {
        var target = Infer(ix.Target, scope);
        if (target is TensorSType tt) { CheckTensorIndexAssign(a, ix, tt, scope); return; }
        SType? elem = null;
        if (ix.Indices.Count != 1 || ix.Indices[0] is FullSlice)
        {
            foreach (var i in ix.Indices) if (i is not FullSlice) Infer(i, scope);
            NotYet(ix.Span, "assigning through a slice or multi-index", 12);
            Infer(a.Value, scope);
            return;
        }
        var idx = ix.Indices[0];
        switch (target)
        {
            case ListSType l:
            {
                var it = Infer(idx, scope, Types.Usize);
                if (!it.IsError && !it.IsAny && !Types.IsInt(it)) Report(Templates.TypeMismatch(idx.Span, N(it), "an integer index"));
                elem = l.Elem;
                RequireMutable(ix.Target, scope);
                break;
            }
            case MapSType m:
            {
                var kt = Infer(idx, scope, m.Key);
                if (!Types.Compatible(kt, m.Key)) Mismatch(idx.Span, kt, m.Key);
                elem = m.Val;
                RequireMutable(ix.Target, scope);
                break;
            }
            default:
                Infer(idx, scope);
                if (!target.IsError && !target.IsAny) Report(Templates.TypeMismatch(ix.Target.Span, N(target), "a List or Map to assign into"));
                elem = SType.Any;
                break;
        }
        _info.Set(ix, elem);

        if (a.Op == TokenKind.Assign)
        {
            var vt = Infer(a.Value, scope, elem);
            if (!Types.Compatible(vt, elem)) Mismatch(a.Value.Span, vt, elem);
            return;
        }
        var baseOp = CompoundBase(a.Op)!.Value;
        var rt = Infer(a.Value, scope, elem);
        var result = ArithmeticType(baseOp, elem, rt, a.Span, OpText(baseOp) + "=");
        if (!Types.Compatible(result, elem)) Mismatch(a.Value.Span, result, elem);
    }

    // ---- Phase 7: set(...) and map(...) ----

    private SType CheckSetCall(CallExpr c, NameExpr name, Scope scope, SType? hint)
    {
        if (c.Args.Any(a => a.Name is not null))
            Report(Templates.UnexpectedToken(c.Args.First(a => a.Name is not null).Span, "a named argument", "positional elements for 'set'"));
        var elemHint = (hint as SetSType)?.Elem;
        if (c.Args.Count == 0)
        {
            if (elemHint is not null) { _info.Set(name, new FuncSType([], new SetSType(elemHint))); return new SetSType(elemHint); }
            if (hint is null or SetSType)
            {
                Report(Templates.CannotInfer(c.Span, "the element type of 'set()'", "Annotate it: s::Set<i32> = set()"));
                return new SetSType(SType.Error);
            }
            return new SetSType(SType.Any);
        }
        var elem = UnifyElements(c.Args.Select(a => a.Value).ToList(), scope, elemHint, out bool failed);
        if (failed) return new SetSType(SType.Error);
        var result = CheckedSet(elem ?? SType.Any, c.Args[0].Value.Span);
        _info.Set(name, new FuncSType(c.Args.Select(_ => elem ?? SType.Any).ToList(), result));
        return result;
    }

    private SType CheckMapCall(CallExpr c, NameExpr name, Scope scope, SType? hint)
    {
        var mh = hint as MapSType;
        if (c.Args.Count == 0)
        {
            if (mh is not null) { _info.Set(name, new FuncSType([], mh)); return mh; }
            if (hint is null)
            {
                Report(Templates.CannotInfer(c.Span, "the key and value types of 'map()'", "Annotate it: m::Map<txt, i32> = map()"));
                return new MapSType(SType.Error, SType.Error);
            }
            return new MapSType(SType.Any, SType.Any);
        }

        SType? key = mh?.Key, val = mh?.Val;
        bool failed = false;
        foreach (var a in c.Args)
        {
            if (a.Name is not null || a.Value is not PairExpr pair)
            {
                Infer(a.Value, scope);
                Report(Templates.UnexpectedToken(a.Span, "an argument", "a 'key => value' pair, as in map(\"a\" => 1, \"b\" => 2)"));
                failed = true;
                continue;
            }
            var kt = Infer(pair.Key, scope, key);
            var vt = Infer(pair.Value, scope, val);
            _info.Set(pair, new TupleSType([kt, vt]));
            if (kt.IsError || vt.IsError) { failed = true; continue; }
            if (key is null) key = kt;
            else if (!kt.IsAny && !key.IsAny && !Types.Compatible(kt, key)) { Mismatch(pair.Key.Span, kt, key); failed = true; }
            else if (ContainsAny(key) && !ContainsAny(kt)) key = kt;
            if (val is null) val = vt;
            else if (!vt.IsAny && !val.IsAny && !Types.Compatible(vt, val)) { Mismatch(pair.Value.Span, vt, val); failed = true; }
            else if (ContainsAny(val) && !ContainsAny(vt)) val = vt;
        }
        if (failed) return new MapSType(SType.Error, SType.Error);
        var result = CheckedMap(key ?? SType.Any, val ?? SType.Any, c.Args[0].Span);
        _info.Set(name, new FuncSType(c.Args.Select(_ => (SType)SType.Any).ToList(), result));
        return result;
    }

    // ---- Phase 7: mutation while iterating ----

    /// <summary>
    /// The variables a <c>loop</c> subject iterates directly: a plain name, the parts of a <c>then</c> chain, and the
    /// arguments of <c>rev</c>/<c>enumerate</c>/<c>unit</c>.
    /// </summary>
    private static IEnumerable<string> SubjectRoots(Expr e, Scope scope)
    {
        switch (e)
        {
            case NameExpr n: yield return n.Name; break;
            case ChainExpr ch:
                foreach (var part in ch.Parts) foreach (var r in SubjectRoots(part, scope)) yield return r;
                break;
            case CallExpr { Callee: NameExpr cn } call when cn.Name is "rev" or "enumerate" or "zip" && scope.Lookup(cn.Name) is null:
                foreach (var a in call.Args) foreach (var r in SubjectRoots(a.Value, scope)) yield return r;
                break;
        }
    }

    private void CheckNoMutation(IReadOnlyList<Stmt> stmts, string name)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case AssignStmt a:
                    if (RootName(a.Target) is { } t && t.Name == name) Report(Templates.MutateWhileIterating(a.Span, name));
                    ScanExpr(a.Target, name);
                    ScanExpr(a.Value, name);
                    break;
                case BindStmt b:
                    ScanExpr(b.Value, name);
                    if (b.Name == name) return; // shadowed from here on
                    break;
                case TupleBindStmt tb:
                    ScanExpr(tb.Value, name);
                    if (tb.Names.Contains(name)) return;
                    break;
                case ExprStmt e: ScanExpr(e.Expression, name); break;
                case ReturnStmt { Value: { } yv }: ScanExpr(yv, name); break;
                case RipStmt { Message: { } rm }: ScanExpr(rm, name); break;
                case IfStmt h:
                    foreach (var br in h.Branches) { ScanExpr(br.Condition, name); CheckNoMutation(br.Body.Statements, name); }
                    if (h.Else is not null) CheckNoMutation(h.Else.Statements, name);
                    break;
                case LoopStmt sp:
                    if (sp.Subject is not null) ScanExpr(sp.Subject, name);
                    if (sp.Variable != name) CheckNoMutation(sp.Body.Statements, name);
                    break;
                case TryStmt ty:
                    CheckNoMutation(ty.Body.Statements, name);
                    if (ty.CatchName != name) CheckNoMutation(ty.Handler.Statements, name);
                    break;
            }
        }
    }

    /// <summary>Finds pokes (<c>~xs</c>) and mutating method calls on <paramref name="name"/> inside an expression.</summary>
    private void ScanExpr(Expr e, string name)
    {
        switch (e)
        {
            case UnaryExpr { Op: TokenKind.Tilde } u when RootName(u.Operand)?.Name == name:
                Report(Templates.MutateWhileIterating(u.Span, name));
                break;
            case CallExpr { Callee: MemberExpr m } c when MutatingMethods.Contains(m.Name) && RootName(m.Target)?.Name == name:
                Report(Templates.MutateWhileIterating(c.Span, name));
                foreach (var a in c.Args) ScanExpr(a.Value, name);
                return;
        }
        foreach (var child in ChildExprs(e)) ScanExpr(child, name);
        if (e is PickExpr pk)
            foreach (var arm in pk.Arms)
                if (arm.BlockBody is not null) CheckNoMutation(arm.BlockBody.Statements, name);
    }

    private static IEnumerable<Expr> ChildExprs(Expr e)
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
            case PickExpr pk: return pk.Arms.Where(a => a.ExprBody is not null).Select(a => a.ExprBody!).Prepend(pk.Subject);
            default: return [];
        }
    }
}
