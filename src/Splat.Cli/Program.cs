using System.Text;
using Splat.Core;

namespace Splat.Cli;

public static class Program
{
    public const string Version = "0.2.0-phase1";

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        if (args.Length == 0 || args[0] is "--version" or "-v")
        {
            Console.WriteLine($"🍥 SPLAT {Version} - Strict, Portable, Linear-algebraic & AI Terse-language");
            if (args.Length == 0) Console.WriteLine("REPL and 'run' arrive in later phases. For now I only look pretty.");
            return 0;
        }

        Console.Error.WriteLine($"Unknown command '{args[0]}'. (Nothing is implemented yet; see build_plan.md.)");
        return 64;
    }
}
