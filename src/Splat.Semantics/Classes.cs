using Splat.Core;
using Splat.Syntax;

namespace Splat.Semantics;

/// <summary>A field of a class (own, not inherited). The type is written in terms of the class's own type parameters.</summary>
public sealed class FieldInfo
{
    public string Name { get; }
    public SType Type { get; internal set; }
    public bool IsSecret { get; }
    public Expr? Default { get; }
    public ClassInfo Owner { get; }
    public SourceSpan Span { get; }
    internal FieldInfo(string name, SType type, bool secret, Expr? def, ClassInfo owner, SourceSpan span)
    { Name = name; Type = type; IsSecret = secret; Default = def; Owner = owner; Span = span; }
}

/// <summary>
/// A method of a class, trait or extend block. <see cref="Sym"/> carries the declaration and drives body checking; a
/// built-in method has a <see cref="FixedSig"/> instead. Signatures exclude <c>self</c>.
/// </summary>
public sealed class MethodInfo
{
    public string Name { get; }
    public FnDecl? Decl { get; }
    public bool IsSecret { get; }
    /// <summary>How the method takes <c>self</c>: Self, PeekSelf, PokeSelf, or Normal when it takes none.</summary>
    public ParamKind SelfKind { get; }
    /// <summary>The class, trait or extend block that declares it.</summary>
    public object Owner { get; }
    internal Symbol? Sym { get; set; }
    public FuncSType? FixedSig { get; }
    /// <summary>The declaration with its <c>self</c> parameter removed, so call checking can map arguments to parameters.</summary>
    internal FnDecl? CallDecl { get; set; }
    public bool HasBody => Decl is not null && (Decl.ExprBody is not null || Decl.BlockBody is not null);

    internal MethodInfo(string name, FnDecl? decl, bool secret, ParamKind selfKind, object owner, FuncSType? fixedSig = null)
    { Name = name; Decl = decl; IsSecret = secret; SelfKind = selfKind; Owner = owner; FixedSig = fixedSig; }

    public bool Mutates => SelfKind == ParamKind.PokeSelf;
}

/// <summary>A user (or built-in) class.</summary>
public sealed class ClassInfo
{
    public string Name { get; }
    public IReadOnlyList<string> TypeParams { get; }
    /// <summary>One bound per type parameter (null when unbounded).</summary>
    public List<TraitSType?> Bounds { get; } = [];
    public ClassSType? Parent { get; internal set; }
    public List<TraitSType> Traits { get; } = [];
    public List<FieldInfo> Fields { get; } = [];
    public Dictionary<string, MethodInfo> Methods { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, MethodInfo> Ops { get; } = new(StringComparer.Ordinal);
    public ClassDecl? Decl { get; }
    public int DeclaredLine { get; }
    public bool IsBuiltin { get; }

    public ClassInfo(string name, IReadOnlyList<string> typeParams, ClassDecl? decl, int declaredLine, bool builtin = false)
    { Name = name; TypeParams = typeParams; Decl = decl; DeclaredLine = declaredLine; IsBuiltin = builtin; }

    public MethodInfo? Init => Methods.GetValueOrDefault("init");
    public MethodInfo? Deinit => Methods.GetValueOrDefault("deinit");
    public FieldInfo? OwnField(string name) => Fields.FirstOrDefault(f => f.Name == name);

    /// <summary>This class as a type, applied to its own type parameters (what <c>self</c> has inside the class).</summary>
    public ClassSType SelfType => new(this, TypeParams.Select(p => (SType)new TypeParamSType(p)).ToList());
}

/// <summary>
/// A trait: required method signatures plus default implementations. <see cref="Impls"/> lists the non-class types an
/// <c>extend T with Trait</c> made implementers.
/// </summary>
public sealed class TraitInfo
{
    public string Name { get; }
    public IReadOnlyList<string> TypeParams { get; }
    public List<MethodInfo> Methods { get; } = [];
    public List<SType> Impls { get; } = [];
    public TraitDecl? Decl { get; }
    public int DeclaredLine { get; }
    public bool IsBuiltinIterable { get; init; }

    public TraitInfo(string name, IReadOnlyList<string> typeParams, TraitDecl? decl, int declaredLine)
    { Name = name; TypeParams = typeParams; Decl = decl; DeclaredLine = declaredLine; }

    public MethodInfo? Method(string name) => Methods.FirstOrDefault(m => m.Name == name);
    public TraitSType SelfType => new(this, TypeParams.Select(p => (SType)new TypeParamSType(p)).ToList());
}

/// <summary>An <c>extend Target [with Trait]:</c> block.</summary>
public sealed class ExtInfo
{
    public SType Target { get; internal set; } = SType.Error;
    public TraitSType? Trait { get; internal set; }
    public List<MethodInfo> Methods { get; } = [];
    public ExtendDecl Decl { get; }
    public ExtInfo(ExtendDecl decl) => Decl = decl;
    public MethodInfo? Method(string name) => Methods.FirstOrDefault(m => m.Name == name);
}

/// <summary>A class applied to type arguments: <c>Dog</c>, <c>Stack&lt;i32&gt;</c>.</summary>
public sealed class ClassSType : SType
{
    public ClassInfo Info { get; }
    public IReadOnlyList<SType> Args { get; }
    public ClassSType(ClassInfo info, IReadOnlyList<SType> args) { Info = info; Args = args; }

    public override string Name => Args.Count == 0 ? Info.Name : $"{Info.Name}<{string.Join(", ", Args.Select(a => a.Name))}>";
    public override bool Equals(SType? other) =>
        other is ClassSType c && ReferenceEquals(c.Info, Info) && c.Args.Count == Args.Count && c.Args.Zip(Args).All(p => p.First.Equals(p.Second));
    public override int GetHashCode() => Info.Name.GetHashCode(StringComparison.Ordinal);

    /// <summary>Replaces the class's own type parameters by this instance's arguments throughout <paramref name="template"/>.</summary>
    public SType Substitute(SType template)
    {
        if (Info.TypeParams.Count == 0 || Args.Count != Info.TypeParams.Count) return template;
        var map = new Dictionary<string, SType>(StringComparer.Ordinal);
        for (int i = 0; i < Args.Count; i++) map[Info.TypeParams[i]] = Args[i];
        return Types.Substitute(template, map);
    }

    /// <summary>The parent class type with this instance's arguments applied, or null for a root class.</summary>
    public ClassSType? ParentType() => Info.Parent is null ? null : (ClassSType)Substitute(Info.Parent);

    /// <summary>This class and every ancestor, nearest first.</summary>
    public IEnumerable<ClassSType> Chain()
    {
        for (var c = this; c is not null; c = c.ParentType()) yield return c;
    }
}

/// <summary>A trait applied to type arguments, used as a type (<c>fn f(t::Talkable)</c>) or as a bound (<c>T: Talkable</c>).</summary>
public sealed class TraitSType : SType
{
    public TraitInfo Info { get; }
    public IReadOnlyList<SType> Args { get; }
    public TraitSType(TraitInfo info, IReadOnlyList<SType> args) { Info = info; Args = args; }

    public override string Name => Args.Count == 0 ? Info.Name : $"{Info.Name}<{string.Join(", ", Args.Select(a => a.Name))}>";
    public override bool Equals(SType? other) =>
        other is TraitSType t && ReferenceEquals(t.Info, Info) && t.Args.Count == Args.Count && t.Args.Zip(Args).All(p => p.First.Equals(p.Second));
    public override int GetHashCode() => Info.Name.GetHashCode(StringComparison.Ordinal);

    public SType Substitute(SType template)
    {
        if (Info.TypeParams.Count == 0 || Args.Count != Info.TypeParams.Count) return template;
        var map = new Dictionary<string, SType>(StringComparer.Ordinal);
        for (int i = 0; i < Args.Count; i++) map[Info.TypeParams[i]] = Args[i];
        return Types.Substitute(template, map);
    }
}

/// <summary>A built-in opaque type with methods: <c>File</c>, <c>Socket</c>, <c>Server</c>, <c>Json</c>, <c>Date</c>, <c>Time</c>, <c>Stamp</c>, <c>Dur</c>, <c>Cal</c>.</summary>
public sealed class NativeSType : SType
{
    private readonly string _name;
    internal NativeSType(string name) => _name = name;
    public override string Name => _name;
    public override bool Equals(SType? other) => other is NativeSType n && n._name == _name;
    public override int GetHashCode() => _name.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A lazy, one-shot stream of values (<c>File.lines()</c>). Iterable in <c>loop</c> but not a collection.</summary>
public sealed class IterSType : SType
{
    public SType Elem { get; }
    public IterSType(SType elem) => Elem = elem;
    public override string Name => $"iter<{Elem.Name}>";
    public override bool Equals(SType? other) => other is IterSType i && i.Elem.Equals(Elem);
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

/// <summary>Signatures of the methods and static functions of the built-in native types.</summary>
public static class NativeMembers
{
    public sealed record Sig(IReadOnlyList<SType> Params, SType Ret);

    private static Sig S(SType ret, params SType[] ps) => new(ps, ret);

    private static SType Maybe(SType t) => EnumRegistry.MaybeOf(t);

    public static readonly IReadOnlyDictionary<string, Sig> File = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["lines"] = S(new IterSType(Types.Txt)),
        ["read_all"] = S(Types.Txt),
        ["write"] = S(Types.Unit, Types.Txt),
        ["write_line"] = S(Types.Unit, Types.Txt),
        ["close"] = S(Types.Unit),
    };

    public static readonly IReadOnlyDictionary<string, Sig> Socket = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["send"] = S(Types.Unit, Types.Txt),
        ["send_to"] = S(Types.Unit, Types.Txt, Types.I32, Types.Txt),
        ["recv"] = S(Types.Txt),
        ["accept"] = S(Types.SocketT),
        ["port"] = S(Types.I32),
        ["close"] = S(Types.Unit),
    };

    public static readonly IReadOnlyDictionary<string, Sig> Server = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["stop"] = S(Types.Unit),
        ["port"] = S(Types.I32),
        ["wait"] = S(Types.Unit),
    };

    public static readonly IReadOnlyDictionary<string, Sig> Json = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["kind"] = S(Types.Txt),
        ["len"] = S(Types.Usize),
        ["keys"] = S(new ListSType(Types.Txt)),
        ["items"] = S(new ListSType(Types.JsonT)),
        ["get"] = S(Maybe(Types.JsonT), Types.Txt),
        ["at"] = S(Maybe(Types.JsonT), Types.Usize),
        ["as_txt"] = S(Maybe(Types.Txt)),
        ["as_f64"] = S(Maybe(Types.F64)),
        ["as_i64"] = S(Maybe(Types.I64)),
        ["as_bool"] = S(Maybe(Types.Bool)),
    };

    public static readonly IReadOnlyDictionary<string, Sig> Date = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["year"] = S(Types.I32), ["month"] = S(Types.I32), ["day"] = S(Types.I32), ["weekday"] = S(Types.I32), ["iso"] = S(Types.Txt),
    };

    public static readonly IReadOnlyDictionary<string, Sig> Time = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["hour"] = S(Types.I32), ["minute"] = S(Types.I32), ["second"] = S(Types.I32), ["iso"] = S(Types.Txt),
    };

    public static readonly IReadOnlyDictionary<string, Sig> Stamp = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["year"] = S(Types.I32), ["month"] = S(Types.I32), ["day"] = S(Types.I32), ["weekday"] = S(Types.I32),
        ["hour"] = S(Types.I32), ["minute"] = S(Types.I32), ["second"] = S(Types.I32), ["iso"] = S(Types.Txt),
        ["date"] = S(Types.DateT), ["time"] = S(Types.TimeT),
    };

    public static readonly IReadOnlyDictionary<string, Sig> Dur = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["ns"] = S(Types.I64), ["ms"] = S(Types.I64), ["secs"] = S(Types.F64),
    };

    public static readonly IReadOnlyDictionary<string, Sig> Cal = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["months"] = S(Types.I64),
    };

    public static IReadOnlyDictionary<string, Sig>? MethodsOf(SType t) => t switch
    {
        NativeSType n => n.Name switch
        {
            "File" => File, "Socket" => Socket, "Server" => Server, "Json" => Json,
            "Date" => Date, "Time" => Time, "Stamp" => Stamp, "Dur" => Dur, "Cal" => Cal, _ => null,
        },
        _ => null,
    };

    /// <summary>Static functions called on a type name: <c>Socket.tcp(host, port)</c>, <c>Res.ok(body)</c>.</summary>
    public static IReadOnlyDictionary<string, Sig>? StaticsOf(string typeName) => typeName switch
    {
        "Socket" => SocketStatics,
        "Res" => ResStatics,
        _ => null,
    };

    private static readonly IReadOnlyDictionary<string, Sig> SocketStatics = new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["tcp"] = S(Types.SocketT, Types.Txt, Types.I32),
        ["udp"] = S(Types.SocketT, Types.I32),
        ["listen"] = S(Types.SocketT, Types.I32),
    };

    private static IReadOnlyDictionary<string, Sig>? _resStatics;
    private static IReadOnlyDictionary<string, Sig> ResStatics => _resStatics ??= new Dictionary<string, Sig>(StringComparer.Ordinal)
    {
        ["ok"] = S(BuiltinClasses.ResType, Types.Txt),
        ["text"] = S(BuiltinClasses.ResType, Types.Txt),
        ["json"] = S(BuiltinClasses.ResType, Types.Txt),
        ["not_found"] = S(BuiltinClasses.ResType, Types.Txt),
        ["status"] = S(BuiltinClasses.ResType, Types.I32, Types.Txt),
    };
}

/// <summary>Built-in classes (<c>Catch</c>, <c>Req</c>, <c>Res</c>) and the built-in <c>Iterable&lt;T&gt;</c> trait.</summary>
public static class BuiltinClasses
{
    private static ClassInfo Make(string name, params (string Name, SType Type)[] fields)
    {
        var c = new ClassInfo(name, [], null, 0, builtin: true);
        foreach (var (n, t) in fields) c.Fields.Add(new FieldInfo(n, t, false, null, c, SourceSpan.None));
        return c;
    }

    public static readonly ClassInfo Catch = Make("Catch",
        ("message", Types.Txt), ("kind", Types.Txt), ("code", Types.Txt), ("line", Types.I32), ("span", Types.Txt), ("trace", new ListSType(Types.Txt)));

    public static readonly ClassInfo Req = Make("Req",
        ("method", Types.Txt), ("path", Types.Txt), ("query", new MapSType(Types.Txt, Types.Txt)),
        ("headers", new MapSType(Types.Txt, Types.Txt)), ("body", Types.Txt));

    public static readonly ClassInfo Res = Make("Res",
        ("status", Types.I32), ("headers", new MapSType(Types.Txt, Types.Txt)), ("body", Types.Txt));

    public static ClassSType CatchType => new(Catch, []);
    public static ClassSType ReqType => new(Req, []);
    public static ClassSType ResType => new(Res, []);

    /// <summary><c>trait Iterable&lt;T&gt;: fn next(~self)::T? ;</c></summary>
    public static readonly TraitInfo Iterable = CreateIterable();

    private static TraitInfo CreateIterable()
    {
        var t = new TraitInfo("Iterable", ["T"], null, 0) { IsBuiltinIterable = true };
        t.Methods.Add(new MethodInfo("next", null, false, ParamKind.PokeSelf, t,
            new FuncSType([], EnumRegistry.MaybeOf(new TypeParamSType("T")))));
        return t;
    }
}
