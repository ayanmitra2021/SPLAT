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
    // Phases 8-9: classes, traits and extend blocks live in the same chained registry.
    private readonly Dictionary<string, ClassInfo> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TraitInfo> _traits = new(StringComparer.Ordinal);
    private readonly List<ExtInfo> _exts = [];

    public EnumRegistry(EnumRegistry? parent = null) => _parent = parent;

    /// <summary><c>Maybe&lt;T&gt;</c>: <c>Got(T)</c> or <c>Nada</c>. There is no <c>null</c> in SPLAT.</summary>
    public static readonly EnumSType Maybe = new("Maybe", ["T"],
        [new EnumVariantInfo("Got", [new TypeParamSType("T")]), new EnumVariantInfo("Nada", [])]);

    /// <summary>The root registry; holds only the built-in enums.</summary>
    private static readonly Lazy<EnumRegistry> BuiltinLazy = new(CreateBuiltin);
    public static EnumRegistry Builtin => BuiltinLazy.Value;

    private static EnumRegistry CreateBuiltin()
    {
        var r = new EnumRegistry();
        r.AddEnum(Maybe);
        r.AddVariants(Maybe);
        r.AddClass(BuiltinClasses.Catch);
        r.AddClass(BuiltinClasses.Req);
        r.AddClass(BuiltinClasses.Res);
        r.AddTrait(BuiltinClasses.Iterable);
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

    public ClassInfo? FindClass(string name)
    {
        for (var r = this; r is not null; r = r._parent)
            if (r._classes.TryGetValue(name, out var c)) return c;
        return null;
    }

    public TraitInfo? FindTrait(string name)
    {
        for (var r = this; r is not null; r = r._parent)
            if (r._traits.TryGetValue(name, out var t)) return t;
        return null;
    }

    /// <summary>False when this registry (not a parent) already has a class of that name.</summary>
    public bool AddClass(ClassInfo c)
    {
        if (_classes.ContainsKey(c.Name)) return false;
        _classes[c.Name] = c;
        return true;
    }

    public bool AddTrait(TraitInfo t)
    {
        if (_traits.ContainsKey(t.Name)) return false;
        _traits[t.Name] = t;
        return true;
    }

    public void AddExtension(ExtInfo e) => _exts.Add(e);

    /// <summary>Every extend block visible here, oldest first (parents before children, declaration order inside one registry).</summary>
    public IEnumerable<ExtInfo> Extensions()
    {
        var chain = new List<EnumRegistry>();
        for (var r = this; r is not null; r = r._parent) chain.Add(r);
        chain.Reverse();
        return chain.SelectMany(r => r._exts);
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
