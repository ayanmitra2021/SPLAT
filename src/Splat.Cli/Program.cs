using System.Text;
using Splat.Core;
using Splat.Runtime;
using Splat.Syntax;

namespace Splat.Cli;

public static class Program
{
    public const string Version = "0.3.0-phase11";

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        // --locale <tag> pins how dates print (also SPLAT_LOCALE); it may appear anywhere before the command's file.
        int li = Array.IndexOf(args, "--locale");
        if (li >= 0)
        {
            if (li + 1 >= args.Length || !Temporal.Locale.TryFromTag(args[li + 1], out var loc))
            {
                Console.Error.WriteLine("Usage: --locale <tag>, for example --locale en-US or --locale en-GB");
                return 64;
            }
            Temporal.Locale.Default = loc;
            args = args.Where((_, i) => i != li && i != li + 1).ToArray();
        }

        if (args.Length == 1 && args[0] is "--version" or "-v")
        {
            Console.WriteLine($"🍥 SPLAT {Version} - Strict, Portable, Linear-algebraic & AI Terse-language");
            return 0;
        }

        switch (args.Length == 0 ? "repl" : args[0])
        {
            case "repl" when args.Length <= 1:
                return RunRepl();
            case "repl":
                Console.Error.WriteLine("Usage: splat [repl]");
                return 64;
            case "run" when args.Length == 2:
                return RunFile(args[1], checkOnly: false);
            case "run":
                Console.Error.WriteLine("Usage: splat run <file.splat>");
                return 64;
            case "check" when args.Length == 2:
                return RunFile(args[1], checkOnly: true);
            case "check":
                Console.Error.WriteLine("Usage: splat check <file.splat>");
                return 64;
            case "tokens" when args.Length == 2:
                return DumpTokens(args[1]);
            case "tokens":
                Console.Error.WriteLine("Usage: splat tokens <file.splat>");
                return 64;
            case "ast" when args.Length == 2:
                return DumpAst(args[1], formatSource: false);
            case "ast" when args.Length == 3 && args[1] == "--fmt":
                return DumpAst(args[2], formatSource: true);
            case "ast" when args.Length == 3 && args[1] == "-e":
                return DumpExpression(args[2]);
            case "ast":
                Console.Error.WriteLine("Usage: splat ast [--fmt] <file.splat>  |  splat ast -e \"<expression>\"");
                return 64;
            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'. See build_plan.md for what exists so far.");
                return 64;
        }
    }

    /// <summary>The interactive session. Prompts and the banner show only on a real terminal, so piped input gives clean output.</summary>
    private static int RunRepl()
    {
        bool interactive = !Console.IsInputRedirected;
        bool color = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        return new Repl(Console.In, Console.Out, Console.Error, interactive, color).Run();
    }

    /// <summary>Exit codes: 0 ok, 64 usage, 65 compile error, 66 file not found, 70 runtime error.</summary>
    private static int RunFile(string path, bool checkOnly)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var d = new DiagnosticEngine();
            d.Report(Templates.FileNotFound(SourceSpan.None, path));
            ConsolePrinter.ForStdErr().Print(d);
            return 66;
        }

        var diag = new DiagnosticEngine();
        var source = new SourceText(path, text);
        var outcome = checkOnly
            ? (Pipeline.Check(source, diag) ? RunOutcome.Ok : RunOutcome.CompileError)
            : Pipeline.Run(source, diag, Console.Out, Console.Error, Console.In);

        // A program that started a server keeps serving until Ctrl+C (or until the server is stopped).
        if (outcome == RunOutcome.Ok && ServerRegistry.AnyRunning)
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; ServerRegistry.StopAll(); };
            Console.Error.WriteLine($"Serving on port(s) {string.Join(", ", ServerRegistry.RunningPorts)}. Press Ctrl+C to stop.");
            ServerRegistry.WaitForAll();
        }

        // Warnings (an unreachable pick arm, say) are shown even when the program is fine.
        if (outcome != RunOutcome.Ok || diag.Diagnostics.Count > 0) ConsolePrinter.ForStdErr().Print(diag);
        return outcome switch { RunOutcome.Ok => 0, RunOutcome.CompileError => 65, _ => 70 };
    }

    private static int DumpTokens(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var d = new DiagnosticEngine();
            d.Report(Templates.FileNotFound(SourceSpan.None, path));
            ConsolePrinter.ForStdErr().Print(d);
            return 66;
        }

        var diag = new DiagnosticEngine();
        var source = new SourceText(path, text);
        diag.AddSource(source);

        foreach (var t in new Lexer(source, diag).Tokenize())
            Console.WriteLine(Format(t));

        if (!diag.HasErrors) return 0;
        ConsolePrinter.ForStdErr().Print(diag);
        return 65;
    }

    private static int DumpAst(string path, bool formatSource)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var d = new DiagnosticEngine();
            d.Report(Templates.FileNotFound(SourceSpan.None, path));
            ConsolePrinter.ForStdErr().Print(d);
            return 66;
        }

        var diag = new DiagnosticEngine();
        var source = new SourceText(path, text);
        diag.AddSource(source);

        var program = Parser.ParseProgram(source, diag);
        Console.Out.Write(formatSource ? SourceFormatter.Format(program) : AstPrinter.ProgramText(program));

        if (!diag.HasErrors) return 0;
        ConsolePrinter.ForStdErr().Print(diag);
        return 65;
    }

    /// <summary>Parses one expression from the command line and prints its s-expression on a single line.</summary>
    private static int DumpExpression(string text)
    {
        var diag = new DiagnosticEngine();
        var source = new SourceText("<expr>", text);
        diag.AddSource(source);

        var expr = Parser.ParseExpression(source, diag);
        if (!diag.HasErrors)
        {
            Console.WriteLine(AstPrinter.Expr(expr).Flat());
            return 0;
        }
        ConsolePrinter.ForStdErr().Print(diag);
        return 65;
    }

    private static string Format(Token t)
    {
        var sb = new StringBuilder();
        sb.Append($"{t.Span.Line}:{t.Span.Col}".PadRight(8)).Append(t.Kind.ToString().PadRight(15));
        if (t.Kind != TokenKind.EndOfFile) sb.Append(t.Text.Replace("\n", "\\n"));
        if (t.Kind is TokenKind.Int or TokenKind.Float or TokenKind.Char) sb.Append($"  => {t.Value}");
        if (t.Parts is not null)
        {
            foreach (var p in t.Parts)
            {
                sb.Append(p switch
                {
                    TextPart tp => $"  [text {tp.Value.Replace("\n", "\\n")}]",
                    ExprPart ep => $"  [expr {ep.Source}{(ep.Format is null ? "" : " fmt " + ep.Format)}]",
                    _ => "",
                });
            }
        }
        return sb.ToString();
    }
}
