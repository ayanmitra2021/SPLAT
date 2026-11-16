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
}

internal sealed record ParamInfo(string Name, Expr? Default);

/// <summary>A function value: a <c>cook</c> declaration or a lambda together with the scope it was created in.</summary>
internal sealed class Closure
{
    public required string Name { get; init; }
    public required IReadOnlyList<ParamInfo> Params { get; init; }
    public Expr? ExprBody { get; init; }
    public Block? BlockBody { get; init; }
    public required Env Env { get; init; }
    /// <summary>False when the declared return type is <c>zip</c>: a trailing expression is then discarded.</summary>
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
    private readonly DiagnosticEngine _diag;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly List<(string Name, SourceSpan CallSite)> _frames = [];
    private Value _ret = Value.Unit;

    public Interpreter(TypeInfo info, DiagnosticEngine diagnostics, TextWriter stdout, TextWriter stderr)
    {
        _info = info;
        _diag = diagnostics;
        _stdout = stdout;
        _stderr = stderr;
        Globals = new Env();
    }

    public Env Globals { get; }

    /// <summary>
    /// Runs the program on a large-stack thread. Returns false after reporting a runtime error to the diagnostic engine.
    /// </summary>
    public bool Run(Program program)
    {
        bool ok = true;
        var thread = new Thread(() => ok = RunCore(program), ThreadStackBytes);
        thread.Start();
        thread.Join();
        return ok;
    }

    private bool RunCore(Program program)
    {
        try
        {
            ExecBlock(program.Statements, Globals, false, out _);
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
            if (s is CookDecl d && d.OpSymbol is null && _info.CookType(d) is { } ft)
            {
                var closure = new Closure
                {
                    Name = d.Name,
                    Params = d.Params.Select(p => new ParamInfo(p.Name, p.Default)).ToList(),
                    ExprBody = d.ExprBody,
                    BlockBody = d.BlockBody,
                    Env = env,
                    ReturnsValue = !(ft.Ret is PrimType { Kind: Prim.Zip }),
                };
                env.Define(d.Name, Value.Func(closure), false);
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
                if (s is HmmStmt h) return ExecHmm(h, env, true, out tail);
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
                env.Define(b.Name, Eval(b.Value, env), b.IsMutable);
                return Flow.Normal;
            case AssignStmt a:
                ExecAssign(a, env);
                return Flow.Normal;
            case ExprStmt e:
                Eval(e.Expression, env);
                return Flow.Normal;
            case YeetStmt y:
                _ret = y.Value is null ? Value.Unit : Eval(y.Value, env);
                return Flow.Return;
            case RipStmt r:
            {
                string msg = r.Message is null ? "Unrecoverable panic. No further details; the program lost the will to continue." : Eval(r.Message, env).AsString;
                throw Fail(Templates.Panic(r.Span, msg, Trace()));
            }
            case BailStmt: return Flow.Break;
            case SkipStmt: return Flow.Continue;
            case HmmStmt h: return ExecHmm(h, env, false, out _);
            case SpinStmt sp: return ExecSpin(sp, env);
            default: return Flow.Normal; // CookDecl is hoisted; Reserved/Error never reach the interpreter
        }
    }

    private void ExecAssign(AssignStmt a, Env env)
    {
        var name = (NameExpr)a.Target;
        var cell = env.Find(name.Name) ?? throw Fail(Templates.Internal(name.Span, $"unbound variable '{name.Name}'"));
        var rhs = Eval(a.Value, env);
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

    private Flow ExecHmm(HmmStmt h, Env env, bool wantTail, out Value tail)
    {
        foreach (var br in h.Branches)
            if (Eval(br.Condition, env).AsBool)
                return ExecBlock(br.Body.Statements, new Env(env), wantTail, out tail);
        if (h.Else is not null) return ExecBlock(h.Else.Statements, new Env(env), wantTail, out tail);
        tail = Value.Unit;
        return Flow.Normal;
    }

    private Flow ExecSpin(SpinStmt sp, Env env)
    {
        switch (sp.Kind)
        {
            case SpinKind.Forever:
                while (true)
                {
                    var f = ExecBlock(sp.Body.Statements, new Env(env), false, out _);
                    if (f == Flow.Break) return Flow.Normal;
                    if (f == Flow.Return) return f;
                }
            case SpinKind.While:
                while (Eval(sp.Subject!, env).AsBool)
                {
                    var f = ExecBlock(sp.Body.Statements, new Env(env), false, out _);
                    if (f == Flow.Break) break;
                    if (f == Flow.Return) return f;
                }
                return Flow.Normal;
            default:
            {
                var subject = Eval(sp.Subject!, env);
                if (subject.Kind == VKind.Txt)
                {
                    foreach (var rune in subject.AsString.EnumerateRunes())
                    {
                        var citer = new Env(env);
                        citer.Define(sp.Variable!, Value.Char(rune.Value), false);
                        var cf = ExecBlock(sp.Body.Statements, citer, false, out _);
                        if (cf == Flow.Break) break;
                        if (cf == Flow.Return) return cf;
                    }
                    return Flow.Normal;
                }
                var range = subject.AsRange;
                var kind = range.Lo.Kind;
                Int128 i = range.Lo.AsInt128;
                Int128 end = range.Hi.AsInt128 - (range.Inclusive ? 0 : 1);
                for (; i <= end; i++)
                {
                    var iter = new Env(env);
                    iter.Define(sp.Variable!, Value.FromInt128(kind, i), false);
                    var f = ExecBlock(sp.Body.Statements, iter, false, out _);
                    if (f == Flow.Break) break;
                    if (f == Flow.Return) return f;
                }
                return Flow.Normal;
            }
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
                var cell = env.Find(n.Name) ?? throw Fail(Templates.Internal(n.Span, $"unbound name '{n.Name}'"));
                return cell.V;
            }
            case UnaryExpr u: return EvalUnary(u, env);
            case BinaryExpr b: return EvalBinary(b, env);
            case CallExpr c: return EvalCall(c, env);
            case LambdaExpr l:
            {
                var ft = _info.TypeOf(l) as FuncSType;
                return Value.Func(new Closure
                {
                    Name = "<lambda>",
                    Params = l.Params.Select(p => new ParamInfo(p.Name, null)).ToList(),
                    ExprBody = l.Body,
                    Env = env,
                    ReturnsValue = !(ft?.Ret is PrimType { Kind: Prim.Zip }),
                });
            }
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
            default:
                return Binary(b.Op, Eval(b.Left, env), Eval(b.Right, env), b.Span);
        }
    }

    private Value EvalCall(CallExpr c, Env env)
    {
        if (c.Callee is NameExpr n && env.Find(n.Name) is null)
        {
            if (Types.ByName.TryGetValue(n.Name, out var target))
                return Convert(Eval(c.Args[0].Value, env), target, c.Span);
            if (n.Name is "squish" or "yell")
            {
                var text = string.Join(" ", c.Args.Select(a => Formatter.Display(Eval(a.Value, env)))) + "\n";
                (n.Name == "squish" ? _stdout : _stderr).Write(text);
                return Value.Unit;
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
}
