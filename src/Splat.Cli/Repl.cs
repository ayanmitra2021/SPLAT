using System.Text;
using Splat.Core;
using Splat.Runtime;
using Splat.Syntax;

namespace Splat.Cli;

/// <summary>
/// The interactive loop. Each entry is lex, parse, check, run against a persistent <see cref="SessionState"/>. Input that
/// leaves a block, bracket, string or block comment open is continued on the next line. Results go to <c>output</c>,
/// diagnostics to <c>error</c>; <c>readl</c> reads from the same <c>input</c>, so a piped transcript can answer it.
/// </summary>
public sealed class Repl
{
    public const string Prompt = "splat> ";
    public const string ContinuationPrompt = "  ...> ";

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _prompts;
    private readonly bool _color;
    private readonly SessionState _session;
    private readonly List<string> _history = [];

    /// <param name="prompts">Show the banner and prompts (an interactive terminal); off for piped input so output stays clean.</param>
    public Repl(TextReader input, TextWriter output, TextWriter error, bool prompts, bool color)
    {
        _input = input;
        _output = output;
        _error = error;
        _prompts = prompts;
        _color = color;
        _session = new SessionState(input, output, error);
    }

    public IReadOnlyList<string> History => _history;

    /// <summary>Runs until <c>:quit</c> or end of input. Always returns 0: errors in an entry never end the session.</summary>
    public int Run()
    {
        if (_prompts)
        {
            _output.WriteLine($"🍥 SPLAT {Program.Version} - Strict, Portable, Linear-algebraic & AI Terse-language");
            _output.WriteLine("Type an expression or statement. ':help' lists commands, ':quit' leaves.");
        }

        var pending = new StringBuilder();
        while (true)
        {
            if (_prompts) { _output.Write(pending.Length == 0 ? Prompt : ContinuationPrompt); _output.Flush(); }
            var line = _input.ReadLine();
            if (line is null)
            {
                if (_prompts) _output.WriteLine();
                // End of input inside an unfinished entry: run it anyway so the missing ';' is reported.
                if (pending.Length > 0) Submit(pending.ToString());
                return 0;
            }

            if (pending.Length == 0)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed[0] == ':')
                {
                    if (!Command(trimmed)) return 0;
                    continue;
                }
            }

            pending.Append(line).Append('\n');
            if (IsIncomplete(pending.ToString())) continue;
            Submit(pending.ToString());
            pending.Clear();
        }
    }

    private void Submit(string text)
    {
        _history.Add(text.TrimEnd('\n'));
        Report(_session.Eval(text, "<repl>"));
    }

    private void Report(EvalResult r)
    {
        if (r.Outcome != RunOutcome.Ok || r.Diagnostics.Diagnostics.Count > 0)
            new ConsolePrinter(_error, _color).Print(r.Diagnostics);
        if (r.Echo is not null) _output.WriteLine(r.Echo);
    }

    // ---- commands ----

    /// <summary>Runs a ':' command. Returns false when the session should end.</summary>
    private bool Command(string line)
    {
        var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0];
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        switch (name)
        {
            case ":quit" or ":q" or ":exit":
                return false;
            case ":help" or ":h" or ":?":
                _output.WriteLine(HelpText);
                return true;
            case ":reset":
                _session.Reset();
                _output.WriteLine("Session cleared. All bindings and functions are forgotten.");
                return true;
            case ":history":
                for (int i = 0; i < _history.Count; i++) _output.WriteLine($"{i + 1,4}  {_history[i].Replace("\n", "\n      ")}");
                return true;
            case ":vars" or ":names":
                _output.WriteLine(_session.Names.Count == 0 ? "(nothing defined yet)" : string.Join(", ", _session.Names));
                return true;
            case ":load":
                Load(arg);
                return true;
            default:
                PrintOne(Templates.UnknownCommand(SourceSpan.None, name));
                return true;
        }
    }

    private void PrintOne(Diagnostic d)
    {
        var engine = new DiagnosticEngine();
        engine.Report(d);
        new ConsolePrinter(_error, _color).Print(engine);
    }

    private void Load(string arg)
    {
        if (arg.Length == 0)
        {
            PrintOne(Templates.UnexpectedToken(SourceSpan.None, ":load", "a file name, as in ':load examples/hello.splat'"));
            return;
        }

        var path = arg.Trim('"');
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PrintOne(Templates.FileNotFound(SourceSpan.None, path));
            return;
        }
        // The file is one transactional entry: if any part fails, none of its bindings are kept. It does not echo values.
        Report(_session.Eval(text, path, echo: false));
    }

    public const string HelpText =
        """
        Type SPLAT at the prompt. An expression prints its value (=> 42 :: i32). A line that ends inside an open
        ':' block, '(' or '[', string or #- comment continues on the next prompt.

          :help            show this help
          :reset           forget every binding and function
          :load <file>     run a .splat file inside this session (all or nothing)
          :vars            list the names defined so far
          :history         show what you have entered
          :quit            leave (also :q, or end of input)
        """;

    // ---- multi-line continuation ----

    /// <summary>
    /// True when the text leaves something open that more lines could close: an unterminated string or block comment, an
    /// unclosed '(' or '[', or a ':' block with no matching ';'. The 'elif', 'el' and 'catch' colons continue a block that
    /// is already open instead of opening a new one.
    /// </summary>
    public static bool IsIncomplete(string text)
    {
        var diag = new DiagnosticEngine(1000);
        var src = new SourceText("<repl>", text);
        var tokens = new Lexer(src, diag).Tokenize();
        if (diag.Diagnostics.Any(d => d.Category is DiagnosticCategory.UnterminatedString or DiagnosticCategory.UnterminatedComment))
            return true;

        int brackets = 0, blocks = 0;
        bool chainColon = false;
        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case TokenKind.LParen or TokenKind.LBracket: brackets++; break;
                case TokenKind.RParen or TokenKind.RBracket: brackets--; break;
                case TokenKind.Elif or TokenKind.Else or TokenKind.Catch: chainColon = true; break;
                case TokenKind.Colon:
                    if (chainColon) chainColon = false; else blocks++;
                    break;
                case TokenKind.Semicolon: blocks--; break;
            }
        }
        return brackets > 0 || blocks > 0;
    }
}
