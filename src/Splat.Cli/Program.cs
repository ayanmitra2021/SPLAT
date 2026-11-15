using System.Text;
using Splat.Core;
using Splat.Syntax;

namespace Splat.Cli;

public static class Program
{
    public const string Version = "0.2.0-phase3";

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        if (args.Length == 0 || args[0] is "--version" or "-v")
        {
            Console.WriteLine($"🍥 SPLAT {Version} - Strict, Portable, Linear-algebraic & AI Terse-language");
            if (args.Length == 0) Console.WriteLine("REPL and 'run' arrive in later phases. Try 'splat tokens file.splat' or 'splat ast file.splat'.");
            return 0;
        }

        switch (args[0])
        {
            case "tokens" when args.Length == 2:
                return DumpTokens(args[1]);
            case "tokens":
                Console.Error.WriteLine("Usage: splat tokens <file.splat>");
                return 64;
            case "ast" when args.Length == 2:
                return DumpAst(args[1], formatSource: false);
            case "ast" when args.Length == 3 && args[1] == "--fmt":
                return DumpAst(args[2], formatSource: true);
            case "ast":
                Console.Error.WriteLine("Usage: splat ast [--fmt] <file.splat>");
                return 64;
            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'. See build_plan.md for what exists so far.");
                return 64;
        }
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
