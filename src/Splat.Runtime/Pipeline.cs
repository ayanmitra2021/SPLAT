using Splat.Core;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Runtime;

public enum RunOutcome { Ok, CompileError, RuntimeError }

/// <summary>Lex, parse, type-check, then run: the whole Milestone 1 pipeline for a source file.</summary>
public static class Pipeline
{
    /// <summary>Parses and type-checks. Returns false (with diagnostics reported) when the program is not runnable.</summary>
    public static bool Compile(SourceText source, DiagnosticEngine diag, out Program program, out TypeInfo info)
    {
        diag.AddSource(source);
        program = Parser.ParseProgram(source, diag);
        info = new TypeInfo();
        if (diag.HasErrors) return false; // no point type-checking a broken parse
        info = TypeChecker.Check(program, diag);
        return !diag.HasErrors;
    }

    /// <summary>Checks only (diagnostics, no execution).</summary>
    public static bool Check(SourceText source, DiagnosticEngine diag) => Compile(source, diag, out _, out _);

    public static RunOutcome Run(SourceText source, DiagnosticEngine diag, TextWriter stdout, TextWriter stderr)
    {
        if (!Compile(source, diag, out var program, out var info)) return RunOutcome.CompileError;
        var interpreter = new Interpreter(info, diag, stdout, stderr);
        return interpreter.Run(program) ? RunOutcome.Ok : RunOutcome.RuntimeError;
    }
}
