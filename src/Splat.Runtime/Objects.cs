using Splat.Core;
using Splat.Syntax;

namespace Splat.Runtime;

/// <summary>
/// A reference value that lives on the heap and has a lifetime: a class instance or a native handle (File, Socket,
/// Server, Json, lazy iterator). <see cref="Owner"/> is whatever is responsible for destroying it: a variable
/// (<see cref="Cell"/>), another object (the parent whose field holds it) or nothing (a fresh temporary).
/// </summary>
public abstract class HeapObj
{
    internal object? Owner;
    internal bool Dying;
    public bool Dead { get; internal set; }
    public int DeadLine { get; internal set; }
    public abstract string TypeName { get; }
    public abstract string Display();
    /// <summary>Releases external resources. Called once, by the interpreter, when the owner leaves scope or on <c>drop</c>.</summary>
    internal virtual void Close() { }
}

internal sealed record FieldDef(string Name, Expr? Default, bool IsSecret);

/// <summary>The runtime form of a class: fields, methods (as closures) and its parent.</summary>
internal sealed class ClassDef
{
    public required string Name { get; init; }
    public ClassDef? Parent { get; set; }
    public List<FieldDef> Fields { get; } = [];
    public Dictionary<string, Closure> Methods { get; } = new(StringComparer.Ordinal);
    public List<string> TraitNames { get; } = [];
    public Env? Env { get; init; }
    public bool InitCallsSuperInit { get; set; }
    public bool Builtin { get; init; }
    public Func<ClassInst, string>? DisplayHook { get; init; }

    public IEnumerable<ClassDef> Chain()
    {
        for (var c = this; c is not null; c = c.Parent) yield return c;
    }

    public Closure? FindMethod(string name)
    {
        foreach (var c in Chain())
            if (c.Methods.TryGetValue(name, out var m)) return m;
        return null;
    }

    /// <summary>Every field name, inherited ones first (the order they are initialised in).</summary>
    public IEnumerable<FieldDef> AllFields() => Chain().Reverse().SelectMany(c => c.Fields);
}

public sealed class ClassInst : HeapObj
{
    internal ClassDef Class { get; }
    public Dictionary<string, Value> Fields { get; } = new(StringComparer.Ordinal);
    internal ClassInst(ClassDef cls) => Class = cls;

    public override string TypeName => Class.Name;

    public override string Display()
    {
        if (Class.DisplayHook is { } hook) return hook(this);
        var parts = Class.AllFields().Where(f => !f.IsSecret && Fields.ContainsKey(f.Name))
            .Select(f => $"{f.Name}: {Formatter.Repr(Fields[f.Name])}");
        return $"{Class.Name}{{{string.Join(", ", parts)}}}";
    }
}

/// <summary>A trait's default methods, as closures.</summary>
internal sealed class TraitDef
{
    public required string Name { get; init; }
    public Dictionary<string, Closure> Defaults { get; } = new(StringComparer.Ordinal);
}
