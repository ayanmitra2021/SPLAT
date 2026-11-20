using Splat.Core;
using Splat.Syntax;

namespace Splat.Semantics;

// Named-type resolution for classes/traits/natives, and the date/time rules (spec 4.5).
public sealed partial class TypeChecker
{
    private SType ResolveNamed(NamedType n)
    {
        if (n.Args.Count == 0 && _typeParams.Contains(n.Name))
            return new TypeParamSType(n.Name) { Bound = _typeBounds.GetValueOrDefault(n.Name) };

        if (Types.ByName.TryGetValue(n.Name, out var prim))
        {
            if (n.Args.Count > 0) { Report(Templates.BadTypeArgs(n.Span, n.Name, 0, n.Args.Count)); return SType.Error; }
            return prim;
        }

        if (_enums.FindClass(n.Name) is { } ci)
        {
            var args = ResolveArgs(n, ci.TypeParams.Count);
            return args is null ? SType.Error : new ClassSType(ci, args);
        }
        if (_enums.FindTrait(n.Name) is { } ti)
        {
            var args = ResolveArgs(n, ti.TypeParams.Count);
            return args is null ? SType.Error : new TraitSType(ti, args);
        }
        if (Types.NativeByName.TryGetValue(n.Name, out var native))
        {
            if (n.Args.Count > 0) { Report(Templates.BadTypeArgs(n.Span, n.Name, 0, n.Args.Count)); return SType.Error; }
            return native;
        }

        int arity = n.Name switch { "List" or "Set" => 1, "Map" => 2, _ => _enums.FindEnum(n.Name)?.TypeParams.Count ?? -1 };
        if (arity < 0)
        {
            foreach (var a in n.Args) ResolveType(a);
            Report(Templates.UndefinedName(n.Span, n.Name));
            return SType.Error;
        }
        var targs = ResolveArgs(n, arity);
        if (targs is null) return SType.Error;
        return n.Name switch
        {
            "List" => new ListSType(targs[0]),
            "Set" => CheckedSet(targs[0], n.Args[0].Span),
            "Map" => CheckedMap(targs[0], targs[1], n.Args[0].Span),
            _ => new EnumInstanceSType(_enums.FindEnum(n.Name)!, targs),
        };
    }

    /// <summary>Resolves a name's type arguments; reports and returns null when their number is wrong.</summary>
    private List<SType>? ResolveArgs(NamedType n, int arity)
    {
        var args = n.Args.Select(ResolveType).ToList();
        if (args.Count != arity)
        {
            Report(Templates.BadTypeArgs(n.Span, n.Name, arity, args.Count));
            return null;
        }
        return args;
    }

    // ---- date and time ----

    private static SType TemporalType(TemporalLiteral t) => t.Kind switch
    {
        TemporalKind.Date => Types.DateT,
        TemporalKind.Time => Types.TimeT,
        TemporalKind.Stamp => Types.StampT,
        TemporalKind.Dur => Types.DurT,
        _ => Types.CalT,
    };

    private SType CheckTemporalRange(BinaryExpr b, SType l, SType r)
    {
        if (l.IsError || r.IsError) return SType.Error;
        if (!l.Equals(r))
        {
            Report(Templates.TypeMismatch(b.Right.Span, N(r), $"{N(l)} (both ends of a range must be the same kind of point in time)"));
            return SType.Error;
        }
        // A Date steps by one day unless told otherwise; Time and Stamp must say how far apart their points are.
        return new RangeSType(l) { NeedsStep = !ReferenceEquals(l, Types.DateT) };
    }

    private SType CheckTemporalStep(StepExpr s, SType elem, SType step)
    {
        var rt = new RangeSType(elem);
        if (step.IsAny) return rt;
        bool dur = ReferenceEquals(step, Types.DurT), cal = ReferenceEquals(step, Types.CalT);
        bool allowed = ReferenceEquals(elem, Types.TimeT) ? dur : dur || cal;
        if (!allowed)
        {
            Report(Templates.TypeMismatch(s.Step.Span, N(step), ReferenceEquals(elem, Types.TimeT)
                ? "a Dur step such as 15m (a Time of day has no calendar)"
                : "a Dur step (like 1d, 1h) or a Cal step (like 1mo)"));
            return rt;
        }
        if (s.Step is TemporalLiteral lit)
        {
            if (lit.Value <= 0) Report(Templates.BadStep(s.Step.Span, AstPrinter.TemporalText(lit)));
            else if (ReferenceEquals(elem, Types.DateT) && lit.Kind == TemporalKind.Dur && lit.Value % Temporal.NsPerDay != 0)
                Report(Templates.TypeMismatch(s.Step.Span, AstPrinter.TemporalText(lit), "a whole number of days (like 1d or 1w), or a Cal step; a Date has no hours"));
        }
        return rt;
    }

    private void RequireStep(Expr e, SType t)
    {
        if (t is RangeSType { NeedsStep: true } rs) Report(Templates.MissingRangeStep(e.Span, N(rs.Elem)));
    }

    private SType TemporalArithmetic(TokenKind op, SType l, SType r, SourceSpan span, string opText)
    {
        SType Bad(string fix)
        {
            Report(Templates.BadOperator(span, opText, N(l), N(r), fix));
            return SType.Error;
        }
        SType D = Types.DateT, T = Types.TimeT, S = Types.StampT, U = Types.DurT, C = Types.CalT;
        bool Is(SType t, SType k) => ReferenceEquals(t, k);
        bool Span(SType t) => Is(t, U) || Is(t, C);
        bool Point(SType t) => Types.IsTemporalPoint(t);
        bool Int(SType t) => Types.IsInt(t);

        switch (op)
        {
            case TokenKind.Plus:
                if ((Is(l, D) && Span(r)) || (Span(l) && Is(r, D))) return D;
                if ((Is(l, S) && Span(r)) || (Span(l) && Is(r, S))) return S;
                if ((Is(l, T) && Is(r, U)) || (Is(l, U) && Is(r, T))) return T;
                if (Is(l, U) && Is(r, U)) return U;
                if (Is(l, C) && Is(r, C)) return C;
                if ((Is(l, T) && Is(r, C)) || (Is(l, C) && Is(r, T)))
                    return Bad("A Cal (months, years) only works with a Date or a Stamp; a Time of day has no calendar.");
                if (Point(l) && Point(r)) return Bad("Two points in time cannot be added; subtract them to get a Dur.");
                if ((Is(l, U) && Is(r, C)) || (Is(l, C) && Is(r, U)))
                    return Bad("A Dur (fixed length) and a Cal (calendar length) cannot be mixed; add them to a Date one at a time.");
                break;
            case TokenKind.Minus:
                if (Is(l, D) && Is(r, D)) return U;
                if (Is(l, S) && Is(r, S)) return U;
                if (Is(l, T) && Is(r, T)) return U;
                if (Is(l, D) && Span(r)) return D;
                if (Is(l, S) && Span(r)) return S;
                if (Is(l, T) && Is(r, U)) return T;
                if (Is(l, U) && Is(r, U)) return U;
                if (Is(l, C) && Is(r, C)) return C;
                if (Is(l, T) && Is(r, C)) return Bad("A Cal (months, years) only works with a Date or a Stamp; a Time of day has no calendar.");
                if (Span(l) && Point(r)) return Bad("You cannot subtract a point in time from a length of time; put the point first.");
                break;
            case TokenKind.Star:
                if ((Is(l, U) && Int(r)) || (Int(l) && Is(r, U))) return U;
                if ((Is(l, C) && Int(r)) || (Int(l) && Is(r, C))) return C;
                break;
            case TokenKind.Slash:
                if (Is(l, U) && Int(r)) return U;
                if (Is(l, U) && Is(r, U)) return Types.F64;
                if (Is(l, C) && Int(r)) return C;
                break;
        }
        return Bad("Dates and times add and subtract lengths (Dur, Cal) and subtract each other (giving a Dur); lengths also scale by integers.");
    }

    /// <summary>Validates <c>{x:iso}</c> and <c>{x:%Y-%m-%d}</c> against the value's type.</summary>
    private bool CheckTemporalFormat(TemplateExpr te, SType t)
    {
        var f = te.Format!;
        if (f != "iso" && !f.StartsWith('%')) return false;
        if (!t.IsError && !t.IsAny && !Types.IsTemporalPoint(t))
        {
            Report(Templates.BadFormat(te.Span, f, $"is a date/time pattern, but this is a '{N(t)}'"));
            return true;
        }
        if (f != "iso" && !Temporal.TryFormatPattern(f, 0, 0, out _, out var problem))
            Report(Templates.BadFormat(te.Span, f, problem!));
        return true;
    }
}
