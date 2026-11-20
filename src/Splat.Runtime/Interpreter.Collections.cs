using Splat.Core;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Runtime;

// Phase 7: methods, indexing, slicing, index assignment and value semantics for List, Set, Map and tuples.
public sealed partial class Interpreter
{
    private static Value Got(Value v) => Value.Enum(new EnumValue("Maybe", "Got", [v]));
    private static Value Nada() => Value.Enum(new EnumValue("Maybe", "Nada", []));

    /// <summary>
    /// Until ownership arrives in Phase 17, collections have value semantics at the points where aliasing would show:
    /// binding, assignment, and storing into another collection copy the value (deeply) unless the expression just made a
    /// fresh one. So <c>var ys = xs; ys.push(1)</c> never changes <c>xs</c>.
    /// </summary>
    private Value EvalOwned(Expr e, Env env) => Own(e, Eval(e, env));

    private static Value Own(Expr e, Value v)
    {
        if (v.Kind == VKind.Tensor) return IsFreshTensor(e) ? v : Value.OfTensor(v.AsTensor.Clone());
        if (v.Kind is not (VKind.List or VKind.Set or VKind.Map or VKind.Tuple or VKind.Enum)) return v;
        return IsFresh(e) ? v : DeepClone(v);
    }

    private static bool IsFresh(Expr e) => e is ListLiteral or PairExpr or BinaryExpr
        or CallExpr { Callee: NameExpr { Name: "split" or "lines" or "enumerate" or "zip" or "set" or "map" or "rev" } };

    internal static Value DeepClone(Value v)
    {
        switch (v.Kind)
        {
            case VKind.List:
            {
                var l = new SplatList();
                foreach (var item in v.AsList.Items) l.Items.Add(DeepClone(item));
                return Value.List(l);
            }
            case VKind.Set:
            {
                var s = new SplatSet();
                foreach (var item in v.AsSet.Items) s.Add(DeepClone(item));
                return Value.Set(s);
            }
            case VKind.Map:
            {
                var m = new SplatMap();
                foreach (var (k, val) in v.AsMap.Items) m.Set(DeepClone(k), DeepClone(val));
                return Value.Map(m);
            }
            case VKind.Tuple:
                return Value.Tuple(v.AsTuple.Select(DeepClone).ToArray());
            case VKind.Enum:
                return v.AsEnum.PayloadCount == 0 ? v : Value.Enum(new EnumValue(v.AsEnum.EnumName, v.AsEnum.VariantName, v.AsEnum.Payloads.Select(DeepClone).ToArray()));
            case VKind.Tensor:
                return Value.OfTensor(v.AsTensor.Clone());
            default:
                return v;
        }
    }

    private static string KeyText(Value k) => k.Kind == VKind.Txt ? "\"" + k.AsString + "\"" : Formatter.Display(k);

    private static string MissingKeyText(Value k) => KeyText(k);

    private void CheckHashable(Value v, SourceSpan span)
    {
        bool ok = v.Kind switch
        {
            VKind.F16 or VKind.F32 or VKind.F64 or VKind.Unit or VKind.List or VKind.Set or VKind.Map or VKind.Enum
                or VKind.Range or VKind.Func or VKind.Chain or VKind.Object or VKind.Dur or VKind.Cal => false,
            VKind.Tuple => v.AsTuple.All(x => IsHashableValue(x)),
            _ => true,
        };
        if (!ok) throw Fail(Templates.NotHashable(span, v.TypeName));
    }

    private static bool IsHashableValue(Value v) => v.Kind switch
    {
        VKind.F16 or VKind.F32 or VKind.F64 or VKind.Unit or VKind.List or VKind.Set or VKind.Map or VKind.Enum
            or VKind.Range or VKind.Func or VKind.Chain or VKind.Object or VKind.Dur or VKind.Cal => false,
        VKind.Tuple => v.AsTuple.All(IsHashableValue),
        _ => true,
    };

    // ---- set(...) and map(...) ----

    private Value MakeSet(CallExpr c, Env env)
    {
        var set = new SplatSet();
        foreach (var a in c.Args)
        {
            var item = EvalOwned(a.Value, env);
            CheckHashable(item, a.Value.Span);
            set.Add(item);
        }
        return Value.Set(set);
    }

    private Value MakeMap(CallExpr c, Env env)
    {
        var map = new SplatMap();
        foreach (var a in c.Args)
        {
            var pair = (PairExpr)a.Value;
            var key = EvalOwned(pair.Key, env);
            CheckHashable(key, pair.Key.Span);
            map.Set(key, EvalOwned(pair.Value, env));
        }
        return Value.Map(map);
    }

    // ---- indexing ----

    private Value EvalIndex(IndexExpr ix, Env env)
    {
        if (_info.TensorLiteral(ix) is { } lit && _info.TypeOf(ix) is TensorSType lt) return EvalTensorList(lit, lt, env);
        var target = Eval(ix.Target, env);
        if (target.Kind == VKind.Tensor) return TensorIndex(target.AsTensor, ix, env);
        var idx = Eval(ix.Indices[0], env);
        switch (target.Kind)
        {
            case VKind.Object:
                if (target.AsObj is JsonObj jo) return IndexJson(jo, idx, ix.Span);
                CheckAlive(target.AsObj, ix.Target);
                return CallUserMethod(target, "op []", [idx], ix.Span);
            case VKind.List when idx.Kind == VKind.Range:
            {
                var items = target.AsList.Items;
                var slice = new SplatList();
                foreach (var i in idx.AsRange.Items())
                {
                    long n = (long)i.AsInt128;
                    if (n < 0 || n >= items.Count) throw Fail(Templates.IndexOutOfRange(ix.Span, n, items.Count));
                    slice.Items.Add(DeepClone(items[(int)n]));
                }
                return Value.List(slice);
            }
            case VKind.List:
            {
                var items = target.AsList.Items;
                var i = idx.AsInt128;
                if (i < 0 || i >= items.Count) throw Fail(Templates.IndexOutOfRange(ix.Span, (long)Int128.Clamp(i, long.MinValue, long.MaxValue), items.Count));
                return items[(int)i];
            }
            case VKind.Map:
                if (!target.AsMap.TryGet(idx, out var v)) throw Fail(Templates.KeyNotFound(ix.Span, KeyText(idx)));
                return v;
            case VKind.Txt:
            {
                var runes = target.AsString.EnumerateRunes().ToList();
                var i = idx.AsInt128;
                if (i < 0 || i >= runes.Count) throw Fail(Templates.IndexOutOfRange(ix.Span, (long)Int128.Clamp(i, long.MinValue, long.MaxValue), runes.Count));
                return Value.Char(runes[(int)i].Value);
            }
            default:
                throw Fail(Templates.Internal(ix.Span, $"cannot index {target.TypeName}"));
        }
    }

    /// <summary><c>xs[i] = v</c>, <c>m[k] = v</c> and compound forms. Assigning to a new map key inserts it.</summary>
    private void ExecIndexAssign(AssignStmt a, IndexExpr ix, Env env)
    {
        var target = Eval(ix.Target, env);
        if (target.Kind == VKind.Tensor) { ExecTensorAssign(a, ix, target.AsTensor, env); return; }
        var idx = Eval(ix.Indices[0], env);
        var rhs = a.Op == TokenKind.Assign ? EvalOwned(a.Value, env) : Eval(a.Value, env);

        if (target.Kind == VKind.List)
        {
            var items = target.AsList.Items;
            var i = idx.AsInt128;
            if (i < 0 || i >= items.Count) throw Fail(Templates.IndexOutOfRange(ix.Span, (long)Int128.Clamp(i, long.MinValue, long.MaxValue), items.Count));
            items[(int)i] = Combine(a, items[(int)i], rhs);
            return;
        }
        if (target.Kind == VKind.Map)
        {
            CheckHashable(idx, ix.Indices[0].Span);
            if (a.Op == TokenKind.Assign) { target.AsMap.Set(idx, rhs); return; }
            if (!target.AsMap.TryGet(idx, out var cur)) throw Fail(Templates.KeyNotFound(ix.Span, KeyText(idx)));
            target.AsMap.Set(idx, Combine(a, cur, rhs));
            return;
        }
        throw Fail(Templates.Internal(ix.Span, $"cannot assign into {target.TypeName}"));
    }

    private Value Combine(AssignStmt a, Value current, Value rhs) => a.Op == TokenKind.Assign ? rhs
        : Binary(BaseOpOf(a.Op) ?? throw Fail(Templates.Internal(a.Span, $"assignment operator {a.Op}")), current, rhs, a.Span);

    // ---- methods of the built-in collections ----

    private static bool IsBuiltinCollectionMethod(VKind kind, string name) => (kind, name) is
        (VKind.List, "push" or "pop" or "insert" or "remove" or "len") or (VKind.Set, "add" or "remove" or "len")
        or (VKind.Map, "get" or "remove" or "len") or (VKind.Txt, "upper" or "lower" or "trim" or "len" or "split" or "lines");

    private Value CallMethod(CallExpr c, MemberExpr m, Env env)
    {
        if (m.Target is NameExpr { Name: "super" }) return CallSuper(c, m, env);
        if (m.Target is NameExpr tn && env.Find(tn.Name) is null && NativeMembers.StaticsOf(tn.Name) is not null)
            return CallStatic(tn.Name, m.Name, c, env);

        var recv = Eval(m.Target, env);
        if (recv.Kind == VKind.Object) return CallObjectMethod(c, m, recv, env);
        if (recv.Kind == VKind.Tensor && m.Name is "shape" or "len" && FindUserMethod(recv, m.Name) is null)
        {
            var tt = recv.AsTensor;
            if (m.Name == "len") return Value.Int(VKind.Usize, tt.Rank == 0 ? 1 : tt.Shape[0]);
            var dims = new SplatList();
            foreach (var d in tt.Shape) dims.Items.Add(Value.Int(VKind.Usize, d));
            return Value.List(dims);
        }
        if (recv.IsTemporal || recv.Kind is VKind.Date or VKind.Time or VKind.Stamp)
        {
            if (FindUserMethod(recv, m.Name) is null) return TemporalMethod(recv, m.Name, c.Span);
        }
        if (!IsBuiltinCollectionMethod(recv.Kind, m.Name)) return CallExtMethod(c, m, recv, env);
        var args = c.Args.Select(a => EvalOwned(a.Value, env)).ToArray();

        switch (recv.Kind, m.Name)
        {
            case (VKind.Txt, "upper"): return Value.Str(recv.AsString.ToUpperInvariant());
            case (VKind.Txt, "lower"): return Value.Str(recv.AsString.ToLowerInvariant());
            case (VKind.Txt, "trim"): return Value.Str(recv.AsString.Trim());
            case (VKind.Txt, "len"): return Value.Int(VKind.Usize, recv.AsString.EnumerateRunes().Count());
            case (VKind.Txt, "split"):
            {
                var parts = new SplatList();
                foreach (var p in recv.AsString.Split(args[0].AsString, StringSplitOptions.None)) parts.Items.Add(Value.Str(p));
                return Value.List(parts);
            }
            case (VKind.Txt, "lines"):
            {
                var ls = new SplatList();
                foreach (var l in recv.AsString.Split('\n')) ls.Items.Add(Value.Str(l.TrimEnd('\r')));
                return Value.List(ls);
            }
            case (VKind.List, "len"): return Value.Int(VKind.Usize, recv.AsList.Items.Count);
            case (VKind.Set, "len"): return Value.Int(VKind.Usize, recv.AsSet.Count);
            case (VKind.Map, "len"): return Value.Int(VKind.Usize, recv.AsMap.Count);
            case (VKind.List, "push"):
                recv.AsList.Items.Add(args[0]);
                return Value.Unit;
            case (VKind.List, "pop"):
            {
                var items = recv.AsList.Items;
                if (items.Count == 0) return Nada();
                var last = items[^1];
                items.RemoveAt(items.Count - 1);
                return Got(last);
            }
            case (VKind.List, "insert"):
            {
                var items = recv.AsList.Items;
                var i = args[0].AsInt128;
                if (i < 0 || i > items.Count) throw Fail(Templates.IndexOutOfRange(c.Span, (long)Int128.Clamp(i, long.MinValue, long.MaxValue), items.Count + 1));
                items.Insert((int)i, args[1]);
                return Value.Unit;
            }
            case (VKind.List, "remove"):
            {
                var items = recv.AsList.Items;
                var i = args[0].AsInt128;
                if (i < 0 || i >= items.Count) throw Fail(Templates.IndexOutOfRange(c.Span, (long)Int128.Clamp(i, long.MinValue, long.MaxValue), items.Count));
                var removed = items[(int)i];
                items.RemoveAt((int)i);
                return removed;
            }
            case (VKind.Set, "add"):
                CheckHashable(args[0], c.Args[0].Value.Span);
                recv.AsSet.Add(args[0]);
                return Value.Unit;
            case (VKind.Set, "remove"):
                return Value.Bool(recv.AsSet.Remove(args[0]));
            case (VKind.Map, "get"):
                return recv.AsMap.TryGet(args[0], out var got) ? Got(got) : Nada();
            case (VKind.Map, "remove"):
            {
                if (!recv.AsMap.TryGet(args[0], out var old)) return Nada();
                recv.AsMap.Remove(args[0]);
                return Got(old);
            }
            default:
                throw Fail(Templates.Internal(c.Span, $"method '{m.Name}' on {recv.TypeName}"));
        }
    }

    // ---- equality and list concatenation ----

    private Value CompoundBinary(TokenKind op, Value a, Value b, SourceSpan span)
    {
        switch (op)
        {
            case TokenKind.EqEq: return Value.Bool(ValueEquality.Equal(a, b));
            case TokenKind.BangEq: return Value.Bool(!ValueEquality.Equal(a, b));
            case TokenKind.Plus when a.Kind == VKind.List && b.Kind == VKind.List:
            {
                var joined = new SplatList();
                foreach (var x in a.AsList.Items) joined.Items.Add(DeepClone(x));
                foreach (var x in b.AsList.Items) joined.Items.Add(DeepClone(x));
                return Value.List(joined);
            }
            default:
                throw Fail(Templates.Internal(span, $"operator {op} on {a.TypeName}"));
        }
    }
}

/// <summary>Structural equality for every value kind (what <c>==</c> means on lists, tuples, enums, sets and maps).</summary>
public static class ValueEquality
{
    public static bool Equal(Value a, Value b)
    {
        if (a.IsInt && b.IsInt) return a.AsInt128 == b.AsInt128;
        if (a.Kind != b.Kind) return false;
        switch (a.Kind)
        {
            case VKind.F16 or VKind.F32 or VKind.F64: return a.AsDouble == b.AsDouble; // NaN is not equal to itself
            case VKind.Bool or VKind.Char or VKind.Date or VKind.Time or VKind.Stamp or VKind.Dur or VKind.Cal: return a.Bits == b.Bits;
            case VKind.Object: return ReferenceEquals(a.Ref, b.Ref);
            case VKind.Tensor: return Tensor.ValuesEqual(a.AsTensor, b.AsTensor);
            case VKind.Unit: return true;
            case VKind.Txt: return string.Equals(a.AsString, b.AsString, StringComparison.Ordinal);
            case VKind.Tuple:
                return a.AsTuple.Length == b.AsTuple.Length && a.AsTuple.Zip(b.AsTuple).All(p => Equal(p.First, p.Second));
            case VKind.List:
                return a.AsList.Items.Count == b.AsList.Items.Count && a.AsList.Items.Zip(b.AsList.Items).All(p => Equal(p.First, p.Second));
            case VKind.Set:
                return a.AsSet.Count == b.AsSet.Count && a.AsSet.Items.All(x => b.AsSet.Contains(x));
            case VKind.Map:
                return a.AsMap.Count == b.AsMap.Count
                    && a.AsMap.Items.All(p => b.AsMap.TryGet(p.Key, out var v) && Equal(p.Val, v));
            case VKind.Enum:
            {
                var x = a.AsEnum; var y = b.AsEnum;
                return x.EnumName == y.EnumName && x.VariantName == y.VariantName
                    && x.Payloads.Length == y.Payloads.Length && x.Payloads.Zip(y.Payloads).All(p => Equal(p.First, p.Second));
            }
            default: return false;
        }
    }
}
