using Splat.Core;
using Splat.Syntax;

namespace Splat.Semantics;

// Phases 8-9: classes, inheritance, traits, extend, member lookup, constructors, operator overloads, super, drop.
public sealed partial class TypeChecker
{
    /// <summary>Operators a class (or extend block) may define with <c>fn op &lt;symbol&gt;</c>.</summary>
    internal static readonly IReadOnlySet<string> OverloadableOps = new HashSet<string>(StringComparer.Ordinal)
        { "+", "-", "*", "/", "@", "==", "!=", "<", ">", "[]" };

    private sealed record GenericEnv(HashSet<string> Params, Dictionary<string, TraitSType> Bounds);

    private Dictionary<string, TraitSType> _typeBounds = new(StringComparer.Ordinal);
    private readonly Dictionary<FnDecl, GenericEnv> _fnGeneric = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FnDecl, List<(string Name, TraitSType Bound)>> _declBounds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ClassDecl, ClassInfo> _classOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TraitDecl, TraitInfo> _traitOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ExtendDecl, ExtInfo> _extOf = new(ReferenceEqualityComparer.Instance);

    // The class and method whose body is being checked (null outside methods).
    private ClassInfo? _curClass;
    private Symbol? _curMethod;

    // ---- generics scope ----

    /// <summary>Adds generic parameters (and their trait bounds) to the scope; returns the previous scope for <see cref="PopGenerics"/>.</summary>
    private (HashSet<string>, Dictionary<string, TraitSType>) PushGenerics(IReadOnlyList<GenericParam> gps)
    {
        var saved = (_typeParams, _typeBounds);
        _typeParams = new HashSet<string>(_typeParams, StringComparer.Ordinal);
        _typeBounds = new Dictionary<string, TraitSType>(_typeBounds, StringComparer.Ordinal);
        foreach (var g in gps) { _typeParams.Add(g.Name); _typeBounds.Remove(g.Name); }
        foreach (var g in gps)
        {
            if (g.Bound is null) continue;
            var bt = ResolveType(g.Bound);
            if (bt is TraitSType ts) _typeBounds[g.Name] = ts;
            else if (!bt.IsError) Report(Templates.TypeMismatch(g.Bound.Span, N(bt), "a trait (the bound after ':')"));
        }
        return saved;
    }

    private void PopGenerics((HashSet<string>, Dictionary<string, TraitSType>) saved) => (_typeParams, _typeBounds) = saved;

    private GenericEnv CurrentGenerics() => new(_typeParams, _typeBounds);

    // ---- declaring classes, traits and extend blocks (hoisted, like enums) ----

    private sealed record TypeDecls(List<ClassInfo> Classes, List<ExtInfo> Exts);

    private TypeDecls? DeclareTypes(IReadOnlyList<Stmt> stmts, Scope scope)
    {
        var classes = new List<ClassInfo>();
        var traits = new List<TraitInfo>();
        var exts = new List<ExtInfo>();

        // Pass 1: names, so declarations may refer to each other in any order.
        foreach (var s in stmts)
        {
            if (s is ClassDecl cd)
            {
                var info = new ClassInfo(cd.Name, cd.Generics.Select(g => g.Name).ToList(), cd, cd.Span.Line);
                if (!_enums.AddClass(info))
                { Report(Templates.Redeclared(cd.NameSpan, cd.Name, _enums.FindClass(cd.Name)?.DeclaredLine ?? 0)); continue; }
                _classOf[cd] = info;
                classes.Add(info);
            }
            else if (s is TraitDecl td)
            {
                var info = new TraitInfo(td.Name, td.Generics.Select(g => g.Name).ToList(), td, td.Span.Line);
                if (!_enums.AddTrait(info))
                { Report(Templates.Redeclared(td.NameSpan, td.Name, _enums.FindTrait(td.Name)?.DeclaredLine ?? 0)); continue; }
                _traitOf[td] = info;
                traits.Add(info);
            }
            else if (s is ExtendDecl ed) { var e = new ExtInfo(ed); _extOf[ed] = e; exts.Add(e); }
        }
        if (classes.Count == 0 && traits.Count == 0 && exts.Count == 0) return null;

        // Pass 2a: class headers (parent, traits, fields), so method signatures can mention any class.
        foreach (var ci in classes) DeclareClassHeader(ci);
        foreach (var ci in classes) BreakCycles(ci);
        // Pass 2b: trait signatures and extend targets.
        foreach (var ti in traits) DeclareTraitMethods(ti, scope);
        foreach (var ei in exts) DeclareExtendHeader(ei);
        // Pass 2c: class methods (they need trait and extend headers to exist).
        foreach (var ci in classes) DeclareClassMethods(ci, scope);
        foreach (var ei in exts) DeclareExtendMethods(ei, scope);
        return new TypeDecls(classes, exts);
    }

    /// <summary>Pass 3, run once every function signature of the block is also declared: whole-program rules.</summary>
    private void ValidateTypes(TypeDecls? td)
    {
        if (td is null) return;
        foreach (var ci in td.Classes) ValidateClass(ci);
        foreach (var ei in td.Exts) ValidateExtend(ei);
    }

    private void DeclareClassHeader(ClassInfo ci)
    {
        var cd = ci.Decl!;
        var saved = PushGenerics(cd.Generics);
        for (int i = 0; i < cd.Generics.Count; i++)
            ci.Bounds.Add(_typeBounds.GetValueOrDefault(cd.Generics[i].Name));
        _classGeneric[ci] = CurrentGenerics();

        if (cd.Parent is not null)
        {
            var pt = ResolveType(cd.Parent);
            if (pt is ClassSType { Info.IsBuiltin: false } pc) ci.Parent = pc;
            else if (pt is ClassSType) Report(Templates.TypeMismatch(cd.Parent.Span, N(pt), "a class you can inherit from (built-in classes are sealed)"));
            else if (!pt.IsError) Report(Templates.TypeMismatch(cd.Parent.Span, N(pt), "a class"));
        }
        foreach (var tr in cd.Traits)
        {
            var tt = ResolveType(tr);
            if (tt is TraitSType ts) ci.Traits.Add(ts);
            else if (!tt.IsError) Report(Templates.TypeMismatch(tr.Span, N(tt), "a trait (written after 'with')"));
        }
        foreach (var f in cd.Fields)
        {
            var ft = ResolveType(f.Type);
            if (ci.OwnField(f.Name) is { } dup) { Report(Templates.Redeclared(f.NameSpan, f.Name, dup.Span.Line)); continue; }
            ci.Fields.Add(new FieldInfo(f.Name, ft, f.IsSecret, f.Default, ci, f.Span));
        }
        PopGenerics(saved);
    }

    private readonly Dictionary<ClassInfo, GenericEnv> _classGeneric = new(ReferenceEqualityComparer.Instance);

    private void BreakCycles(ClassInfo ci)
    {
        var seen = new HashSet<ClassInfo>(ReferenceEqualityComparer.Instance) { ci };
        for (var p = ci.Parent; p is not null; p = p.Info.Parent)
        {
            if (!seen.Add(p.Info))
            {
                Report(Templates.InheritanceCycle(ci.Decl!.NameSpan, ci.Name));
                ci.Parent = null;
                return;
            }
        }
        // A subclass may not reuse an inherited field's name.
        for (var p = ci.Parent; p is not null; p = p.Info.Parent)
            foreach (var f in ci.Fields)
                if (p.Info.OwnField(f.Name) is { } pf)
                    Report(Templates.Redeclared(f.Span, f.Name, pf.Span.Line));
    }

    private MethodInfo? DeclareMethod(object owner, FnDecl d, bool secret, Scope scope, SType selfType, ClassInfo? cls, GenericEnv outer)
    {
        // `self` must come first, and every method needs one (there are no static methods yet).
        int selfIdx = d.Params.ToList().FindIndex(p => p.Kind != ParamKind.Normal);
        if (selfIdx != 0)
        {
            Report(Templates.UnexpectedToken(d.Span, $"fn {d.Name}(...)",
                selfIdx < 0 ? "a method with 'self' as its first parameter (static methods do not exist yet)" : "'self' as the first parameter"));
            return null;
        }
        var selfKind = d.Params[0].Kind;
        var normal = d.Params.Where(p => p.Kind == ParamKind.Normal).ToList();
        var callDecl = d with { Params = normal };

        var savedAll = (_typeParams, _typeBounds);
        _typeParams = outer.Params;
        _typeBounds = outer.Bounds;
        var saved = PushGenerics(d.Generics);
        var genv = CurrentGenerics();

        var ptypes = new List<SType>();
        foreach (var p in normal)
        {
            if (p.Type is null)
            {
                Report(Templates.CannotInfer(p.Span, $"the type of parameter '{p.Name}'", $"Annotate it: {p.Name}::i32 (or another type)."));
                ptypes.Add(SType.Error);
            }
            else ptypes.Add(ResolveType(p.Type));
        }
        SType? ret = d.ReturnType is not null ? ResolveType(d.ReturnType)
            : d.ExprBody is not null ? null  // inferred from the body
            : Types.Unit;
        PopGenerics(saved);
        (_typeParams, _typeBounds) = savedAll;

        var sym = new Symbol(d.Name, SymbolKind.Function, ret is null ? null : new FuncSType(ptypes, ret), false, d.Span)
        {
            Decl = d, DeclScope = scope, ParamTypes = ptypes, SelfType = selfType, ClassCtx = cls, SelfKind = selfKind,
        };
        _fnGeneric[d] = genv;
        if (sym.Type is FuncSType ft) _info.SetFn(d, ft);

        var bounds = d.Generics.Where(g => genv.Bounds.ContainsKey(g.Name)).Select(g => (g.Name, genv.Bounds[g.Name])).ToList();
        if (bounds.Count > 0) { _declBounds[d] = bounds; _declBounds[callDecl] = bounds; }

        return new MethodInfo(d.Name, d, secret, selfKind, owner) { Sym = sym, CallDecl = callDecl };
    }

    private void DeclareClassMethods(ClassInfo ci, Scope scope)
    {
        var cd = ci.Decl!;
        var genv = _classGeneric[ci];
        var selfType = ci.SelfType;
        foreach (var mm in cd.Methods)
        {
            var d = mm.Fn;
            if (d.OpSymbol is not null && !OverloadableOps.Contains(d.OpSymbol))
            { Report(Templates.BadOverload(d.Span, d.OpSymbol)); continue; }
            if (ci.Methods.TryGetValue(d.Name, out var dup))
            { Report(Templates.Redeclared(d.Span, d.Name, dup.Decl?.Span.Line ?? 0)); continue; }
            var mi = DeclareMethod(ci, d, mm.IsSecret, scope, selfType, ci, genv);
            if (mi is null) continue;
            if (d.Name is "init" or "deinit" && mi.Sym!.Type is FuncSType { Ret: var r } && !Types.Unit.Equals(r) && !r.IsError)
                Report(Templates.TypeMismatch(d.ReturnType?.Span ?? d.Span, N(r), "unit (constructors and destructors return nothing)"));
            if (d.Name == "deinit" && mi.CallDecl!.Params.Count > 0)
                Report(Templates.WrongArgCount(d.Span, "deinit", 0, mi.CallDecl.Params.Count));
            ci.Methods[d.Name] = mi;
            if (d.OpSymbol is not null) ci.Ops[d.OpSymbol] = mi;
        }
    }

    private void DeclareTraitMethods(TraitInfo ti, Scope scope)
    {
        var td = ti.Decl!;
        var saved = PushGenerics(td.Generics.Select(g => g).ToList());
        var genv = CurrentGenerics();
        PopGenerics(saved);
        foreach (var d in td.Methods)
        {
            if (d.OpSymbol is not null)
            { Report(Templates.UnexpectedToken(d.Span, $"fn op {d.OpSymbol}", "an ordinary method (operators are defined in classes or extend blocks)")); continue; }
            if (ti.Method(d.Name) is { } dup) { Report(Templates.Redeclared(d.Span, d.Name, dup.Decl?.Span.Line ?? 0)); continue; }
            var mi = DeclareMethod(ti, d, false, scope, ti.SelfType, null, genv);
            if (mi is not null) ti.Methods.Add(mi);
        }
    }

    private void DeclareExtendHeader(ExtInfo ei)
    {
        var ed = ei.Decl;
        var target = ResolveType(ed.Target);
        ei.Target = target;
        if (ed.Trait is not null)
        {
            var tt = ResolveType(ed.Trait);
            if (tt is TraitSType ts) ei.Trait = ts;
            else if (!tt.IsError) Report(Templates.TypeMismatch(ed.Trait.Span, N(tt), "a trait (written after 'with')"));
        }
        if (target.IsError) return;
        _enums.AddExtension(ei);

        if (ei.Trait is { } trait)
        {
            if (target is ClassSType cs) { if (!cs.Info.Traits.Any(t => ReferenceEquals(t.Info, trait.Info))) cs.Info.Traits.Add(trait); }
            else if (trait.Info.IsBuiltinIterable)
                Report(Templates.UnexpectedToken(ed.Trait!.Span, "Iterable", "a trait other than the built-in 'Iterable' for a non-class type (built-in iterables already have it; give a class 'with Iterable<T>')"));
            else trait.Info.Impls.Add(target);
        }
    }

    private void DeclareExtendMethods(ExtInfo ei, Scope scope)
    {
        if (ei.Target.IsError) return;
        var genv = CurrentGenerics();
        foreach (var d in ei.Decl.Methods)
        {
            if (d.OpSymbol is not null && (!OverloadableOps.Contains(d.OpSymbol) || ei.Target is not ClassSType))
            {
                if (!OverloadableOps.Contains(d.OpSymbol)) Report(Templates.BadOverload(d.Span, d.OpSymbol));
                else Report(Templates.UnexpectedToken(d.Span, $"fn op {d.OpSymbol}", "an operator overload only on a class (built-in types already own their operators)"));
                continue;
            }
            if (ei.Method(d.Name) is { } dup) { Report(Templates.Redeclared(d.Span, d.Name, dup.Decl?.Span.Line ?? 0)); continue; }
            var mi = DeclareMethod(ei, d, false, scope, ei.Target, null, genv);
            if (mi is not null) ei.Methods.Add(mi);
        }
    }

    // ---- whole-program validation ----

    private FuncSType? KnownSig(MethodInfo m) => m.FixedSig ?? m.Sym?.Type as FuncSType;

    /// <summary>The signature of a method, checking the body first when its return type is inferred.</summary>
    private FuncSType? SigOf(MethodInfo m, SourceSpan useSpan)
    {
        if (m.FixedSig is not null) return m.FixedSig;
        var t = FunctionType(m.Sym!, useSpan);
        return t as FuncSType;
    }

    private void ValidateClass(ClassInfo ci)
    {
        var cd = ci.Decl!;
        var self = ci.SelfType;

        // Overrides keep the parent's shape (parameters, self kind and compatible return).
        foreach (var m in ci.Methods.Values)
        {
            if (m.Name is "init" or "deinit" || m.Decl!.OpSymbol is not null) continue;
            foreach (var up in self.Chain().Skip(1))
            {
                if (!up.Info.Methods.TryGetValue(m.Name, out var pm)) continue;
                CompareOverride(m, pm, up, m.Decl.Span);
                break;
            }
        }
        foreach (var m in ci.Methods.Values)
            if (m.Decl!.OpSymbol is "==" or "!=" or "<" or ">" && KnownSig(m) is { } os && !os.Ret.Equals(Types.Bool) && !os.Ret.IsError)
                Report(Templates.TypeMismatch(m.Decl.ReturnType?.Span ?? m.Decl.Span, N(os.Ret), "bool (comparison operators answer yes or no)"));

        // Trait obligations: every required method must exist with the right signature.
        foreach (var tr in ci.Traits)
        {
            var missing = new List<string>();
            foreach (var req in tr.Info.Methods.Where(x => !x.HasBody))
            {
                var found = FindMethodInChainOrExt(self, req.Name);
                var want = KnownSig(req);
                var have = found is null ? null : KnownSig(found);
                if (found is null) { missing.Add(req.Name); continue; }
                if (want is not null && have is not null)
                {
                    var tw = (FuncSType)tr.Substitute(want);
                    if (!SameShape(have, tw) || (req.SelfKind == ParamKind.PokeSelf && !found.Mutates))
                        missing.Add($"{req.Name} (with the signature fn {req.Name}({(req.SelfKind == ParamKind.PokeSelf ? "~self" : "self")}{string.Concat(tw.Params.Select(p => ", " + N(p)))})::{N(tw.Ret)})");
                }
            }
            if (missing.Count > 0) Report(Templates.MissingTraitMethods(cd.NameSpan, ci.Name, tr.Name, string.Join(", ", missing)));
        }

        // Diamond rule: two traits supplying the same default need an explicit override in the class.
        var providers = new Dictionary<string, TraitSType>(StringComparer.Ordinal);
        foreach (var tr in ci.Traits)
            foreach (var dm in tr.Info.Methods.Where(x => x.HasBody))
            {
                if (self.Chain().Any(c => c.Info.Methods.ContainsKey(dm.Name))) continue;
                if (providers.TryGetValue(dm.Name, out var first) && !ReferenceEquals(first.Info, tr.Info))
                    Report(Templates.AmbiguousMember(cd.NameSpan, dm.Name, $"trait '{first.Name}'", $"trait '{tr.Name}'"));
                else providers[dm.Name] = tr;
            }

        // Init chain: a parent that needs arguments must be introduced with super.init(...).
        var init = ci.Init;
        if (init is not null && ci.Parent is { } parent && init.Decl!.BlockBody is { } initBody)
        {
            bool calls = AstWalk.ExprsIn(initBody.Statements).Any(IsSuperInitCall);
            if (!calls && FindInit(parent) is { } pinit && InitNeedsArgs(pinit))
                Report(Templates.SuperInitRequired(init.Decl.Span, ci.Name, parent.Info.Name));
        }

        // Fields without a default must be set by init.
        foreach (var f in ci.Fields)
        {
            if (f.Default is not null) continue;
            bool assigned = init?.Decl?.BlockBody is { } body
                && AstWalk.Statements(body.Statements).OfType<AssignStmt>().Any(a =>
                    a.Op == TokenKind.Assign && a.Target is MemberExpr { Target: NameExpr { Name: "self" } } me && me.Name == f.Name);
            if (!assigned) Report(Templates.UninitializedField(f.Span, ci.Name, f.Name));
        }
    }

    public static bool IsSuperInitCall(Expr e) =>
        e is CallExpr { Callee: MemberExpr { Name: "init", Target: NameExpr { Name: "super" } } };

    private bool InitNeedsArgs(MethodInfo init) => init.CallDecl is { } cd && cd.Params.Any(p => p.Default is null);

    private ClassSType? InitOwner(ClassSType cs)
    {
        foreach (var up in cs.Chain()) if (up.Info.Init is not null) return up;
        return null;
    }

    private MethodInfo? FindInit(ClassSType cs) => InitOwner(cs)?.Info.Init;

    private static bool SameShape(FuncSType a, FuncSType b) =>
        a.Params.Count == b.Params.Count && a.Params.Zip(b.Params).All(p => p.First.Equals(p.Second) || p.First.IsError || p.Second.IsError || p.First.IsAny || p.Second.IsAny)
        && (a.Ret.Equals(b.Ret) || Types.Compatible(a.Ret, b.Ret) || a.Ret.IsError || b.Ret.IsError);

    private void CompareOverride(MethodInfo child, MethodInfo parent, ClassSType parentType, SourceSpan span)
    {
        var cs = KnownSig(child);
        var ps = KnownSig(parent) is { } raw ? (FuncSType)parentType.Substitute(raw) : null;
        if (child.SelfKind != parent.SelfKind)
        {
            Report(Templates.BadOverride(span, child.Name, $"takes '{(child.SelfKind == ParamKind.PokeSelf ? "~self" : "self")}' where the parent takes '{(parent.SelfKind == ParamKind.PokeSelf ? "~self" : "self")}'"));
            return;
        }
        if (cs is null || ps is null) return;
        if (cs.Params.Count != ps.Params.Count)
            Report(Templates.BadOverride(span, child.Name, $"takes {cs.Params.Count} parameter(s) where the parent takes {ps.Params.Count}"));
        else if (!cs.Params.Zip(ps.Params).All(p => p.First.Equals(p.Second) || p.First.IsError || p.Second.IsError))
            Report(Templates.BadOverride(span, child.Name,
                $"has parameter types ({string.Join(", ", cs.Params.Select(N))}) where the parent has ({string.Join(", ", ps.Params.Select(N))})"));
        else if (!Types.Compatible(cs.Ret, ps.Ret))
            Report(Templates.BadOverride(span, child.Name, $"returns '{N(cs.Ret)}' where the parent returns '{N(ps.Ret)}'"));
    }

    private void ValidateExtend(ExtInfo ei)
    {
        if (ei.Target.IsError) return;
        // An extend-with block must supply the trait's required methods.
        if (ei.Trait is { } trait && !(ei.Target is ClassSType))
            ValidateTraitImpl(ei, trait, ei.Target, ei.Decl.Span, ei.Methods);
        else if (ei.Trait is { } t2 && ei.Target is ClassSType cs)
        {
            var self = cs;
            var missing = new List<string>();
            foreach (var req in t2.Info.Methods.Where(x => !x.HasBody))
            {
                var found = FindMethodInChainOrExt(self, req.Name) ?? ei.Method(req.Name);
                if (found is null) missing.Add(req.Name);
            }
            if (missing.Count > 0) Report(Templates.MissingTraitMethods(ei.Decl.Span, N(ei.Target), t2.Name, string.Join(", ", missing)));
        }

        // Two extend blocks on the same type must not both define a method.
        var key = BaseName(ei.Target);
        foreach (var other in _enums.Extensions())
        {
            if (ReferenceEquals(other, ei)) break; // only earlier ones; later ones report against this one
            if (other.Target.IsError || BaseName(other.Target) != key) continue;
            foreach (var m in ei.Methods)
                if (other.Method(m.Name) is not null)
                    Report(Templates.AmbiguousMember(m.Decl!.Span, m.Name, $"'extend {N(other.Target)}'", $"'extend {N(ei.Target)}'"));
        }
    }

    private void ValidateTraitImpl(ExtInfo ei, TraitSType trait, SType target, SourceSpan span, List<MethodInfo> provided)
    {
        var missing = new List<string>();
        foreach (var req in trait.Info.Methods.Where(x => !x.HasBody))
        {
            var found = provided.FirstOrDefault(p => p.Name == req.Name);
            if (found is null) { missing.Add(req.Name); continue; }
            var want = KnownSig(req);
            var have = KnownSig(found);
            if (want is not null && have is not null && !SameShape(have, (FuncSType)trait.Substitute(want))) missing.Add($"{req.Name} (wrong signature)");
        }
        if (missing.Count > 0) Report(Templates.MissingTraitMethods(span, N(target), trait.Name, string.Join(", ", missing)));
    }

    /// <summary>The key extend blocks and runtime tables use for a receiver type: its name without type arguments.</summary>
    internal static string BaseName(SType t) => t switch
    {
        ClassSType c => c.Info.Name,
        EnumInstanceSType e => e.Base.EnumName,
        ListSType => "List", SetSType => "Set", MapSType => "Map", TupleSType => "tuple",
        RangeSType => "range", FuncSType => "fn", IterSType => "iter",
        _ => t.Name,
    };

    // ---- member lookup ----

    /// <summary>A method found for a receiver, with its signature (no self) already specialised to the receiver's type arguments.</summary>
    private sealed record MethodHit(MethodInfo Method, FuncSType Sig);

    private MethodInfo? FindMethodInChainOrExt(ClassSType cs, string name)
    {
        foreach (var up in cs.Chain())
            if (up.Info.Methods.TryGetValue(name, out var m)) return m;
        foreach (var ext in _enums.Extensions())
            if (ext.Target is ClassSType et && cs.Chain().Any(c => ReferenceEquals(c.Info, et.Info)) && ext.Method(name) is { } em) return em;
        return null;
    }

    /// <summary>
    /// Finds a method by name. Order: the type's own members (a class's chain, nearest first), then <c>extend</c> blocks,
    /// then trait defaults.
    /// </summary>
    private MethodHit? FindMethod(SType recv, string name, SourceSpan span)
    {
        FuncSType? Sig(MethodInfo m, Func<SType, SType> subst)
        {
            var raw = SigOf(m, span);
            return raw is null ? null : (FuncSType)subst(raw);
        }
        static SType Id(SType t) => t;

        switch (recv)
        {
            case ClassSType cs:
            {
                foreach (var up in cs.Chain())
                    if (up.Info.Methods.TryGetValue(name, out var m) && Sig(m, up.Substitute) is { } s) return new MethodHit(m, s);
                foreach (var ext in _enums.Extensions())
                    if (ext.Target is ClassSType et && cs.Chain().Any(c => ReferenceEquals(c.Info, et.Info)) && ext.Method(name) is { } em
                        && Sig(em, Id) is { } es) return new MethodHit(em, es);
                foreach (var up in cs.Chain())
                    foreach (var tr in up.Info.Traits)
                    {
                        var ts = (TraitSType)up.Substitute(tr);
                        if (tr.Info.Method(name) is { HasBody: true } dm && Sig(dm, ts.Substitute) is { } ds) return new MethodHit(dm, ds);
                    }
                foreach (var ext in _enums.Extensions())
                    if (ext.Trait is { } et2 && ext.Target is ClassSType ec && cs.Chain().Any(c => ReferenceEquals(c.Info, ec.Info))
                        && et2.Info.Method(name) is { HasBody: true } xm && Sig(xm, et2.Substitute) is { } xs) return new MethodHit(xm, xs);
                return null;
            }
            case TypeParamSType { Bound: { } b }:
                return b.Info.Method(name) is { } bm && Sig(bm, b.Substitute) is { } bs ? new MethodHit(bm, bs) : null;
            case TraitSType ts:
                return ts.Info.Method(name) is { } tm && Sig(tm, ts.Substitute) is { } tsig ? new MethodHit(tm, tsig) : null;
            default:
            {
                foreach (var ext in _enums.Extensions())
                    if (!ext.Target.IsError && ext.Method(name) is { } em && SameType(recv, ext.Target) && Sig(em, Id) is { } es)
                        return new MethodHit(em, es);
                foreach (var ext in _enums.Extensions())
                    if (ext.Trait is { } et && !ext.Target.IsError && SameType(recv, ext.Target)
                        && et.Info.Method(name) is { HasBody: true } xm && Sig(xm, et.Substitute) is { } xs) return new MethodHit(xm, xs);
                foreach (var info in TraitsImplementedBy(recv))
                    if (info.Info.Method(name) is { HasBody: true } dm && Sig(dm, info.Substitute) is { } ds) return new MethodHit(dm, ds);
                return null;
            }
        }
    }

    private static bool SameType(SType a, SType b) => a.Equals(b) || (Types.Compatible(a, b) && Types.Compatible(b, a));

    private IEnumerable<TraitSType> TraitsImplementedBy(SType t)
    {
        foreach (var ext in _enums.Extensions())
            if (ext.Trait is { } tr && !ext.Target.IsError && SameType(t, ext.Target)) yield return tr;
    }

    /// <summary>A field of a class (own or inherited) with its type specialised to the receiver's type arguments.</summary>
    private (FieldInfo Field, SType Type)? FindField(ClassSType cs, string name)
    {
        foreach (var up in cs.Chain())
            if (up.Info.OwnField(name) is { } f) return (f, up.Substitute(f.Type));
        return null;
    }

    // ---- member reads and field assignment ----

    private SType CheckMember(MemberExpr m, Scope scope)
    {
        if (m.Target is NameExpr { Name: "super" } sn)
        {
            Report(Templates.UnexpectedToken(m.Span, "super." + m.Name, "a call such as super.method(...); 'super' reaches the parent's methods, not fields"));
            return SType.Error;
        }
        var recv = Infer(m.Target, scope);
        if (recv.IsError) return SType.Error;
        if (recv.IsAny) return SType.Any;

        if (recv is ClassSType cs)
        {
            if (FindField(cs, m.Name) is { } hit)
            {
                if (hit.Field.IsSecret && !ReferenceEquals(hit.Field.Owner, _curClass))
                    Report(Templates.SecretAccess(m.NameSpan, m.Name, hit.Field.Owner.Name));
                return hit.Type;
            }
            if (FindMethod(recv, m.Name, m.NameSpan) is not null)
            {
                Report(Templates.UnexpectedToken(m.Span, $".{m.Name}", $"a call such as {m.Name}(...); methods are not first-class values"));
                return SType.Error;
            }
        }
        Report(Templates.UndefinedName(m.NameSpan, $"{N(recv)}.{m.Name}"));
        return SType.Error;
    }

    private void CheckFieldAssign(AssignStmt a, MemberExpr me, Scope scope)
    {
        var ft = CheckMember(me, scope);
        _info.Set(me, ft);
        if (ft.IsError) { Infer(a.Value, scope); return; }

        var recv = _info.TypeOf(me.Target);
        if (recv is ClassSType cs && FindMethod(recv, me.Name, me.NameSpan) is null)
            RequireMutableRoot(me.Target, scope, isSelfOk: true);

        if (a.Op == TokenKind.Assign)
        {
            var vt = Infer(a.Value, scope, ft);
            if (!Types.Compatible(vt, ft)) Mismatch(a.Value.Span, vt, ft);
            return;
        }
        var baseOp = CompoundBase(a.Op)!.Value;
        var rt = Infer(a.Value, scope, ft);
        var result = ArithmeticType(baseOp, ft, rt, a.Span, OpText(baseOp) + "=");
        if (!Types.Compatible(result, ft)) Mismatch(a.Value.Span, result, ft);
    }

    /// <summary>Mutation through a name needs a <c>var</c> root; through <c>self</c> it needs a poke (<c>~self</c>) method or <c>init</c>/<c>deinit</c>.</summary>
    private void RequireMutableRoot(Expr target, Scope scope, bool isSelfOk)
    {
        var root = RootName(target);
        if (root is not null && root.Name == "self" && scope.Lookup("self") is { Kind: SymbolKind.Variable } selfSym)
        {
            if (!selfSym.IsMutable) Report(Templates.ImmutableSelf(target.Span, _curMethod?.Name ?? "method"));
            return;
        }
        RequireMutable(target, scope);
    }

    // ---- calls: constructors, methods, static natives, super ----

    private SType CheckConstruct(CallExpr c, NameExpr name, ClassInfo ci, Scope scope, SType? hint)
    {
        if (ci.IsBuiltin)
        {
            foreach (var a in c.Args) Infer(a.Value, scope);
            Report(Templates.NotCallable(name.Span, $"the built-in class '{ci.Name}' (it is created by the runtime)"));
            return SType.Error;
        }
        var self = ci.SelfType;
        var init = FindInit(self);
        var owner = InitOwner(self);
        var ptypes = init is null ? [] : ((FuncSType)owner!.Substitute(SigOf(init, c.Span) ?? new FuncSType([], Types.Unit))).Params;

        // The synthetic declaration carries the class's generics (and bounds) so ordinary call checking infers them.
        var gparams = ci.Decl?.Generics ?? [];
        var initGenerics = init?.Decl?.Generics ?? [];
        var ctorDecl = new FnDecl(ci.Name, null, [.. gparams, .. initGenerics], init?.CallDecl?.Params ?? [], null, null, null, c.Span);
        var bounds = new List<(string, TraitSType)>();
        for (int i = 0; i < gparams.Count; i++) if (ci.Bounds.Count > i && ci.Bounds[i] is { } b) bounds.Add((gparams[i].Name, b));
        if (init is not null && _declBounds.TryGetValue(init.CallDecl!, out var ib)) bounds.AddRange(ib);
        if (bounds.Count > 0) _declBounds[ctorDecl] = bounds;

        var ft = new FuncSType(ptypes, self);
        _info.Set(name, ft);
        var sym = new Symbol(ci.Name, SymbolKind.Function, ft, false, c.Span) { Decl = ctorDecl };
        var result = FinishCall(c, ft, sym, scope, null, hint);
        // `b::B<i32> = B()`: nothing in the call fixes T, but the annotation does.
        if (hint is ClassSType hc && ReferenceEquals(hc.Info, ci) && !result.IsError && ContainsAny(result) && !ContainsAny(hc)) result = hc;
        if (ci.TypeParams.Count > 0 && !result.IsError && ContainsAny(result))
            Report(Templates.CannotInfer(c.Span, $"the type arguments of '{ci.Name}'", $"Annotate it: x::{ci.Name}<i32> = {ci.Name}(...)"));
        return result;
    }

    /// <summary><c>super.init(...)</c> and <c>super.method(...)</c>: the parent's version, not the override.</summary>
    private SType CheckSuperCall(CallExpr c, MemberExpr m, Scope scope)
    {
        void InferAll() { foreach (var a in c.Args) Infer(a.Value, scope); }
        if (_curClass is null || _curMethod is null)
        {
            InferAll();
            Report(Templates.SelfOutside(m.Target.Span, "super"));
            return SType.Error;
        }
        if (_curClass.Parent is not { } parent)
        {
            InferAll();
            Report(Templates.UndefinedName(m.Target.Span, $"super (class '{_curClass.Name}' has no parent)"));
            return SType.Error;
        }
        if (m.Name == "init")
        {
            if (_curMethod.Name != "init")
            {
                InferAll();
                Report(Templates.UnexpectedToken(m.Span, "super.init", "'super.init(...)' only inside 'init'"));
                return SType.Error;
            }
            var init = FindInit(parent);
            var owner = InitOwner(parent);
            var ptypes = init is null ? [] : ((FuncSType)owner!.Substitute(SigOf(init, c.Span) ?? new FuncSType([], Types.Unit))).Params;
            var ft = new FuncSType(ptypes, Types.Unit);
            _info.Set(m, ft);
            return FinishCall(c, ft, new Symbol("init", SymbolKind.Function, ft, false, c.Span) { Decl = init?.CallDecl ?? NoParamDecl(c.Span, "init") }, scope, null);
        }
        var hit = FindMethod(parent, m.Name, m.NameSpan);
        if (hit is null || !hit.Method.HasBody)
        {
            InferAll();
            Report(Templates.UndefinedName(m.NameSpan, $"super.{m.Name} (the parent '{parent.Info.Name}' has no such method)"));
            return SType.Error;
        }
        if (hit.Method.Mutates) RequireMutableRoot(new NameExpr("self", m.Target.Span), scope, true);
        _info.Set(m, hit.Sig);
        return FinishCall(c, hit.Sig, MethodSymbol(hit), scope, null);
    }

    private static FnDecl NoParamDecl(SourceSpan span, string name) => new(name, null, [], [], null, null, null, span);

    private static Symbol MethodSymbol(MethodHit hit) =>
        new(hit.Method.Name, SymbolKind.Function, hit.Sig, false, hit.Method.Decl?.Span ?? SourceSpan.None) { Decl = hit.Method.CallDecl };

    /// <summary>Static functions on a built-in type name: <c>Socket.tcp(host, port)</c>, <c>Res.ok(body)</c>.</summary>
    private SType CheckStaticCall(CallExpr c, MemberExpr m, string typeName, IReadOnlyDictionary<string, NativeMembers.Sig> statics, Scope scope)
    {
        void InferAll() { foreach (var a in c.Args) Infer(a.Value, scope); }
        if (!statics.TryGetValue(m.Name, out var sig))
        {
            InferAll();
            Report(Templates.UndefinedName(m.NameSpan, $"{typeName}.{m.Name}"));
            return SType.Error;
        }
        var ft = new FuncSType(sig.Params, sig.Ret);
        _info.Set(m, ft);
        if (c.Args.Count != sig.Params.Count || c.Args.Any(a => a.Name is not null))
        {
            InferAll();
            Report(Templates.WrongArgCount(c.Span, $"{typeName}.{m.Name}", sig.Params.Count, c.Args.Count));
            return sig.Ret;
        }
        for (int i = 0; i < sig.Params.Count; i++)
        {
            var at = Infer(c.Args[i].Value, scope, sig.Params[i]);
            if (!Types.Compatible(at, sig.Params[i])) Mismatch(c.Args[i].Value.Span, at, sig.Params[i]);
        }
        return sig.Ret;
    }

    /// <summary>Methods of native handles and temporal values (<c>f.read_all()</c>, <c>d.year()</c>).</summary>
    private SType CheckNativeMethod(CallExpr c, MemberExpr m, SType recv, IReadOnlyDictionary<string, NativeMembers.Sig> methods, Scope scope)
    {
        void InferAll() { foreach (var a in c.Args) Infer(a.Value, scope); }
        if (!methods.TryGetValue(m.Name, out var sig))
        {
            InferAll();
            Report(Templates.UndefinedName(m.NameSpan, $"{N(recv)}.{m.Name}"));
            return SType.Error;
        }
        _info.Set(m, new FuncSType(sig.Params, sig.Ret));
        if (c.Args.Count != sig.Params.Count || c.Args.Any(a => a.Name is not null))
        {
            InferAll();
            Report(Templates.WrongArgCount(c.Span, $"{N(recv)}.{m.Name}", sig.Params.Count, c.Args.Count));
            return sig.Ret;
        }
        for (int i = 0; i < sig.Params.Count; i++)
        {
            var at = Infer(c.Args[i].Value, scope, sig.Params[i]);
            if (!Types.Compatible(at, sig.Params[i])) Mismatch(c.Args[i].Value.Span, at, sig.Params[i]);
        }
        return sig.Ret;
    }

    /// <summary>A method of a user class, trait, type parameter or extend block.</summary>
    private SType? CheckUserMethod(CallExpr c, MemberExpr m, SType recv, Scope scope)
    {
        // A field holding a function: `obj.callback(x)`.
        if (recv is ClassSType cs && FindField(cs, m.Name) is { } fh && FindMethod(recv, m.Name, m.NameSpan) is null)
        {
            if (fh.Field.IsSecret && !ReferenceEquals(fh.Field.Owner, _curClass))
                Report(Templates.SecretAccess(m.NameSpan, m.Name, fh.Field.Owner.Name));
            _info.Set(m, fh.Type);
            return FinishCall(c, fh.Type, null, scope, null);
        }

        var hit = FindMethod(recv, m.Name, m.NameSpan);
        if (hit is null) return null;

        if (recv is ClassSType rc && rc.Chain().Select(x => x.Info.Methods.GetValueOrDefault(m.Name)).FirstOrDefault(x => x is not null) is { IsSecret: true } sm
            && !ReferenceEquals(sm.Owner, _curClass))
            Report(Templates.SecretAccess(m.NameSpan, m.Name, ((ClassInfo)sm.Owner).Name));

        if (hit.Method.Mutates) RequireMutableRoot(m.Target, scope, true);
        _info.Set(m, hit.Sig);
        return FinishCall(c, hit.Sig, MethodSymbol(hit), scope, null);
    }

    // ---- bounded generics ----

    /// <summary>After inference: every type argument must implement the trait its parameter was bounded by.</summary>
    private void EnforceBounds(CallExpr c, FnDecl decl, Dictionary<string, SType> vars)
    {
        if (!_declBounds.TryGetValue(decl, out var bounds)) return;
        foreach (var (name, bound) in bounds)
        {
            if (!vars.TryGetValue(name, out var v)) continue;
            var arg = Types.Resolve(v);
            if (arg.IsAny || arg.IsError) continue;
            var want = (TraitSType)Types.Resolve(Types.Substitute(bound, vars));
            if (!Types.ImplementsTrait(arg, want))
                Report(Templates.TypeMismatch(c.Span, N(arg), want.Name));
        }
    }

    // ---- operator overloads ----

    private MethodInfo? FindOp(ClassSType cs, string symbol)
    {
        var name = "op " + symbol;
        foreach (var up in cs.Chain())
            if (up.Info.Methods.TryGetValue(name, out var m)) return m;
        foreach (var ext in _enums.Extensions())
            if (ext.Target is ClassSType et && cs.Chain().Any(c => ReferenceEquals(c.Info, et.Info)) && ext.Method(name) is { } em) return em;
        return null;
    }

    private static string? OverloadSymbol(TokenKind op) => op switch
    {
        TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Star => "*", TokenKind.Slash => "/", TokenKind.At => "@",
        TokenKind.EqEq => "==", TokenKind.BangEq => "!=", TokenKind.Lt => "<", TokenKind.Gt => ">", _ => null,
    };

    /// <summary>
    /// Resolves <c>a op b</c> through a user-defined operator on the left operand's class. <c>!=</c> falls back to the
    /// negation of <c>==</c>. Returns false when the left side is not a class (or has no such operator).
    /// </summary>
    private bool TryOverload(TokenKind op, SType l, SType r, SourceSpan span, out SType result)
    {
        result = SType.Error;
        if (l is not ClassSType cs || OverloadSymbol(op) is not { } sym) return false;
        var m = FindOp(cs, sym) ?? (op == TokenKind.BangEq ? FindOp(cs, "==") : null);
        if (m is null) return false;
        var sig = SigOf(m, span);
        if (sig is null) return true;
        var owner = cs.Chain().FirstOrDefault(c => ReferenceEquals(c.Info, m.Owner)) ?? cs;
        var ssig = (FuncSType)owner.Substitute(sig);
        if (ssig.Params.Count != 1)
        {
            Report(Templates.WrongArgCount(span, $"operator {sym}", 1, ssig.Params.Count));
            return true;
        }
        if (!r.IsError && !Types.Compatible(r, ssig.Params[0]))
        { Mismatch(span, r, ssig.Params[0]); return true; }
        result = op is TokenKind.EqEq or TokenKind.BangEq or TokenKind.Lt or TokenKind.Gt ? Types.Bool : ssig.Ret;
        return true;
    }

    private SType? TryIndexOverload(IndexExpr ix, ClassSType cs, Scope scope)
    {
        var m = FindOp(cs, "[]");
        if (m is null) return null;
        var sig = SigOf(m, ix.Span);
        if (sig is null) return SType.Error;
        var owner = cs.Chain().FirstOrDefault(c => ReferenceEquals(c.Info, m.Owner)) ?? cs;
        var ssig = (FuncSType)owner.Substitute(sig);
        var idx = ix.Indices[0];
        var it = Infer(idx, scope, ssig.Params.Count == 1 ? ssig.Params[0] : null);
        if (ssig.Params.Count != 1) { Report(Templates.WrongArgCount(ix.Span, "operator []", 1, ssig.Params.Count)); return ssig.Ret; }
        if (!Types.Compatible(it, ssig.Params[0])) Mismatch(idx.Span, it, ssig.Params[0]);
        return ssig.Ret;
    }

    // ---- bodies ----

    private void CheckClassBodies(ClassDecl cd)
    {
        if (!_classOf.TryGetValue(cd, out var ci)) return;
        var outerClass = _curClass;
        var outerParams = (_typeParams, _typeBounds);
        _curClass = ci;
        if (_classGeneric.TryGetValue(ci, out var genv)) { _typeParams = genv.Params; _typeBounds = genv.Bounds; }
        foreach (var f in ci.Fields)
        {
            if (f.Default is null) continue;
            var dt = Infer(f.Default, new Scope(_globals), f.Type);
            if (!Types.Compatible(dt, f.Type)) Mismatch(f.Default.Span, dt, f.Type);
        }
        (_typeParams, _typeBounds) = outerParams;
        _curClass = outerClass;
        foreach (var m in ci.Methods.Values) EnsureChecked(m.Sym!);
    }

    private void CheckTraitBodies(TraitDecl td)
    {
        if (!_traitOf.TryGetValue(td, out var ti)) return;
        foreach (var m in ti.Methods) if (m.HasBody) EnsureChecked(m.Sym!);
    }

    private void CheckExtendBodies(ExtendDecl ed)
    {
        if (!_extOf.TryGetValue(ed, out var ei)) return;
        foreach (var m in ei.Methods) EnsureChecked(m.Sym!);
    }

    private void CheckDrop(DropStmt d, Scope scope)
    {
        var t = Infer(d.Target, scope);
        if (d.Target is not NameExpr)
        {
            Report(Templates.UnexpectedToken(d.Target.Span, "an expression", "a variable name after 'drop', as in 'drop x'"));
            return;
        }
        if (t.IsError || t.IsAny) return;
        bool droppable = t is ClassSType or TraitSType or TypeParamSType || ReferenceEquals(t, Types.FileT) || ReferenceEquals(t, Types.SocketT) || ReferenceEquals(t, Types.ServerT);
        if (!droppable) Report(Templates.TypeMismatch(d.Target.Span, N(t), "a class instance, File, Socket or Server (only those have something to clean up)"));
    }

    private bool CheckTry(TryStmt t, Scope scope)
    {
        bool bodyReturns = CheckStatements(t.Body.Statements, new Scope(scope), null);
        var handlerScope = new Scope(scope);
        Declare(handlerScope, new Symbol(t.CatchName, SymbolKind.Variable, BuiltinClasses.CatchType, false, t.CatchNameSpan), t.CatchNameSpan);
        bool handlerReturns = CheckStatements(t.Handler.Statements, handlerScope, null);
        return bodyReturns && handlerReturns;
    }
}
