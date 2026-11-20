using Splat.Core;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Runtime;

/// <summary>What one REPL entry did. <see cref="Echo"/> is the <c>=> value :: type</c> line for a non-<c>unit</c> expression statement.</summary>
public sealed record EvalResult(RunOutcome Outcome, DiagnosticEngine Diagnostics, string? Echo);

/// <summary>
/// The live state of a REPL: top-level bindings and functions (for the checker and for the interpreter) that persist from
/// one entry to the next.
/// <para>
/// Entries are transactional. Each runs against a fresh scope chained onto the previous committed entry; only when it
/// parses, type-checks AND runs without error does that scope become the new tip of the chain. A failed entry leaves no
/// new bindings behind and restores the values of session <c>var</c> variables it had already assigned. (State captured
/// inside a closure's own scope is not rolled back.)
/// </para>
/// Redefining a top-level name in a later entry is allowed and shadows it for new code. Functions defined earlier keep
/// the definitions they were checked against, because they only see the part of the chain that existed when they were
/// defined. (A long session therefore has a long scope chain; use ':reset' to flatten it.)
/// </summary>
public sealed class SessionState
{
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private TypeInfo _info = new();
    private Scope _scope = new();
    private EnumRegistry _moods = EnumRegistry.Builtin;
    private Interpreter _interpreter;
    private Env _env;

    public SessionState(TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _interpreter = NewInterpreter();
        _env = _interpreter.Globals;
    }

    private Interpreter NewInterpreter() => new(_info, new DiagnosticEngine(), _stdout, _stderr, _stdin);

    /// <summary>Names bound at the top level of the session (variables and functions), sorted.</summary>
    public IReadOnlyList<string> Names => _env.AllNames().Order(StringComparer.Ordinal).ToList();

    /// <summary>Ends the session: destroys (deinit, close) whatever the session's variables still own.</summary>
    public void Close()
    {
        _interpreter.CloseSession(_env);
        _interpreter.StopServers();
    }

    /// <summary>Forgets every binding and function.</summary>
    public void Reset()
    {
        _interpreter.CloseSession(_env);
        _info = new TypeInfo();
        _scope = new Scope();
        _moods = EnumRegistry.Builtin;
        _interpreter = NewInterpreter();
        _env = _interpreter.Globals;
    }

    /// <summary>Runs one entry (lex, parse, check, run) against the session. Never throws.</summary>
    public EvalResult Eval(string text, string sourceName = "<repl>", bool echo = true)
    {
        var diag = new DiagnosticEngine();
        var source = new SourceText(sourceName, text);
        diag.AddSource(source);

        var program = Parser.ParseProgram(source, diag);
        if (diag.HasErrors) return new EvalResult(RunOutcome.CompileError, diag, null);

        var entryScope = new Scope(_scope);
        var entryEnums = new EnumRegistry(_moods);
        new TypeChecker(diag, entryScope, _info, entryEnums).CheckProgram(program);
        if (diag.HasErrors) return new EvalResult(RunOutcome.CompileError, diag, null);

        var entryEnv = new Env(_env);
        var snapshot = _env.SnapshotMutable();
        if (!_interpreter.Execute(program, entryEnv, diag, out var last, keepScope: true))
        {
            Env.Restore(snapshot);
            return new EvalResult(RunOutcome.RuntimeError, diag, null);
        }

        // Success: this entry's scopes become the tip that the next entry chains onto.
        _scope = entryScope;
        _moods = entryEnums;
        _env = entryEnv;

        string? echoText = null;
        if (echo && last is { } v && program.Statements[^1] is ExprStmt es
            && _info.TypeOf(es.Expression) is { } type && !type.Equals(Types.Unit))
            echoText = $"=> {Formatter.Repr(v)} :: {type.Name}";
        return new EvalResult(RunOutcome.Ok, diag, echoText);
    }
}
