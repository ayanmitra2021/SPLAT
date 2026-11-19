namespace Splat.Semantics;

/// <summary>
/// The names callable without a declaration. A user binding with the same name shadows them. Shared by the type checker
/// (signatures) and the interpreter (implementations).
/// </summary>
public static class Prelude
{
    /// <summary>Printing: any number of printable arguments, returns <c>unit</c>.</summary>
    public static readonly IReadOnlySet<string> Printers = new HashSet<string>(StringComparer.Ordinal) { "print", "eprint" };

    /// <summary>Functions with one fixed signature.</summary>
    public static readonly IReadOnlyDictionary<string, FuncSType> Fixed = new Dictionary<string, FuncSType>(StringComparer.Ordinal)
    {
        ["upper"] = new([Types.Txt], Types.Txt),
        ["lower"] = new([Types.Txt], Types.Txt),
        ["trim"] = new([Types.Txt], Types.Txt),
        ["parse_i32"] = new([Types.Txt], EnumRegistry.MaybeOf(Types.I32)),
        ["parse_i64"] = new([Types.Txt], EnumRegistry.MaybeOf(Types.I64)),
        ["parse_mid"] = new([Types.Txt], EnumRegistry.MaybeOf(Types.F32)),
        ["parse_chonk"] = new([Types.Txt], EnumRegistry.MaybeOf(Types.F64)),
        ["split"] = new([Types.Txt, Types.Txt], new ListSType(Types.Txt)),
        ["lines"] = new([Types.Txt], new ListSType(Types.Txt)),
    };

    /// <summary>
    /// Names with bespoke checking: <c>readl</c>, <c>rev</c> (range or list), <c>len</c> (txt or list),
    /// <c>enumerate</c>, <c>unit</c>, <c>set(a, b, ...)</c>, <c>map(k =&gt; v, ...)</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> Special = new HashSet<string>(StringComparer.Ordinal)
        { "readl", "rev", "len", "enumerate", "zip", "set", "map" };

    public static bool IsBuiltin(string name) => Printers.Contains(name) || Fixed.ContainsKey(name) || Special.Contains(name);
}
