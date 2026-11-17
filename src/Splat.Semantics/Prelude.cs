namespace Splat.Semantics;

/// <summary>
/// The names callable without a declaration. A user binding with the same name shadows them. Shared by the type checker
/// (signatures) and the interpreter (implementations).
/// </summary>
public static class Prelude
{
    /// <summary>Printing: any number of printable arguments, returns <c>zip</c>.</summary>
    public static readonly IReadOnlySet<string> Printers = new HashSet<string>(StringComparer.Ordinal) { "squish", "yell" };

    /// <summary>Functions with one fixed signature. Parse functions rip on bad input until Phase 7 brings <c>Maybe&lt;T&gt;</c>.</summary>
    public static readonly IReadOnlyDictionary<string, FuncSType> Fixed = new Dictionary<string, FuncSType>(StringComparer.Ordinal)
    {
        ["len"] = new([Types.Txt], Types.Usz),
        ["upper"] = new([Types.Txt], Types.Txt),
        ["lower"] = new([Types.Txt], Types.Txt),
        ["trim"] = new([Types.Txt], Types.Txt),
        ["parse_i32"] = new([Types.Txt], Types.I32),
        ["parse_i64"] = new([Types.Txt], Types.I64),
        ["parse_mid"] = new([Types.Txt], Types.Mid),
        ["parse_chonk"] = new([Types.Txt], Types.Chonk),
    };

    /// <summary>Names with bespoke checking: <c>slurp</c> (0 or 1 argument) and <c>rev</c> (a range).</summary>
    public static readonly IReadOnlySet<string> Special = new HashSet<string>(StringComparer.Ordinal) { "slurp", "rev" };

    public static bool IsBuiltin(string name) => Printers.Contains(name) || Fixed.ContainsKey(name) || Special.Contains(name);
}
