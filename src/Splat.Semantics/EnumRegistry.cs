namespace Splat.Semantics;

/// <summary>A variant together with the enum that owns it.</summary>
public sealed record VariantRef(EnumSType Enum, EnumVariantInfo Variant);

/// <summary>
/// Enums (algebraic data types) and their variants, by name. Registries chain: the built-in one holds <c>Maybe</c>, a
/// program (or one REPL entry) adds a child on top, so a failed REPL entry leaves nothing behind and a user enum may shadow
/// a built-in one. Variant names are unqualified in expressions and patterns, so they must be unique within one registry.
/// </summary>
public sealed class EnumRegistry
{
    private readonly EnumRegistry? _parent;
    private readonly Dictionary<string, EnumSType> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VariantRef> _variants = new(StringComparer.Ordinal);

    public EnumRegistry(EnumRegistry? parent = null) => _parent = parent;

    /// <summary><c>Maybe&lt;T&gt;</c>: <c>Got(T)</c> or <c>Nada</c>. There is no <c>null</c> in SPLAT.</summary>
    public static readonly EnumSType Maybe = new("Maybe", ["T"],
        [new EnumVariantInfo("Got", [new TypeParamSType("T")]), new EnumVariantInfo("Nada", [])]);

    /// <summary>The root registry; holds only the built-in enums.</summary>
    public static EnumRegistry Builtin { get; } = CreateBuiltin();

    private static EnumRegistry CreateBuiltin()
    {
        var r = new EnumRegistry();
        r.AddEnum(Maybe);
        r.AddVariants(Maybe);
        return r;
    }

    public static SType MaybeOf(SType t) => new EnumInstanceSType(Maybe, [t]);

    public EnumSType? FindEnum(string name)
    {
        for (var r = this; r is not null; r = r._parent)
            if (r._enums.TryGetValue(name, out var m)) return m;
        return null;
    }

    public VariantRef? FindVariant(string name)
    {
        for (var r = this; r is not null; r = r._parent)
            if (r._variants.TryGetValue(name, out var v)) return v;
        return null;
    }

    /// <summary>False when this registry (not a parent) already has a enum of that name.</summary>
    public bool AddEnum(EnumSType enumType)
    {
        if (_enums.ContainsKey(enumType.EnumName)) return false;
        _enums[enumType.EnumName] = enumType;
        return true;
    }

    /// <summary>Registers every variant of <paramref name="enum"/>; returns the names that were already taken in this registry.</summary>
    public List<string> AddVariants(EnumSType enumType)
    {
        var duplicates = new List<string>();
        foreach (var v in enumType.Variants)
        {
            if (_variants.ContainsKey(v.Name)) { duplicates.Add(v.Name); continue; }
            _variants[v.Name] = new VariantRef(enumType, v);
        }
        return duplicates;
    }
}
