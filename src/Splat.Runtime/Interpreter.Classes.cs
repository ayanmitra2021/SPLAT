using Splat.Core;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Runtime;

// Phases 8-10: classes, ownership and deinit, traits and extend dispatch, operators, drop, try/catch.
public sealed partial class Interpreter
{
    private readonly Dictionary<string, ClassDef> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TraitDef> _traits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Closure>> _ext = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _extTraits = new(StringComparer.Ordinal);
    private readonly List<ClassDef> _classStack = [];
    private bool _panicking;
    private readonly object _gil = new();

    private void InitBuiltinClasses()
    {
        var catchDef = new ClassDef { Name = "Catch", Builtin = true, DisplayHook = i => i.Fields["message"].AsString };
        foreach (var n in new[] { "message", "kind", "code", "line", "span", "trace" }) catchDef.Fields.Add(new FieldDef(n, null, false));
        _classes["Catch"] = catchDef;
        var req = new ClassDef { Name = "Req", Builtin = true };
        foreach (var n in new[] { "method", "path", "query", "headers", "body" }) req.Fields.Add(new FieldDef(n, null, false));
        _classes["Req"] = req;
        var res = new ClassDef { Name = "Res", Builtin = true };
        foreach (var n in new[] { "status", "headers", "body" }) res.Fields.Add(new FieldDef(n, null, false));
        _classes["Res"] = res;
    }

    // ---- hoisting ----

    private static string TypeBase(TypeRef t) => t switch
    {
        NamedType n => n.Name,
        TupleType => "tuple",
        FuncType => "fn",
        // The same key Tensor.BaseKey gives a receiver: element type and rank, no dimensions ("f32[,]").
        TensorType tt => (tt.Element is NamedType en ? en.Name : "?") + "[" + new string(',', Math.Max(0, tt.Dims.Count - 1)) + "]",
        _ => "tensor",
    };

    private Closure MethodClosure(FnDecl d, Env env, ClassDef? cls)
    {
        var ft = _info.FnType(d);
        return new Closure
        {
            Name = (cls is null ? "" : cls.Name + ".") + d.Name,
            Params = d.Params.Where(p => p.Kind == ParamKind.Normal).Select(p => new ParamInfo(p.Name, p.Default)).ToList(),
            ExprBody = d.ExprBody,
            BlockBody = d.BlockBody,
            Env = env,
            ReturnsValue = !(ft?.Ret is PrimType { Kind: Prim.Unit }),
            DeclClass = cls,
        };
    }

    private void HoistTypes(IReadOnlyList<Stmt> stmts, Env env)
    {
        var classes = stmts.OfType<ClassDecl>().ToList();
        var defs = new Dictionary<ClassDecl, ClassDef>(ReferenceEqualityComparer.Instance);
        foreach (var cd in classes)
        {
            var def = new ClassDef { Name = cd.Name, Env = env };
            defs[cd] = def;
            _classes[cd.Name] = def;
        }
        foreach (var cd in classes)
        {
            var def = defs[cd];
            if (cd.Parent is NamedType pn && _classes.TryGetValue(pn.Name, out var parent)) def.Parent = parent;
            foreach (var f in cd.Fields) def.Fields.Add(new FieldDef(f.Name, f.Default, f.IsSecret));
            foreach (var t in cd.Traits) if (t is NamedType tn) def.TraitNames.Add(tn.Name);
            foreach (var m in cd.Methods) def.Methods[m.Fn.Name] = MethodClosure(m.Fn, env, def);
            if (def.Methods.ContainsKey("init") && cd.Methods.First(m => m.Fn.Name == "init").Fn.BlockBody is { } body)
                def.InitCallsSuperInit = AstWalk.ExprsIn(body.Statements).Any(TypeChecker.IsSuperInitCall);
        }
        foreach (var td in stmts.OfType<TraitDecl>())
        {
            var trait = new TraitDef { Name = td.Name };
            foreach (var m in td.Methods)
                if (m.ExprBody is not null || m.BlockBody is not null) trait.Defaults[m.Name] = MethodClosure(m, env, null);
            _traits[td.Name] = trait;
        }
        foreach (var ed in stmts.OfType<ExtendDecl>())
        {
            var key = TypeBase(ed.Target);
            if (!_ext.TryGetValue(key, out var table)) _ext[key] = table = new(StringComparer.Ordinal);
            foreach (var m in ed.Methods) table[m.Name] = MethodClosure(m, env, null);
            if (ed.Trait is NamedType tn)
            {
                if (!_extTraits.TryGetValue(key, out var list)) _extTraits[key] = list = [];
                if (!list.Contains(tn.Name)) list.Add(tn.Name);
            }
        }
    }

    // ---- ownership and deinit ----

    private static bool IsKeepAlive(HeapObj o) => o is ServerObj;

    private void ClaimBind(Value v, Expr valueExpr, Env env, Cell cell)
    {
        if (v.Kind == VKind.Object) ClaimObject(v.AsObj, valueExpr, env, cell, env);
        else ClaimDeep(v, cell, env);
    }

    /// <summary>A bind from a plain name moves the object; a bind from anything else claims it if nobody owns it yet.</summary>
    private void ClaimObject(HeapObj o, Expr? expr, Env env, object newOwner, Env ownerEnv)
    {
        if (o.Dead || o.Dying) return;
        if (expr is NameExpr n && env.Find(n.Name) is { } src && ReferenceEquals(o.Owner, src))
        {
            o.Owner = newOwner;
            src.Moved = true;
            src.MovedLine = expr.Span.Line;
            if (newOwner is Cell nc) ownerEnv.AddOwned(nc, o);
        }
        else if (o.Owner is null)
        {
            o.Owner = newOwner;
            if (newOwner is Cell nc) ownerEnv.AddOwned(nc, o);
        }
    }

    /// <summary>Claims unowned objects held inside a collection, tuple or enum payload.</summary>
    private void ClaimDeep(Value v, object owner, Env? env)
    {
        switch (v.Kind)
        {
            case VKind.Object:
                if (v.AsObj is { Owner: null, Dead: false } o)
                {
                    o.Owner = owner;
                    if (owner is Cell c) env?.AddOwned(c, o);
                }
                break;
            case VKind.List: foreach (var i in v.AsList.Items) ClaimDeep(i, owner, env); break;
            case VKind.Tuple: foreach (var i in v.AsTuple) ClaimDeep(i, owner, env); break;
            case VKind.Set: foreach (var i in v.AsSet.Items) ClaimDeep(i, owner, env); break;
            case VKind.Map: foreach (var (k, x) in v.AsMap.Items) { ClaimDeep(k, owner, env); ClaimDeep(x, owner, env); } break;
            case VKind.Enum: foreach (var i in v.AsEnum.Payloads) ClaimDeep(i, owner, env); break;
        }
    }

    private void ClaimField(Value v, Expr? expr, Env env, ClassInst parent)
    {
        if (v.Kind == VKind.Object) ClaimObject(v.AsObj, expr, env, parent, env);
        else ClaimDeep(v, parent, null);
    }

    /// <summary>A value leaving a function (<c>ret x</c> or a tail <c>x</c>) takes the object out of its variable's care.</summary>
    private Value EvalMoveOut(Expr e, Env env)
    {
        var v = Eval(e, env);
        if (_frames.Count > 0 && e is NameExpr n && env.Find(n.Name) is { } cell)
        {
            if (ReleaseValue(v, cell)) { cell.Moved = true; cell.MovedLine = e.Span.Line; }
        }
        return v;
    }

    private static bool ReleaseValue(Value v, Cell cell)
    {
        bool any = false;
        switch (v.Kind)
        {
            case VKind.Object:
                if (ReferenceEquals(v.AsObj.Owner, cell)) { v.AsObj.Owner = null; any = true; }
                break;
            case VKind.List: foreach (var i in v.AsList.Items) any |= ReleaseValue(i, cell); break;
            case VKind.Tuple: foreach (var i in v.AsTuple) any |= ReleaseValue(i, cell); break;
            case VKind.Set: foreach (var i in v.AsSet.Items) any |= ReleaseValue(i, cell); break;
            case VKind.Map: foreach (var (_, x) in v.AsMap.Items) any |= ReleaseValue(x, cell); break;
            case VKind.Enum: foreach (var i in v.AsEnum.Payloads) any |= ReleaseValue(i, cell); break;
        }
        return any;
    }

    /// <summary>Scope exit: destroy what the scope's variables own, last declared first. Skipped after a <c>rip</c>.</summary>
    private void CleanupEnv(Env env)
    {
        if (env.Owned is not { } list) return;
        env.Owned = null;
        if (_panicking) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var (cell, obj) = list[i];
            if (!obj.Dead && !obj.Dying && ReferenceEquals(obj.Owner, cell)) Destroy(obj, SourceSpan.None);
        }
    }

    /// <summary>Ends a REPL session: destroys what every entry's variables still own.</summary>
    public void CloseSession(Env tip)
    {
        for (var e = tip; e is not null; e = e.Parent) CleanupEnv(e);
    }

    private void Destroy(HeapObj o, SourceSpan site)
    {
        if (o.Dead || o.Dying) return;
        o.Dying = true;
        try
        {
            if (o is ClassInst ci) RunDeinit(ci, site);
            else o.Close();
        }
        finally
        {
            o.Dead = true;
            o.DeadLine = site.IsNone ? 0 : site.Line;
        }
    }

    /// <summary>Most-derived deinit first, then each ancestor's. At every level the objects its fields own are destroyed in reverse order.</summary>
    private void RunDeinit(ClassInst o, SourceSpan site)
    {
        foreach (var cd in o.Class.Chain())
        {
            if (cd.Methods.TryGetValue("deinit", out var d))
                Call(d, [], [], site, Value.Obj(o));
            var own = cd.Fields.ToList();
            for (int i = own.Count - 1; i >= 0; i--)
                if (o.Fields.TryGetValue(own[i].Name, out var fv)) DestroyOwnedValue(fv, o, site);
        }
    }

    private void DestroyOwnedValue(Value v, object owner, SourceSpan site)
    {
        switch (v.Kind)
        {
            case VKind.Object:
                if (ReferenceEquals(v.AsObj.Owner, owner)) Destroy(v.AsObj, site);
                break;
            case VKind.List: for (int i = v.AsList.Items.Count - 1; i >= 0; i--) DestroyOwnedValue(v.AsList.Items[i], owner, site); break;
            case VKind.Tuple: foreach (var i in v.AsTuple.Reverse()) DestroyOwnedValue(i, owner, site); break;
            case VKind.Enum: foreach (var i in v.AsEnum.Payloads) DestroyOwnedValue(i, owner, site); break;
        }
    }

    private void CheckAlive(HeapObj o, Expr target)
    {
        if (o.Dead) throw Fail(Templates.UseAfterMove(target.Span, target is NameExpr n ? n.Name : o.TypeName, o.DeadLine));
    }

    private void ExecDrop(DropStmt d, Env env)
    {
        var n = (NameExpr)d.Target;
        var cell = env.Find(n.Name) ?? throw Fail(Templates.Internal(n.Span, $"unbound variable '{n.Name}'"));
        if (cell.Moved) throw Fail(Templates.UseAfterMove(n.Span, n.Name, cell.MovedLine));
        if (cell.V.Kind == VKind.Object)
        {
            if (cell.V.AsObj.Dead) throw Fail(Templates.UseAfterMove(n.Span, n.Name, cell.V.AsObj.DeadLine));
            Destroy(cell.V.AsObj, d.Span);
        }
        cell.Moved = true;
        cell.MovedLine = d.Span.Line;
    }

    // ---- try / catch ----

    private Flow ExecTry(TryStmt t, Env env)
    {
        try
        {
            return ExecBlock(t.Body.Statements, new Env(env), false, out _);
        }
        catch (RuntimeError e) when (e.Diagnostic.Category != DiagnosticCategory.Panic && !_panicking)
        {
            // Unwinding already ran deinit for every scope the error abandoned.
            var handlerEnv = new Env(env);
            handlerEnv.Define(t.CatchName, Value.Obj(NewCatch(e.Diagnostic)), false);
            return ExecBlock(t.Handler.Statements, handlerEnv, false, out _);
        }
    }

    private ClassInst NewCatch(Diagnostic d)
    {
        var o = new ClassInst(_classes["Catch"]);
        o.Fields["message"] = Value.Str(d.Message);
        o.Fields["kind"] = Value.Str(d.Category.ToString());
        o.Fields["code"] = Value.Str(d.Code);
        o.Fields["line"] = Value.Int(VKind.I32, d.Span.IsNone ? 0 : d.Span.Line);
        o.Fields["span"] = Value.Str(d.Span.IsNone ? "" : d.Span.ToString());
        var trace = new SplatList();
        foreach (var l in d.Trace ?? []) trace.Items.Add(Value.Str(l));
        o.Fields["trace"] = Value.List(trace);
        return o;
    }

    // ---- construction ----

    private Value Construct(ClassDef cd, CallExpr c, Env env)
    {
        var obj = new ClassInst(cd);
        foreach (var level in cd.Chain().Reverse())
            foreach (var f in level.Fields)
                if (f.Default is not null)
                {
                    var v = EvalOwned(f.Default, level.Env ?? env);
                    obj.Fields[f.Name] = v;
                    ClaimField(v, f.Default, level.Env ?? env, obj);
                }

        var initOwner = cd.Chain().FirstOrDefault(x => x.Methods.ContainsKey("init"));
        Value[] vals = []; bool[] has = [];
        if (initOwner is not null) PrepareArgs(initOwner.Methods["init"], c, env, out vals, out has);
        var temps = FreshArgs(vals, has);
        RunInit(cd, obj, vals, has, c.Span);
        DestroyTemps(temps, Value.Obj(obj), c.Span);
        return Value.Obj(obj);
    }

    private void PrepareArgs(Closure f, CallExpr c, Env env, out Value[] vals, out bool[] has)
    {
        var map = _info.ArgMap(c);
        vals = new Value[f.Params.Count];
        has = new bool[f.Params.Count];
        for (int i = 0; i < c.Args.Count; i++)
        {
            int target = map is null ? i : map[i];
            if (target < 0 || target >= vals.Length) continue;
            vals[target] = Eval(c.Args[i].Value, env);
            has[target] = true;
        }
    }

    /// <summary>Runs the nearest <c>init</c> in the chain. A parent that is not introduced with <c>super.init(...)</c> gets its zero-argument init first.</summary>
    private void RunInit(ClassDef from, ClassInst obj, Value[] vals, bool[] has, SourceSpan span)
    {
        var owner = from.Chain().FirstOrDefault(x => x.Methods.ContainsKey("init"));
        if (owner is null) return;
        if (!owner.InitCallsSuperInit && owner.Parent is not null)
        {
            var pInit = owner.Parent.Chain().FirstOrDefault(x => x.Methods.ContainsKey("init"));
            var pVals = new Value[pInit?.Methods["init"].Params.Count ?? 0];
            RunInit(owner.Parent, obj, pVals, new bool[pVals.Length], span);
        }
        Call(owner.Methods["init"], vals, has, span, Value.Obj(obj));
    }

    private static List<HeapObj>? FreshArgs(Value[] vals, bool[] has)
    {
        List<HeapObj>? temps = null;
        for (int i = 0; i < vals.Length; i++)
            if (has[i] && vals[i].Kind == VKind.Object && vals[i].AsObj is { Owner: null, Dead: false } o && !IsKeepAlive(o))
                (temps ??= []).Add(o);
        return temps;
    }

    /// <summary>Temporaries passed as arguments that nobody adopted die when the call ends.</summary>
    private void DestroyTemps(List<HeapObj>? temps, Value result, SourceSpan span)
    {
        if (temps is null) return;
        foreach (var o in temps)
            if (o.Owner is null && !o.Dead && !o.Dying && !(result.Kind == VKind.Object && ReferenceEquals(result.Ref, o)))
                Destroy(o, span);
    }

    private Value CallTracked(Closure f, Value[] vals, bool[] has, SourceSpan span, Value? self)
    {
        var temps = FreshArgs(vals, has);
        var r = Call(f, vals, has, span, self);
        DestroyTemps(temps, r, span);
        return r;
    }

    // ---- method dispatch ----

    /// <summary>Own members (a class's chain, nearest first), then extend blocks, then trait defaults.</summary>
    internal Closure? FindUserMethod(Value recv, string name)
    {
        Closure? Defaults(string key)
        {
            if (_extTraits.TryGetValue(key, out var ets))
                foreach (var t in ets)
                    if (_traits.TryGetValue(t, out var td) && td.Defaults.TryGetValue(name, out var d)) return d;
            return null;
        }

        if (recv.Kind == VKind.Object && recv.AsObj is ClassInst ci)
        {
            foreach (var c in ci.Class.Chain())
                if (c.Methods.TryGetValue(name, out var m)) return m;
            foreach (var c in ci.Class.Chain())
                if (_ext.TryGetValue(c.Name, out var em) && em.TryGetValue(name, out var m2)) return m2;
            foreach (var c in ci.Class.Chain())
            {
                foreach (var t in c.TraitNames)
                    if (_traits.TryGetValue(t, out var td) && td.Defaults.TryGetValue(name, out var d)) return d;
                if (Defaults(c.Name) is { } xd) return xd;
            }
            return null;
        }
        var key = recv.TypeName;
        if (_ext.TryGetValue(key, out var e2) && e2.TryGetValue(name, out var m3)) return m3;
        return Defaults(key);
    }

    private Value CallUserMethod(Value recv, string name, Value[] args, SourceSpan span)
    {
        var m = FindUserMethod(recv, name) ?? throw Fail(Templates.Internal(span, $"no method '{name}' on {recv.TypeName}"));
        var has = new bool[args.Length];
        Array.Fill(has, true);
        return Call(m, args, has, span, recv);
    }

    /// <summary>A method call on a class instance (virtual: the object's own class decides), or on a native handle.</summary>
    private Value CallObjectMethod(CallExpr c, MemberExpr m, Value recv, Env env)
    {
        var o = recv.AsObj;
        CheckAlive(o, m.Target);
        if (o is ClassInst ci)
        {
            var f = FindUserMethod(recv, m.Name);
            if (f is null && ci.Fields.TryGetValue(m.Name, out var fv) && fv.Kind == VKind.Func)
            {
                var callee = (Closure)fv.Ref!;
                PrepareArgs(callee, c, env, out var fvals, out var fhas);
                return CallTracked(callee, fvals, fhas, c.Span, null);
            }
            if (f is null) throw Fail(Templates.Internal(c.Span, $"method '{m.Name}' on {ci.Class.Name}"));
            PrepareArgs(f, c, env, out var vals, out var has);
            return CallTracked(f, vals, has, c.Span, recv);
        }
        var args = c.Args.Select(a => Eval(a.Value, env)).ToArray();
        return CallNative(o, m.Name, args, c.Span);
    }

    /// <summary>A user extension method (or trait default) on a built-in value such as a txt or a List.</summary>
    private Value CallExtMethod(CallExpr c, MemberExpr m, Value recv, Env env)
    {
        var f = FindUserMethod(recv, m.Name) ?? throw Fail(Templates.Internal(c.Span, $"method '{m.Name}' on {recv.TypeName}"));
        PrepareArgs(f, c, env, out var vals, out var has);
        return CallTracked(f, vals, has, c.Span, recv);
    }

    private Value CallSuper(CallExpr c, MemberExpr m, Env env)
    {
        var cd = _classStack.Count > 0 ? _classStack[^1] : throw Fail(Templates.Internal(c.Span, "super outside a method"));
        var self = env.Find("self")!.V;
        var parent = cd.Parent ?? throw Fail(Templates.Internal(c.Span, "super without a parent"));
        if (m.Name == "init")
        {
            var owner = parent.Chain().FirstOrDefault(x => x.Methods.ContainsKey("init"));
            Value[] vals = []; bool[] has = [];
            if (owner is not null) PrepareArgs(owner.Methods["init"], c, env, out vals, out has);
            RunInit(parent, (ClassInst)self.AsObj, vals, has, c.Span);
            return Value.Unit;
        }
        Closure? f = null;
        foreach (var level in parent.Chain())
            if (level.Methods.TryGetValue(m.Name, out f)) break;
        f ??= FindUserMethodFrom(parent, m.Name);
        if (f is null) throw Fail(Templates.Internal(c.Span, $"super.{m.Name}"));
        PrepareArgs(f, c, env, out var v2, out var h2);
        return CallTracked(f, v2, h2, c.Span, self);
    }

    private Closure? FindUserMethodFrom(ClassDef start, string name)
    {
        foreach (var c in start.Chain())
        {
            if (_ext.TryGetValue(c.Name, out var em) && em.TryGetValue(name, out var m)) return m;
            foreach (var t in c.TraitNames)
                if (_traits.TryGetValue(t, out var td) && td.Defaults.TryGetValue(name, out var d)) return d;
        }
        return null;
    }

    // ---- fields, operators, indexing ----

    private Value EvalMember(MemberExpr m, Env env)
    {
        var recv = Eval(m.Target, env);
        if (recv.Kind == VKind.Object && recv.AsObj is ClassInst ci)
        {
            CheckAlive(ci, m.Target);
            if (ci.Fields.TryGetValue(m.Name, out var v)) return v;
            throw Fail(Templates.Internal(m.Span, $"field '{m.Name}' of {ci.Class.Name} was read before it was set"));
        }
        throw Fail(Templates.Internal(m.Span, $"member '{m.Name}' on {recv.TypeName}"));
    }

    private void ExecFieldAssign(AssignStmt a, MemberExpr me, Env env)
    {
        var recv = Eval(me.Target, env);
        if (recv.Kind != VKind.Object || recv.AsObj is not ClassInst ci)
            throw Fail(Templates.Internal(a.Span, "assignment to a field of a non-object"));
        CheckAlive(ci, me.Target);
        var rhs = a.Op == TokenKind.Assign ? EvalOwned(a.Value, env) : Eval(a.Value, env);
        ci.Fields.TryGetValue(me.Name, out var cur);
        var next = a.Op == TokenKind.Assign ? rhs
            : Binary(BaseOpOf(a.Op) ?? throw Fail(Templates.Internal(a.Span, $"assignment operator {a.Op}")), cur, rhs, a.Span);
        if (a.Op == TokenKind.Assign && cur.Kind == VKind.Object && ReferenceEquals(cur.AsObj.Owner, ci)
            && !(next.Kind == VKind.Object && ReferenceEquals(next.Ref, cur.Ref)))
            Destroy(cur.AsObj, a.Span);
        ci.Fields[me.Name] = next;
        ClaimField(next, a.Op == TokenKind.Assign ? a.Value : null, env, ci);
    }

    private Value ObjectBinary(TokenKind op, Value a, Value b, SourceSpan span)
    {
        string? sym = op switch
        {
            TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Star => "*", TokenKind.Slash => "/", TokenKind.At => "@",
            TokenKind.EqEq => "==", TokenKind.BangEq => "!=", TokenKind.Lt => "<", TokenKind.Gt => ">", _ => null,
        };
        if (sym is null) throw Fail(Templates.Internal(span, $"operator {op} on {a.TypeName}"));
        var m = FindUserMethod(a, "op " + sym);
        if (m is null && op == TokenKind.BangEq && FindUserMethod(a, "op ==") is { } eq)
            return Value.Bool(!Call(eq, [b], [true], span, a).AsBool);
        if (m is null) throw Fail(Templates.Internal(span, $"operator {sym} on {a.TypeName}"));
        return Call(m, [b], [true], span, a);
    }
}
