using System.Text;
using Splat.Core;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Runtime;

/// <summary>Internal carrier for a runtime diagnostic. Always caught by <see cref="Interpreter.Run"/>; never reaches the user.</summary>
internal sealed class RuntimeError(Diagnostic diagnostic) : Exception
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

internal sealed class Cell(Value value, bool mutable)
{
    public Value V = value;
    public bool Mutable { get; } = mutable;
}

/// <summary>A runtime scope: names to cells, chained to the enclosing scope.</summary>
public sealed class Env(Env? parent = null)
{
    private Dictionary<string, Cell>? _vars;

    internal Cell? Find(string name)
    {
        for (var e = this; e is not null; e = e._parent)
            if (e._vars is not null && e._vars.TryGetValue(name, out var c)) return c;
        return null;
    }

    private readonly Env? _parent = parent;

    internal void Define(string name, Value value, bool mutable) =>
        (_vars ??= new Dictionary<string, Cell>(StringComparer.Ordinal))[name] = new Cell(value, mutable);

    /// <summary>Remembers the current value of every mutable binding in this scope chain, so a failed REPL entry can be undone.</summary>
    internal List<(Cell Cell, Value Value)> SnapshotMutable()
    {
        var list = new List<(Cell, Value)>();
        for (var e = this; e is not null; e = e._parent)
            if (e._vars is not null)
                foreach (var c in e._vars.Values)
                    if (c.Mutable) list.Add((c, c.V));
        return list;
    }

    internal static void Restore(List<(Cell Cell, Value Value)> snapshot)
    {
        foreach (var (cell, value) in snapshot) cell.V = value;
    }

    /// <summary>Every name visible from this scope (its own and its parents'), without duplicates.</summary>
    public IEnumerable<string> AllNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var e = this; e is not null; e = e._parent)
            if (e._vars is not null)
                foreach (var n in e._vars.Keys) seen.Add(n);
        return seen;
    }

    /// <summary>Names bound directly in this scope (not its parents).</summary>
    public IEnumerable<string> Names => _vars?.Keys ?? Enumerable.Empty<string>();
}

internal sealed record ParamInfo(string Name, Expr? Default);

/// <summary>A function value: a <c>fn</c> declaration or a lambda together with the scope it was created in.</summary>
internal sealed class Closure
{
    public required string Name { get; init; }
    public required IReadOnlyList<ParamInfo> Params { get; init; }
    public Expr? ExprBody { get; init; }
    public Block? BlockBody { get; init; }
    public required Env Env { get; init; }
    /// <summary>False when the declared return type is <c>unit</c>: a trailing expression is then discarded.</summary>
    public required bool ReturnsValue { get; init; }
}

/// <summary>
/// The Tier-0 tree-walking interpreter. Expects a program the <see cref="TypeChecker"/> accepted, so operand types are
/// known to agree and only genuinely dynamic failures (division by zero, overflow, panics, depth) are reported here.
/// </summary>
public sealed partial class Interpreter
{
    /// <summary>Nested SPLAT calls allowed before the comedic stack-overflow error.</summary>
    public const int MaxCallDepth = 10_000;
    private const int ThreadStackBytes = 256 * 1024 * 1024;

    private enum Flow { Normal, Break, Continue, Return }

    private readonly TypeInfo _info;
    private DiagnosticEngine _diag;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TextReader _stdin;
    private readonly List<(string Name, SourceSpan CallSite)> _frames = [];
    private Value _ret = Value.Unit;
    // Phase 7: variant name -> enum name, and which variants carry no payload (they are values, not calls).
    private readonly Dictionary<string, string> _variantToEnum = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unitVariants = new(StringComparer.Ordinal);

    public Interpreter(TypeInfo info, DiagnosticEngine diagnostics, TextWriter stdout, TextWriter stderr, TextReader? stdin = null)
    {
        _info = info;
        _diag = diagnostics;
        _stdout = stdout;
        _stderr = stderr;
        _stdin = stdin ?? Console.In;
        Globals = new Env();
        InitBuiltinEnums();
    }

    private void InitBuiltinEnums()
    {
        // Maybe<T>: Got(T) | Nada. Variants live outside the environment so they never show up as user bindings.
        _variantToEnum["Got"] = "Maybe";
        _variantToEnum["Nada"] = "Maybe";
        _unitVariants.Add("Nada");
    }

    public Env Globals { get; }

    /// <summary>
    /// Runs the program on a large-stack thread. Returns false after reporting a runtime error to the diagnostic engine.
    /// </summary>
    public bool Run(Program program) => Execute(program, Globals, _diag, out _);

    /// <summary>
    /// Runs a program in <paramref name="env"/> (a REPL passes a child of the session scope so a failure leaves no
    /// bindings behind) and reports runtime errors to <paramref name="diagnostics"/>. When the last statement is an
    /// expression, its value is returned in <paramref name="last"/>.
    /// </summary>
    public bool Execute(Program program, Env env, DiagnosticEngine diagnostics, out Value? last)
    {
        _diag = diagnostics;
        bool ok = true;
        Value? tail = null;
        var thread = new Thread(() => ok = RunCore(program, env, out tail), ThreadStackBytes);
        thread.Start();
        thread.Join();
        last = ok ? tail : null;
        return ok;
    }

    private bool RunCore(Program program, Env env, out Value? last)
    {
        last = null;
        try
        {
            ExecBlock(program.Statements, env, true, out var tail);
            if (program.Statements.Count > 0 && program.Statements[^1] is ExprStmt) last = tail;
            return true;
        }
        catch (RuntimeError e)
        {
            _diag.Report(e.Diagnostic);
            return false;
        }
        catch (Exception e) when (e is InvalidCastException or NullReferenceException or InvalidOperationException or KeyNotFoundException)
        {
            _diag.Report(Templates.Internal(SourceSpan.None, $"interpreter fault: {e.Message}"));
            return false;
        }
        finally
        {
            _frames.Clear();
        }
    }

    // ---- errors ----

    private IReadOnlyList<string>? Trace()
    {
        if (_frames.Count == 0) return null;
        var lines = new List<string>();
        string Line((string Name, SourceSpan Site) f) => $"{f.Name}  (called at {f.Site})";
        if (_frames.Count <= 14)
        {
            for (int i = _frames.Count - 1; i >= 0; i--) lines.Add(Line(_frames[i]));
        }
        else
        {
            for (int i = _frames.Count - 1; i >= _frames.Count - 8; i--) lines.Add(Line(_frames[i]));
            lines.Add($"... {_frames.Count - 11} more calls ...");
            for (int i = 2; i >= 0; i--) lines.Add(Line(_frames[i]));
        }
        return lines;
    }

    private RuntimeError Fail(Diagnostic d) => new(d with { Trace = d.Trace ?? Trace() });

    // ---- statements ----

    private void Hoist(IReadOnlyList<Stmt> stmts, Env env)
    {
        foreach (var s in stmts)
        {
            if (s is FnDecl d && d.OpSymbol is null && _info.FnType(d) is { } ft)
            {
                var closure = new Closure
                {
                    Name = d.Name,
                    Params = d.Params.Select(p => new ParamInfo(p.Name, p.Default)).ToList(),
                    ExprBody = d.ExprBody,
                    BlockBody = d.BlockBody,
                    Env = env,
                    ReturnsValue = !(ft.Ret is PrimType { Kind: Prim.Unit }),
                };
                env.Define(d.Name, Value.Func(closure), false);
            }
            else if (s is EnumDecl md) HoistEnum(md, env);
        }
    }

    private void HoistEnum(EnumDecl md, Env env)
    {
        foreach (var v in md.Variants)
        {
            _variantToEnum[v.Name] = md.Name;
            if (v.Payloads.Count == 0) _unitVariants.Add(v.Name); else _unitVariants.Remove(v.Name);
        }
    }

    private Flow ExecBlock(IReadOnlyList<Stmt> stmts, Env env, bool wantTail, out Value tail)
    {
        tail = Value.Unit;
        Hoist(stmts, env);
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            if (wantTail && i == stmts.Count - 1)
            {
                if (s is ExprStmt es) { tail = Eval(es.Expression, env); return Flow.Normal; }
                if (s is IfStmt h) return ExecIf(h, env, true, out tail);
            }
            var f = Exec(s, env);
            if (f != Flow.Normal) return f;
        }
        return Flow.Normal;
    }

    private Flow Exec(Stmt s, Env env)
    {
        switch (s)
        {
            case BindStmt b:
                env.Define(b.Name, EvalOwned(b.Value, env), b.IsMutable);
                return Flow.Normal;
            case AssignStmt a:
                ExecAssign(a, env);
                return Flow.Normal;
            case ExprStmt e:
                Eval(e.Expression, env);
                return Flow.Normal;
            case ReturnStmt y:
                _ret = y.Value is null ? Value.Unit : Eval(y.Value, env);
                return Flow.Return;
            case RipStmt r:
            {
                string msg = r.Message is null ? "Unrecoverable panic. No further details; the program lost the will to continue." : Eval(r.Message, env).AsString;
                throw Fail(Templates.Panic(r.Span, msg, Trace()));
            }
            case BreakStmt: return Flow.Break;
            case ContinueStmt: return Flow.Continue;
            case IfStmt h: return ExecIf(h, env, false, out _);
            case LoopStmt sp: return ExecLoop(sp, env);
            case EnumDecl: return Flow.Normal; // already hoisted
            case TupleBindStmt tb: ExecTupleBind(tb, env); return Flow.Normal;
            default: return Flow.Normal; // FnDecl is hoisted; Reserved/Error never reach the interpreter
        }
    }

    private void ExecAssign(AssignStmt a, Env env)
    {
        if (a.Target is IndexExpr ix) { ExecIndexAssign(a, ix, env); return; }
        var name = (NameExpr)a.Target;
        var cell = env.Find(name.Name) ?? throw Fail(Templates.Internal(name.Span, $"unbound variable '{name.Name}'"));
        var rhs = a.Op == TokenKind.Assign ? EvalOwned(a.Value, env) : Eval(a.Value, env);
        cell.V = a.Op switch
        {
            TokenKind.Assign => rhs,
            TokenKind.PlusAssign => Binary(TokenKind.Plus, cell.V, rhs, a.Span),
            TokenKind.MinusAssign => Binary(TokenKind.Minus, cell.V, rhs, a.Span),
            TokenKind.StarAssign => Binary(TokenKind.Star, cell.V, rhs, a.Span),
            TokenKind.SlashAssign => Binary(TokenKind.Slash, cell.V, rhs, a.Span),
            _ => throw Fail(Templates.Internal(a.Span, $"assignment operator {a.Op}")),
        };
    }

    private Flow ExecIf(IfStmt h, Env env, bool wantTail, out Value tail)
    {
        foreach (var br in h.Branches)
            if (Eval(br.Condition, env).AsBool)
                return ExecBlock(br.Body.Statements, new Env(env), wantTail, out tail);
        if (h.Else is not null) return ExecBlock(h.Else.Statements, new Env(env), wantTail, out tail);
        tail = Value.Unit;
        return Flow.Normal;
    }

    private Flow ExecLoop(LoopStmt sp, Env env)
    {
        switch (sp.Kind)
        {
            case LoopKind.Forever:
                while (true)
                {
                    var f = ExecBlock(sp.Body.Statements, new Env(env), false, out _);
                    if (f == Flow.Break) return Flow.Normal;
                    if (f == Flow.Return) return f;
                }
            case LoopKind.While:
                while (Eval(sp.Subject!, env).AsBool)
                {
                    var f = ExecBlock(sp.Body.Statements, new Env(env), false, out _);
                    if (f == Flow.Break) break;
                    if (f == Flow.Return) return f;
                }
                return Flow.Normal;
            default:
            {
                foreach (var item in Iterate(Eval(sp.Subject!, env)))
                {
                    var iter = new Env(env);
                    iter.Define(sp.Variable!, item, false);
                    var f = ExecBlock(sp.Body.Statements, iter, false, out _);
                    if (f == Flow.Break) break;
                    if (f == Flow.Return) return f;
                }
                return Flow.Normal;
            }
        }
    }

    /// <summary>
    /// The one notion of "iterable" at runtime: txt→chars, range→ints, chain→parts, List/Set/Map/Tuple.
    /// </summary>
    private static IEnumerable<Value> Iterate(Value v)
    {
        switch (v.Kind)
        {
            case VKind.Txt:
                foreach (var rune in v.AsString.EnumerateRunes()) yield return Value.Char(rune.Value);
                break;
            case VKind.Range:
                foreach (var x in v.AsRange.Items()) yield return x;
                break;
            case VKind.Chain:
                foreach (var part in v.AsChain)
                    foreach (var x in Iterate(part)) yield return x;
                break;
            case VKind.List:
                foreach (var item in v.AsList.Items.ToArray()) yield return item;
                break;
            case VKind.Set:
                foreach (var item in v.AsSet.Items.ToArray()) yield return item;
                break;
            case VKind.Map:
                foreach (var (k, val) in v.AsMap.Items.ToArray())
                    yield return Value.Tuple([k, val]);
                break;
            case VKind.Tuple:
                foreach (var item in v.AsTuple) yield return item;
                break;
        }
    }

    /// <summary>`x in S` for ranges, txt, chains, lists, sets and maps.</summary>
    private static bool Contains(Value set, Value x)
    {
        switch (set.Kind)
        {
            case VKind.Txt:
                if (x.Kind == VKind.Char) return set.AsString.EnumerateRunes().Any(r => r.Value == x.Bits);
                return set.AsString.Contains(x.AsString, StringComparison.Ordinal);
            case VKind.Range:
                return set.AsRange.Contains(x.AsInt128);
            case VKind.Chain:
                return set.AsChain.Any(part => Contains(part, x));
            case VKind.List:
                return set.AsList.Items.Any(item => ValueKeyComparer.Instance.Equals(item, x));
            case VKind.Set:
                return set.AsSet.Contains(x);
            case VKind.Map:
                return set.AsMap.ContainsKey(x);
            default:
                return false;
        }
    }

    // ---- expressions ----

    private Value Eval(Expr e, Env env)
    {
        switch (e)
        {
            case IntLiteral i:
                return Value.Int(LiteralKind(e), unchecked((long)i.Value));
            case FloatLiteral f:
                return Value.Float(LiteralKind(e), f.Value);
            case CharLiteral c: return Value.Char(c.Value);
            case BoolLiteral b: return Value.Bool(b.Value);
            case UnitLiteral: return Value.Unit;
            case StringTemplate s: return EvalTemplate(s, env);
            case NameExpr n:
            {
                var cell = env.Find(n.Name);
                if (cell is not null) return cell.V;
                if (_unitVariants.Contains(n.Name)) return Value.Enum(new EnumValue(_variantToEnum[n.Name], n.Name, []));
                throw Fail(Templates.Internal(n.Span, $"unbound name '{n.Name}'"));
            }
            case UnaryExpr u: return EvalUnary(u, env);
            case BinaryExpr b: return EvalBinary(b, env);
            case CallExpr c: return EvalCall(c, env);
            case StepExpr st: return EvalStep(st, env);
            case ChainExpr ch: return Value.Chain(ch.Parts.Select(p => Eval(p, env)).ToArray());
            case LambdaExpr l:
            {
                var ft = _info.TypeOf(l) as FuncSType;
                return Value.Func(new Closure
                {
                    Name = "<lambda>",
                    Params = l.Params.Select(p => new ParamInfo(p.Name, null)).ToList(),
                    ExprBody = l.Body,
                    Env = env,
                    ReturnsValue = !(ft?.Ret is PrimType { Kind: Prim.Unit }),
                });
            }
            case ListLiteral ll:
            {
                var list = new SplatList();
                foreach (var item in ll.Items) list.Items.Add(EvalOwned(item, env));
                return Value.List(list);
            }
            case TupleExpr te:
                return Value.Tuple(te.Items.Select(i => EvalOwned(i, env)).ToArray());
            case PickExpr pk:
                return EvalPick(pk, env);
            case IndexExpr ix:
                return EvalIndex(ix, env);
            default:
                throw Fail(Templates.Internal(e.Span, $"cannot evaluate {e.GetType().Name} yet"));
        }
    }

    private VKind LiteralKind(Expr e) => Kinds.Of(_info.TypeOf(e) ?? Types.I32);

    private Value EvalTemplate(StringTemplate s, Env env)
    {
        if (s.Parts.Count == 1 && s.Parts[0] is TemplateText only) return Value.Str(only.Value);
        var sb = new StringBuilder();
        foreach (var part in s.Parts)
        {
            switch (part)
            {
                case TemplateText t: sb.Append(t.Value); break;
                case TemplateExpr x:
                {
                    var v = Eval(x.Value, env);
                    if (string.IsNullOrEmpty(x.Format)) sb.Append(Formatter.Display(v));
                    else
                    {
                        FormatSpec.TryParse(x.Format, out var spec, out _);
                        sb.Append(Formatter.Format(v, spec));
                    }
                    break;
                }
            }
        }
        return Value.Str(sb.ToString());
    }

    private Value EvalUnary(UnaryExpr u, Env env)
    {
        if (u.Op == TokenKind.Bang) return Value.Bool(!Eval(u.Operand, env).AsBool);

        // `-128` for an i8: the literal 128 alone would not fit, so negate while building it.
        if (u.Operand is IntLiteral lit)
        {
            var kind = LiteralKind(lit);
            return Value.FromInt128(kind, -(Int128)lit.Value);
        }

        var v = Eval(u.Operand, env);
        if (v.IsFloat) return Value.Float(v.Kind, -v.AsDouble);
        var r = -v.AsInt128;
        return CheckedInt(v.Kind, r, "-", u.Span);
    }

    private Value EvalBinary(BinaryExpr b, Env env)
    {
        switch (b.Op)
        {
            case TokenKind.And:
                return Eval(b.Left, env).AsBool ? Eval(b.Right, env) : Value.Bool(false);
            case TokenKind.Or:
                return Eval(b.Left, env).AsBool ? Value.Bool(true) : Eval(b.Right, env);
            case TokenKind.PipeGreater:
                return Eval(_info.PipeCall(b) ?? throw Fail(Templates.Internal(b.Span, "unchecked pipe")), env);
            case TokenKind.DotDot or TokenKind.DotDotEq:
                return Value.Range(Eval(b.Left, env), Eval(b.Right, env), b.Op == TokenKind.DotDotEq);
            case TokenKind.In:
            {
                var x = Eval(b.Left, env);
                if (b.Right is ParenListExpr list)
                {
                    // Sugar for `x == a or x == b or ...`: stops at the first match.
                    foreach (var item in list.Items)
                        if (Binary(TokenKind.EqEq, x, Eval(item, env), item.Span).AsBool) return Value.Bool(true);
                    return Value.Bool(false);
                }
                return Value.Bool(Contains(Eval(b.Right, env), x));
            }
            default:
                return Binary(b.Op, Eval(b.Left, env), Eval(b.Right, env), b.Span);
        }
    }

    private Value EvalStep(StepExpr s, Env env)
    {
        var range = Eval(s.Range, env).AsRange;
        var step = Eval(s.Step, env);
        if (step.AsInt128 <= 0) throw Fail(Templates.BadStep(s.Step.Span, Formatter.Display(step)));
        return Value.Range(range with { Step = step.AsInt128 });
    }

    private Value EvalCall(CallExpr c, Env env)
    {
        if (c.Callee is MemberExpr mexpr) return CallMethod(c, mexpr, env);
        if (c.Callee is NameExpr n && env.Find(n.Name) is null)
        {
            if (!Prelude.IsBuiltin(n.Name) && Types.ByName.TryGetValue(n.Name, out var target))
                return Convert(Eval(c.Args[0].Value, env), target, c.Span);
            if (n.Name is "print" or "eprint")
            {
                var text = string.Join(" ", c.Args.Select(a => Formatter.Display(Eval(a.Value, env)))) + "\n";
                (n.Name == "print" ? _stdout : _stderr).Write(text);
                return Value.Unit;
            }
            if (Prelude.IsBuiltin(n.Name)) return CallBuiltin(n.Name, c, env);
            // Phase 7: enum payload variant constructors
            if (_variantToEnum.TryGetValue(n.Name, out var moodName))
            {
                var ctorArgs = c.Args.Select(a => Eval(a.Value, env)).ToArray();
                return Value.Enum(new EnumValue(moodName, n.Name, ctorArgs));
            }
        }

        var fv = Eval(c.Callee, env);
        var closure = (Closure)fv.Ref!;
        var map = _info.ArgMap(c);
        var vals = new Value[closure.Params.Count];
        var has = new bool[closure.Params.Count];
        for (int i = 0; i < c.Args.Count; i++)
        {
            int target = map is null ? i : map[i];
            vals[target] = Eval(c.Args[i].Value, env);
            has[target] = true;
        }
        return Call(closure, vals, has, c.Span);
    }

    // ---- prelude ----

    private Value CallBuiltin(string name, CallExpr c, Env env)
    {
        // set(...) and map(k => v, ...) build their own elements (a pair is not a value on its own).
        if (name == "set") return MakeSet(c, env);
        if (name == "map") return MakeMap(c, env);
        var args = c.Args.Select(a => Eval(a.Value, env)).ToArray();
        switch (name)
        {
            case "readl":
            {
                if (args.Length == 1)
                {
                    _stdout.Write(args[0].AsString);
                    _stdout.Flush();
                }
                // End of input reads as an empty line (readl stays a plain txt, as in the spec's examples).
                return Value.Str(_stdin.ReadLine() ?? "");
            }
            case "rev":
                if (args[0].Kind == VKind.List)
                {
                    var rev = new SplatList();
                    for (int i = args[0].AsList.Items.Count - 1; i >= 0; i--)
                        rev.Items.Add(args[0].AsList.Items[i]);
                    return Value.List(rev);
                }
                return Value.Range(args[0].AsRange with { Reversed = !args[0].AsRange.Reversed });
            case "len":
                return args[0].Kind switch
                {
                    VKind.Txt => Value.Int(VKind.Usize, args[0].AsString.EnumerateRunes().Count()),
                    VKind.List => Value.Int(VKind.Usize, args[0].AsList.Items.Count),
                    VKind.Set => Value.Int(VKind.Usize, args[0].AsSet.Count),
                    VKind.Map => Value.Int(VKind.Usize, args[0].AsMap.Count),
                    _ => throw Fail(Templates.Internal(c.Span, $"len: unexpected type {args[0].TypeName}")),
                };
            case "split":
            {
                var parts = args[0].AsString.Split(args[1].AsString, StringSplitOptions.None);
                var list = new SplatList();
                foreach (var p in parts) list.Items.Add(Value.Str(p));
                return Value.List(list);
            }
            case "lines":
            {
                var lineList = new SplatList();
                foreach (var line in args[0].AsString.Split('\n'))
                    lineList.Items.Add(Value.Str(line.TrimEnd('\r')));
                return Value.List(lineList);
            }
            case "enumerate":
            {
                var eList = new SplatList();
                long idx = 0;
                foreach (var item in Iterate(args[0]))
                    eList.Items.Add(Value.Tuple([Value.Int(VKind.Usize, idx++), item]));
                return Value.List(eList);
            }
            case "zip":
            {
                var zipList = new SplatList();
                using var e1 = Iterate(args[0]).GetEnumerator();
                using var e2 = Iterate(args[1]).GetEnumerator();
                while (e1.MoveNext() && e2.MoveNext())
                    zipList.Items.Add(Value.Tuple([e1.Current, e2.Current]));
                return Value.List(zipList);
            }
            case "upper": return Value.Str(args[0].AsString.ToUpperInvariant());
            case "lower": return Value.Str(args[0].AsString.ToLowerInvariant());
            case "trim": return Value.Str(args[0].AsString.Trim());
            case "parse_i32": return ParseInt(args[0].AsString, VKind.I32);
            case "parse_i64": return ParseInt(args[0].AsString, VKind.I64);
            case "parse_mid": return ParseFloat(args[0].AsString, VKind.F32);
            case "parse_chonk": return ParseFloat(args[0].AsString, VKind.F64);
            default:
                throw Fail(Templates.Internal(c.Span, $"prelude function '{name}'"));
        }
    }

    /// <summary>The parse functions return <c>Maybe</c>: <c>Got(n)</c>, or <c>Nada</c> for text that is not a number of that type.</summary>
    private static Value ParseInt(string text, VKind kind)
    {
        if (Int128.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n >= Types.Min(Kinds.P(kind)) && n <= Types.Max(Kinds.P(kind)))
            return Got(Value.FromInt128(kind, n));
        return Nada();
    }

    private static Value ParseFloat(string text, VKind kind)
    {
        if (double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d))
            return Got(Value.Float(kind, d));
        return Nada();
    }

    private Value Call(Closure f, Value[] vals, bool[] has, SourceSpan callSite)
    {
        if (_frames.Count >= MaxCallDepth)
            throw Fail(Templates.StackOverflow(callSite, _frames.Count));

        var env = new Env(f.Env);
        for (int i = 0; i < f.Params.Count; i++)
        {
            var p = f.Params[i];
            env.Define(p.Name, has[i] ? vals[i] : Eval(p.Default!, f.Env), false);
        }

        _frames.Add((f.Name, callSite));
        try
        {
            if (f.ExprBody is not null)
            {
                var r = Eval(f.ExprBody, env);
                return f.ReturnsValue ? r : Value.Unit;
            }
            var flow = ExecBlock(f.BlockBody!.Statements, env, f.ReturnsValue, out var tail);
            if (!f.ReturnsValue) return Value.Unit;
            return flow == Flow.Return ? _ret : tail;
        }
        finally
        {
            _frames.RemoveAt(_frames.Count - 1);
        }
    }

    // ---- Phase 7 helpers ----

    private void ExecTupleBind(TupleBindStmt tb, Env env)
    {
        var v = Eval(tb.Value, env);
        var items = v.AsTuple;
        for (int i = 0; i < tb.Names.Count; i++)
            env.Define(tb.Names[i], i < items.Length ? items[i] : Value.Unit, tb.IsMutable);
    }

    private Value EvalPick(PickExpr pk, Env env)
    {
        var subject = Eval(pk.Subject, env);
        foreach (var arm in pk.Arms)
        {
            var bindings = new Dictionary<string, Value>(StringComparer.Ordinal);
            if (MatchPattern(arm.Pat, subject, env, bindings))
            {
                var armEnv = new Env(env);
                foreach (var (n, v) in bindings) armEnv.Define(n, v, false);
                if (arm.ExprBody is not null) return Eval(arm.ExprBody, armEnv);
                ExecBlock(arm.BlockBody!.Statements, armEnv, false, out _);
                return Value.Unit;
            }
        }
        throw Fail(Templates.Internal(pk.Span, "pick: no arm matched (exhaustiveness check missed this)"));
    }

    private bool MatchPattern(Pattern p, Value v, Env env, Dictionary<string, Value> bindings)
    {
        switch (p)
        {
            case WildcardPat:
                return true;
            case BindPat b:
                bindings[b.Name] = v;
                return true;
            case LiteralPat lp:
                var litVal = Eval(lp.Value, env);
                return ValueKeyComparer.Instance.Equals(v, litVal);
            case RangePat rp:
            {
                if (!v.IsInt && v.Kind != VKind.Char) return false; // chars range over their code points
                var n = v.AsInt128;
                var lo = Eval(rp.Lo, env).AsInt128;
                var hi = Eval(rp.Hi, env).AsInt128;
                return rp.Inclusive ? n >= lo && n <= hi : n >= lo && n < hi;
            }
            case VariantPat vp:
                if (v.Kind != VKind.Enum) return false;
                var ev = v.AsEnum;
                if (ev.VariantName != vp.VariantName) return false;
                for (int i = 0; i < vp.Bindings.Count && i < ev.Payloads.Length; i++)
                    if (!MatchPattern(vp.Bindings[i], ev.Payloads[i], env, bindings)) return false;
                return true;
            case TuplePat tp:
                if (v.Kind != VKind.Tuple) return false;
                var tuple = v.AsTuple;
                if (tuple.Length != tp.Items.Count) return false;
                for (int i = 0; i < tp.Items.Count; i++)
                    if (!MatchPattern(tp.Items[i], tuple[i], env, bindings)) return false;
                return true;
            default:
                return false;
        }
    }
}
