using Splat.Core;
using Splat.Syntax;

namespace Splat.Semantics;

// Phases 12-14: tensor types, literals, constructors, indexing, operators (elementwise, matrix product, solve, transpose),
// the math prelude, norms, reductions and integrals. The interpreter side lives in Interpreter.Tensors.cs.
public sealed partial class TypeChecker
{
    // ---- types ----

    private SType ResolveTensorType(TensorType tt)
    {
        SType elem;
        if (tt.Element is NamedType { Args.Count: 0 } en && Types.TensorOnlyElems.TryGetValue(en.Name, out var only)) elem = only;
        else
        {
            elem = ResolveType(tt.Element);
            if (elem.IsError) return SType.Error;
            if (!(elem is PrimType p && (Types.IsNumeric(p.Kind) || p.Kind == Prim.Bool)))
            {
                Report(Templates.TypeMismatch(tt.Element.Span, N(elem), "a number type (f16, f32, f64, i32, ...) as the tensor element"));
                return SType.Error;
            }
        }
        var dims = tt.Dims.Select(ResolveDim).ToList();
        if (dims.Count == 0)
        {
            Report(Templates.UnexpectedToken(tt.Span, "[]", "at least one dimension, as in f32[3] (a single number is just 'f32')"));
            return SType.Error;
        }
        return new TensorSType(elem, dims);
    }

    private static SDim ResolveDim(Dim d) => d switch
    {
        ConstDim c => SDim.Const((long)Math.Min(c.Value, long.MaxValue)),
        VarDim v => SDim.Var(v.Name),
        _ => SDim.Dyn,
    };

    /// <summary>A tensor hint lends its element type to the scalar literals inside an expression (<c>A * 2.0</c> with an f64 tensor).</summary>
    private static SType? ScalarHint(SType? hint) => hint is TensorSType ts ? ts.Elem : hint;

    private static string ShapeDifference(TensorSType a, TensorSType e)
    {
        for (int i = 0; i < a.Rank && i < e.Rank; i++)
            if (Shapes.Unify(a.Dims[i], e.Dims[i], bind: false) == Shapes.Rel.Conflict)
                return $"axis {i} is {Shapes.R(a.Dims[i]).Text} but {Shapes.R(e.Dims[i]).Text} was expected";
        return a.Rank != e.Rank ? $"rank {a.Rank} versus rank {e.Rank}" : "they differ";
    }

    /// <summary>When a <c>?</c> dimension flows into a slot that needs a known size, the interpreter checks it at run time.</summary>
    private void NoteGuard(Expr e, SType actual, SType expected)
    {
        if (actual is TensorSType a && expected is TensorSType x && Shapes.NeedsGuard(a, x))
            _info.SetGuard(e, Shapes.Resolve(x));
    }

    /// <summary>Binds the <c>$R</c>, <c>$C</c> of an <c>extend f32[$R, $C]</c> block to the dimensions of one receiver.</summary>
    private static bool TryBindReceiver(TensorSType target, TensorSType recv, out Dictionary<string, SType> map)
    {
        map = new Dictionary<string, SType>(StringComparer.Ordinal);
        if (!target.Elem.Equals(recv.Elem) || target.Rank != recv.Rank) return false;
        for (int i = 0; i < target.Rank; i++)
        {
            var t = Shapes.R(target.Dims[i]);
            var r = Shapes.R(recv.Dims[i]);
            if (t is VarSDim v)
            {
                if (map.TryGetValue(v.Name, out var prev))
                {
                    if (Shapes.Unify(((DimBindingSType)prev).Dim, r, bind: false) == Shapes.Rel.Conflict) return false;
                }
                else map[v.Name] = new DimBindingSType(r);
            }
            else if (Shapes.Unify(t, r, bind: false) == Shapes.Rel.Conflict) return false;
        }
        return true;
    }

    private bool RuntimeElemOrReport(SType elem, SourceSpan span, string what)
    {
        if (Types.IsRuntimeTensorElem(elem)) return true;
        Report(Templates.NotYetBuilt(span, $"{what} of '{N(elem)}' tensors", 29));
        return false;
    }

    // ---- literals ----

    private static int NestingDepth(ListLiteral l) => 1 + (l.Items.Count > 0 && l.Items[0] is ListLiteral inner ? NestingDepth(inner) : 0);

    private SType CheckPrefixedTensorLiteral(IndexExpr ix, PrimType elem, Scope scope)
    {
        if (ix.Indices.Any(i => i is FullSlice))
        {
            Report(Templates.UnexpectedToken(ix.Span, ":", "elements in a tensor literal, as in f32[1, 2, 3]"));
            return SType.Error;
        }
        // `f32[a, b, c]` is the list literal `[a, b, c]` typed as a tensor of f32.
        var lit = new ListLiteral(ix.Indices, ix.Span);
        if (!Types.IsFloat(elem))
        {
            return NotYet(ix.Span, $"literals of '{elem.Name}' tensors", 29);
        }
        _info.SetTensorLiteral(ix, lit);
        var seed = new TensorSType(elem, Enumerable.Repeat(SDim.Dyn, NestingDepth(lit)).ToList());
        var result = CheckTensorList(lit, seed, scope);
        if (!result.IsError) _info.Set(lit, result);
        return result;
    }

    private static bool IsIntLiteral(Expr e) => e is IntLiteral or UnaryExpr { Op: TokenKind.Minus, Operand: IntLiteral };

    /// <summary>Element of a tensor literal: an integer literal is accepted for a float tensor (<c>f32[1, 2, 3]</c>).</summary>
    private SType InferTensorElement(Expr item, SType elem, Scope scope)
    {
        if (IsIntLiteral(item) && Types.IsFloat(elem))
        {
            if (item is UnaryExpr u) _info.Set(u.Operand, elem);
            _info.Set(item, elem);
            return elem;
        }
        return Infer(item, scope, elem);
    }

    private SType CheckTensorList(ListLiteral ll, TensorSType hint, Scope scope)
    {
        if (!Types.IsRuntimeTensorElem(hint.Elem))
        {
            foreach (var item in ll.Items) Infer(item, scope);
            return NotYet(ll.Span, $"literals of '{N(hint.Elem)}' tensors", 29);
        }
        if (hint.Rank == 0) return SType.Error;
        var inner = hint.Rank == 1 ? null : hint.WithDims(hint.Dims.Skip(1).ToList());
        TensorSType? innerSeen = null;
        bool failed = false;
        foreach (var item in ll.Items)
        {
            if (inner is null)
            {
                var it = InferTensorElement(item, hint.Elem, scope);
                if (it.IsError) { failed = true; continue; }
                if (!Types.Compatible(it, hint.Elem)) { Mismatch(item.Span, it, hint.Elem); failed = true; }
                continue;
            }
            var t = Infer(item, scope, inner);
            if (t.IsError) { failed = true; continue; }
            if (t is not TensorSType rt || rt.Rank != inner.Rank || !rt.Elem.Equals(inner.Elem))
            {
                Mismatch(item.Span, t, inner);
                failed = true;
                continue;
            }
            if (innerSeen is null) { innerSeen = rt; continue; }
            var merged = new List<SDim>();
            for (int i = 0; i < rt.Rank; i++)
            {
                if (Shapes.Unify(innerSeen.Dims[i], rt.Dims[i], bind: false) == Shapes.Rel.Conflict)
                {
                    Report(Templates.GeometryCrime(item.Span,
                        $"Tried to stack a '{N(rt)}' row onto '{N(innerSeen)}' rows. The rows of a tensor literal must all have the same shape.",
                        "Make every row the same length, or build a List instead."));
                    failed = true;
                    break;
                }
                merged.Add(Shapes.IsDyn(innerSeen.Dims[i]) ? rt.Dims[i] : innerSeen.Dims[i]);
            }
            if (merged.Count == rt.Rank) innerSeen = rt.WithDims(merged);
        }
        if (failed) return SType.Error;
        var dims = new List<SDim> { SDim.Const(ll.Items.Count) };
        if (inner is not null) dims.AddRange((innerSeen ?? inner).Dims.Select(d => ll.Items.Count == 0 ? inner.Dims[dims.Count - 1] : d));
        return new TensorSType(hint.Elem, dims);
    }

    // ---- arithmetic ----

    private SType TensorArithmetic(TokenKind op, SType l, SType r, SourceSpan span, string opText)
    {
        var lt = l as TensorSType;
        var rt = r as TensorSType;
        var elem = (lt ?? rt)!.Elem;

        SType Bad(string fix)
        {
            Report(Templates.BadOperator(span, opText, N(l), N(r), fix));
            return SType.Error;
        }

        if (!RuntimeElemOrReport(elem, span, $"'{opText}' on tensors")) return SType.Error;

        // Element types must agree exactly: there is no implicit f32/f64 mixing.
        if (lt is not null && rt is not null && !lt.Elem.Equals(rt.Elem))
            return Bad($"The element types differ ('{N(lt.Elem)}' versus '{N(rt.Elem)}'). Convert one side explicitly, for example {N(lt.Elem)}(B).");
        var scalar = lt is null ? l : rt is null ? r : null;
        bool intPower = op == TokenKind.StarStar && lt is not null && scalar is not null && Types.IsInt(scalar); // A ** 2
        if (scalar is not null && !scalar.IsAny && !scalar.Equals(elem) && !intPower)
            return Bad($"A scalar combined with a '{N(elem)}' tensor must itself be '{N(elem)}' (no implicit conversions). Write {N(elem)}(x) or a literal such as 2.0.");

        switch (op)
        {
            case TokenKind.At: return MatMulType(lt, rt, span, l, r);
            case TokenKind.Backslash: return SolveType(lt, rt, span);
            case TokenKind.MiddleDot: return DotType(lt, rt, span);
        }
        if (scalar is not null && op is TokenKind.At or TokenKind.Backslash or TokenKind.MiddleDot)
            return Bad("This operator needs a tensor on both sides.");

        switch (op)
        {
            case TokenKind.Plus or TokenKind.Minus:
                if (lt is not null && rt is not null) return SameShape(lt, rt, span, opText);
                return lt ?? rt!;

            case TokenKind.Star or TokenKind.Slash:
                if (lt is not null && rt is not null)
                    return Bad(op == TokenKind.Star
                        ? "A tensor times a tensor is ambiguous: did you mean '.*' (elementwise) or '@' (matrix product)?"
                        : "A tensor divided by a tensor is ambiguous: did you mean './' (elementwise)?");
                return lt ?? rt!;

            case TokenKind.StarStar or TokenKind.DotPlus or TokenKind.DotMinus or TokenKind.DotStar or TokenKind.DotSlash:
                if (lt is not null && rt is not null) return BroadcastType(lt, rt, span, opText);
                return lt ?? rt!;

            default:
                return Bad("Tensors support + - * / (with a scalar), .+ .- .* ./ (elementwise, broadcasting), ** (elementwise power), @, \\ and ·.");
        }
    }

    /// <summary><c>+</c> and <c>-</c> between two tensors need identical shapes (use <c>.+</c> to broadcast).</summary>
    private SType SameShape(TensorSType a, TensorSType b, SourceSpan span, string opText)
    {
        if (a.Rank != b.Rank)
        {
            Report(Templates.GeometryCrime(span,
                $"Tried to '{opText}' a '{N(a)}' with a '{N(b)}'. One has {a.Rank} axes, the other {b.Rank}.",
                $"'{opText}' needs identical shapes; use '.{opText}' to broadcast, or reshape one side."));
            return SType.Error;
        }
        var dims = new List<SDim>();
        for (int i = 0; i < a.Rank; i++)
        {
            if (Shapes.Unify(a.Dims[i], b.Dims[i], bind: false) == Shapes.Rel.Conflict)
            {
                Report(Templates.GeometryCrime(span,
                    $"Tried to '{opText}' a '{N(a)}' with a '{N(b)}'. Axis {i} is {Shapes.R(a.Dims[i]).Text} on the left and {Shapes.R(b.Dims[i]).Text} on the right.",
                    $"'{opText}' needs identical shapes; use '.{opText}' to broadcast, or reshape one side."));
                return SType.Error;
            }
            dims.Add(Shapes.IsDyn(a.Dims[i]) ? Shapes.R(b.Dims[i]) : Shapes.R(a.Dims[i]));
        }
        return a.WithDims(dims);
    }

    private SType BroadcastType(TensorSType a, TensorSType b, SourceSpan span, string opText)
    {
        if (!Shapes.TryBroadcast(a.Dims, b.Dims, out var dims, out var x, out var y, out int axis))
        {
            Report(Templates.GeometryCrime(span,
                $"Tried to '{opText}' a '{N(a)}' with a '{N(b)}'. Counting from the right, axis {axis} has {x!.Text} against {y!.Text}, and neither is 1.",
                "Broadcasting lines the shapes up on the right: each pair of dimensions must be equal, or one of them must be 1."));
            return SType.Error;
        }
        return a.WithDims(dims);
    }

    private SType CheckTensorOperator(BinaryExpr b, SType l, SType r)
    {
        if (l.IsError || r.IsError) return SType.Error;
        if (l.IsAny || r.IsAny) return SType.Any;
        string op = OpText(b.Op);
        if (l is not TensorSType && r is not TensorSType)
        {
            Report(Templates.BadOperator(b.Span, op, N(l), N(r), b.Op switch
            {
                TokenKind.At => "'@' is the matrix product of two tensors. For numbers use '*'.",
                TokenKind.Backslash => "'\\' solves A x = b for tensors. For numbers use '/'.",
                TokenKind.MiddleDot => "'·' is the dot product of two vectors. For numbers use '*'.",
                _ => $"'{op}' works on tensors (and a scalar). For plain numbers use the ordinary operator.",
            }));
            return SType.Error;
        }
        return TensorArithmetic(b.Op, l, r, b.Span, op);
    }

    private SType MatMulType(TensorSType? lt, TensorSType? rt, SourceSpan span, SType l, SType r)
    {
        if (lt is null || rt is null)
        {
            Report(Templates.BadOperator(span, "@", N(l), N(r), "A matrix product needs a tensor on both sides; to scale a tensor use '*'."));
            return SType.Error;
        }
        var res = Shapes.MatMul(lt, rt);
        if (res.Problem is not null)
        {
            Report(Templates.GeometryCrime(span, $"Tried to '@' a '{N(lt)}' with a '{N(rt)}': {res.Problem}.",
                "Make the batch dimensions equal, or equal to 1."));
            return SType.Error;
        }
        if (res.K1 is not null && res.K2 is not null && res.Type is null && !res.ScalarResult)
        {
            // Only suggest the transpose when it would really fix the product.
            bool tipB = lt.Rank >= 2 && rt.Rank >= 2 && Shapes.MatMul(lt, Shapes.Transpose(rt)) is { Problem: null, K1: null or not null } fb && fb.Type is not null;
            bool tipA = lt.Rank >= 2 && rt.Rank >= 2 && Shapes.MatMul(Shapes.Transpose(lt), rt) is { Problem: null } fa && fa.Type is not null;
            Report(Templates.ShapeMismatch(span, N(lt), N(rt), Shapes.R(res.K1).Text, Shapes.R(res.K2).Text, tipA || tipB));
            return SType.Error;
        }
        if (res.ScalarResult) return lt.Elem;
        return res.Type!;
    }

    private SType SolveType(TensorSType? a, TensorSType? b, SourceSpan span)
    {
        if (a is null || b is null)
        {
            Report(Templates.BadOperator(span, "\\", a is null ? "scalar" : N(a), b is null ? "scalar" : N(b), "'\\' solves A x = b: the left side is a matrix and the right side a vector or matrix."));
            return SType.Error;
        }
        if (a.Rank != 2 || b.Rank is not (1 or 2))
        {
            Report(Templates.GeometryCrime(span, $"Tried to solve '{N(a)}' \\ '{N(b)}'. The left side must be a matrix (2 axes) and the right a vector or matrix.",
                "Write A \\ b with A::T[M, N] and b::T[M] (or T[M, K])."));
            return SType.Error;
        }
        var rows = b.Dims[0];
        if (Shapes.Unify(a.Dims[0], rows, bind: false) == Shapes.Rel.Conflict)
        {
            Report(Templates.GeometryCrime(span,
                $"Tried to solve '{N(a)}' \\ '{N(b)}'. The matrix has {Shapes.R(a.Dims[0]).Text} rows but the right-hand side has {Shapes.R(rows).Text}.",
                "A \\ b needs one equation (row of A) per entry of b."));
            return SType.Error;
        }
        var dims = new List<SDim> { a.Dims[1] };
        if (b.Rank == 2) dims.Add(b.Dims[1]);
        return a.WithDims(dims);
    }

    private SType DotType(TensorSType? a, TensorSType? b, SourceSpan span)
    {
        if (a is null || b is null || a.Rank != 1 || b.Rank != 1)
        {
            Report(Templates.BadOperator(span, "·", a is null ? "scalar" : N(a), b is null ? "scalar" : N(b), "'·' is the dot product of two vectors. Use '@' for matrices."));
            return SType.Error;
        }
        if (Shapes.Unify(a.Dims[0], b.Dims[0], bind: false) == Shapes.Rel.Conflict)
        {
            Report(Templates.ShapeMismatch(span, N(a), N(b), Shapes.R(a.Dims[0]).Text, Shapes.R(b.Dims[0]).Text, suggestTranspose: false));
            return SType.Error;
        }
        return a.Elem;
    }

    private SType CheckTranspose(PostfixExpr p, Scope scope)
    {
        var t = Infer(p.Operand, scope);
        if (t.IsError || t.IsAny) return t;
        if (t is TensorSType ts) return Shapes.Transpose(ts); // '' is the same as ' (no complex numbers in v0.2)
        Report(Templates.BadOperator(p.Span, p.Op == TokenKind.ConjTranspose ? "''" : "'", N(t), null, "Only tensors can be transposed."));
        return SType.Error;
    }

    // ---- indexing ----

    private SType InferTensorIndex(IndexExpr ix, TensorSType t, Scope scope)
    {
        if (ix.Indices.Count > t.Rank)
        {
            foreach (var i in ix.Indices) if (i is not FullSlice) Infer(i, scope);
            Report(Templates.GeometryCrime(ix.Span, $"Tried to index a '{N(t)}' with {ix.Indices.Count} indices, but it only has {t.Rank} axes.",
                "Use at most one index per axis."));
            return SType.Error;
        }
        var dims = new List<SDim>();
        bool failed = false;
        for (int i = 0; i < ix.Indices.Count; i++)
        {
            var idx = ix.Indices[i];
            if (idx is FullSlice) { dims.Add(t.Dims[i]); continue; }
            bool isRange = idx is BinaryExpr { Op: TokenKind.DotDot or TokenKind.DotDotEq } or StepExpr;
            var it = Infer(idx, scope, isRange ? new RangeSType(Types.Usize) : Types.Usize);
            if (it.IsError) { failed = true; continue; }
            if (it is RangeSType { Elem: var re } && (Types.IsInt(re) || re.IsAny)) dims.Add(RangeLength(idx));
            else if (Types.IsInt(it) || it.IsAny) { /* an integer index removes the axis */ }
            else { Report(Templates.UnexpectedToken(idx.Span, $"a '{N(it)}' index", "an integer index, a range such as 0..3, or ':' for a whole axis")); failed = true; }
        }
        if (failed) return SType.Error;
        dims.AddRange(t.Dims.Skip(ix.Indices.Count));
        return dims.Count == 0 ? t.Elem : new TensorSType(t.Elem, dims);
    }

    /// <summary>The static length of an index range when its bounds (and step) are literals, else <c>?</c>.</summary>
    private static SDim RangeLength(Expr idx)
    {
        Expr range = idx;
        Int128 step = 1;
        if (idx is StepExpr st)
        {
            if (!TryConstInt(st.Step, out step) || step <= 0) return SDim.Dyn;
            range = st.Range;
        }
        if (range is BinaryExpr { Op: var op } be && TryConstInt(be.Left, out var lo) && TryConstInt(be.Right, out var hi))
        {
            Int128 span = hi - lo + (op == TokenKind.DotDotEq ? 1 : 0);
            if (span <= 0) return SDim.Const(0);
            return SDim.Const((long)((span + step - 1) / step));
        }
        return SDim.Dyn;
    }

    private SType CheckTensorIndex(IndexExpr ix, TensorSType t, Scope scope) => InferTensorIndex(ix, t, scope);

    private void CheckTensorIndexAssign(AssignStmt a, IndexExpr ix, TensorSType t, Scope scope)
    {
        RequireMutable(ix.Target, scope);
        var vt = InferTensorIndex(ix, t, scope);
        _info.Set(ix, vt);
        if (vt.IsError) { Infer(a.Value, scope); return; }

        if (a.Op == TokenKind.Assign)
        {
            var rt = Infer(a.Value, scope, vt);
            if (rt.IsError || rt.IsAny) return;
            if (vt is TensorSType view)
            {
                if (rt is TensorSType src)
                {
                    if (!src.Elem.Equals(view.Elem)) { Mismatch(a.Value.Span, rt, vt); return; }
                    bool fits = src.Rank <= view.Rank
                        && Shapes.TryBroadcast(src.Dims, view.Dims, out var res, out _, out _, out _)
                        && res.Count == view.Rank && res.Zip(view.Dims).All(p => Shapes.Unify(p.First, p.Second, bind: false) != Shapes.Rel.Conflict);
                    if (!fits)
                        Report(Templates.GeometryCrime(a.Value.Span, $"Tried to assign a '{N(rt)}' into a '{N(vt)}' slice. The shapes do not broadcast.",
                            "Assign a scalar, or a tensor whose shape broadcasts onto the slice."));
                }
                else if (!Types.Compatible(rt, view.Elem)) Mismatch(a.Value.Span, rt, view.Elem);
            }
            else if (!Types.Compatible(rt, vt)) Mismatch(a.Value.Span, rt, vt);
            return;
        }
        var baseOp = CompoundBase(a.Op)!.Value;
        var rhs = Infer(a.Value, scope, vt);
        var result = ArithmeticType(baseOp, vt, rhs, a.Span, OpText(baseOp) + "=");
        if (!Types.Compatible(result, vt)) Mismatch(a.Value.Span, result, vt);
    }

    private SType? CheckTensorMethod(CallExpr c, MemberExpr m, TensorSType t, Scope scope)
    {
        if (m.Name is not ("shape" or "len")) return null;
        foreach (var a in c.Args) Infer(a.Value, scope);
        if (c.Args.Count != 0) Report(Templates.WrongArgCount(c.Span, $"{N(t)}.{m.Name}", 0, c.Args.Count));
        SType ret = m.Name == "shape" ? new ListSType(Types.Usize) : Types.Usize;
        _info.Set(m, new FuncSType([], ret));
        return ret;
    }

    // ---- prelude: constructors, math, reductions, csv ----

    private bool Arity(CallExpr c, string name, Scope scope, int min, int max)
    {
        if (c.Args.Count >= min && c.Args.Count <= max) return true;
        foreach (var a in c.Args) Infer(a.Value, scope);
        Report(Templates.WrongArgCount(c.Span, name, min, c.Args.Count));
        return false;
    }

    private SType CheckTensorBuiltin(CallExpr c, NameExpr name, Scope scope, SType? hint)
    {
        if (c.Args.Any(a => a.Name is not null))
            Report(Templates.UnexpectedToken(c.Args.First(a => a.Name is not null).Span, "a named argument", $"positional arguments for '{name.Name}'"));

        SType Done(IReadOnlyList<SType> ps, SType ret)
        {
            _info.Set(name, new FuncSType(ps, ret));
            return ret;
        }

        switch (name.Name)
        {
            case "zeros" or "ones" or "rand":
            {
                var elem = hint is TensorSType ht ? ht.Elem : Types.F32;
                if (c.Args.Count == 0) { Arity(c, name.Name, scope, 1, 0); return SType.Error; }
                var dims = SizeArgs(c.Args, scope, name.Name);
                if (dims is null) return SType.Error;
                if (!RuntimeElemOrReport(elem, c.Span, name.Name == "rand" ? "'rand'" : $"'{name.Name}'")) return SType.Error;
                return Done(c.Args.Select(_ => (SType)Types.Usize).ToList(), new TensorSType(elem, dims));
            }
            case "eye":
            {
                if (!Arity(c, "eye", scope, 1, 1)) return SType.Error;
                var elem = hint is TensorSType ht ? ht.Elem : Types.F32;
                var dims = SizeArgs(c.Args, scope, "eye");
                if (dims is null) return SType.Error;
                if (!RuntimeElemOrReport(elem, c.Span, "'eye'")) return SType.Error;
                return Done([Types.Usize], new TensorSType(elem, [dims[0], dims[0]]));
            }
            case "arange": return CheckArange(c, name, scope, hint);
            case "linspace": return CheckLinspace(c, name, scope, hint);
            case "reshape": return CheckReshape(c, name, scope);
            case "seed":
            {
                if (!Arity(c, "seed", scope, 1, 1)) return Types.Unit;
                var t = Infer(c.Args[0].Value, scope, Types.I64);
                if (!t.IsError && !t.IsAny && !Types.IsInt(t)) Mismatch(c.Args[0].Value.Span, t, Types.I64);
                return Done([t], Types.Unit);
            }
            case "sqrt" or "exp" or "ln" or "sin" or "cos" or "tan" or "tanh" or "erf" or "floor" or "ceil" or "abs":
            {
                if (!Arity(c, name.Name, scope, 1, 1)) return SType.Error;
                var t = Infer(c.Args[0].Value, scope, hint is PrimType or TensorSType ? hint : null);
                if (t.IsError) return SType.Error;
                if (t.IsAny) return Done([t], SType.Any);
                if (t is TensorSType ts)
                {
                    if (!RuntimeElemOrReport(ts.Elem, c.Span, $"'{name.Name}'")) return SType.Error;
                    return Done([t], ts);
                }
                bool ok = Types.IsFloat(t) || (name.Name == "abs" && Types.IsNumeric(t));
                if (!ok)
                {
                    Report(Templates.TypeMismatch(c.Args[0].Value.Span, N(t), name.Name == "abs" ? "a number or a tensor" : "a float (f16, f32, f64) or a float tensor"));
                    return SType.Error;
                }
                return Done([t], t);
            }
            case "pow":
            {
                if (!Arity(c, "pow", scope, 2, 2)) return SType.Error;
                var (a, b) = InferPair(c.Args[0].Value, c.Args[1].Value, scope, hint is PrimType or TensorSType ? hint : null);
                if (a.IsError || b.IsError) return SType.Error;
                if (a.IsAny || b.IsAny) return Done([a, b], SType.Any);
                if (a is TensorSType || b is TensorSType) return Done([a, b], TensorArithmetic(TokenKind.StarStar, a, b, c.Span, "pow"));
                if (!Types.IsFloat(a) || !a.Equals(b))
                {
                    Report(Templates.BadOperator(c.Span, "pow", N(a), N(b), "pow(x, y) takes two floats of one type (or tensors). For integers use '**'."));
                    return SType.Error;
                }
                return Done([a, b], a);
            }
            case "min" or "max" when c.Args.Count == 2:
            {
                var (a, b) = InferPair(c.Args[0].Value, c.Args[1].Value, scope, hint is PrimType ? hint : null);
                if (a.IsError || b.IsError) return SType.Error;
                if (a.IsAny || b.IsAny) return Done([a, b], a.IsAny ? b : a);
                if (!Types.IsNumeric(a) || !a.Equals(b))
                {
                    Report(Templates.BadOperator(c.Span, name.Name, N(a), N(b), $"{name.Name}(a, b) compares two numbers of one type. For a tensor or List use {name.Name}(xs) with one argument."));
                    return SType.Error;
                }
                return Done([a, b], a);
            }
            case "min" or "max" or "sum" or "prod" or "mean" or "argmax" or "argmin":
            {
                if (!Arity(c, name.Name, scope, 1, 1)) return SType.Error;
                var t = Infer(c.Args[0].Value, scope);
                if (t.IsError) return SType.Error;
                if (t.IsAny) return Done([t], name.Name.StartsWith("arg", StringComparison.Ordinal) ? Types.Usize : SType.Any);
                SType? elem = t is TensorSType ts ? ts.Elem : t is ListSType l ? l.Elem : null;
                if (t is TensorSType tt && !RuntimeElemOrReport(tt.Elem, c.Span, $"'{name.Name}'")) return SType.Error;
                if (elem is null || !Types.IsNumeric(elem) && !elem.IsAny)
                {
                    Report(Templates.TypeMismatch(c.Args[0].Value.Span, N(t), "a tensor or a List of numbers"));
                    return SType.Error;
                }
                if (name.Name == "mean" && !Types.IsFloat(elem) && !elem.IsAny)
                {
                    Report(Templates.TypeMismatch(c.Args[0].Value.Span, N(t), "floats (the mean of integers is not an integer; convert with f64(x) first)"));
                    return SType.Error;
                }
                return Done([t], name.Name.StartsWith("arg", StringComparison.Ordinal) ? Types.Usize : elem);
            }
            case "load_csv":
            {
                if (!Arity(c, "load_csv", scope, 1, 1)) return SType.Error;
                var pt = Infer(c.Args[0].Value, scope, Types.Txt);
                if (!pt.IsError && !Types.Compatible(pt, Types.Txt)) Mismatch(c.Args[0].Value.Span, pt, Types.Txt);
                var elem = hint is TensorSType ht && Types.IsRuntimeTensorElem(ht.Elem) ? ht.Elem : Types.F32;
                return Done([Types.Txt], new TensorSType(elem, [SDim.Dyn, SDim.Dyn]));
            }
            case "save_csv":
            {
                if (!Arity(c, "save_csv", scope, 2, 2)) return Types.Unit;
                var pt = Infer(c.Args[0].Value, scope, Types.Txt);
                if (!pt.IsError && !Types.Compatible(pt, Types.Txt)) Mismatch(c.Args[0].Value.Span, pt, Types.Txt);
                var dt = Infer(c.Args[1].Value, scope, new TensorSType(Types.F32, [SDim.Dyn, SDim.Dyn]));
                bool ok = dt.IsError || dt.IsAny
                    || (dt is TensorSType { Rank: 1 or 2 } t2 && Types.IsRuntimeTensorElem(t2.Elem))
                    || (dt is ListSType { Elem: ListSType { Elem: var inner } } && Types.IsFloat(inner));
                if (!ok) Report(Templates.TypeMismatch(c.Args[1].Value.Span, N(dt), "a vector or matrix tensor (for example f32[?, ?])"));
                return Done([Types.Txt, dt], Types.Unit);
            }
        }
        foreach (var a in c.Args) Infer(a.Value, scope);
        return SType.Error;
    }

    /// <summary>Sizes for <c>zeros(3, 4)</c>: integers; a literal gives a static dimension, anything else is <c>?</c>.</summary>
    private List<SDim>? SizeArgs(IReadOnlyList<Arg> args, Scope scope, string fn)
    {
        var dims = new List<SDim>();
        bool ok = true;
        foreach (var a in args)
        {
            var t = Infer(a.Value, scope, Types.Usize);
            if (t.IsError) { ok = false; continue; }
            if (!Types.IsInt(t) && !t.IsAny)
            {
                Report(Templates.TypeMismatch(a.Value.Span, N(t), $"an integer size for '{fn}'"));
                ok = false;
                continue;
            }
            if (TryConstInt(a.Value, out var n))
            {
                if (n < 0)
                {
                    Report(Templates.BadLiteral(a.Value.Span, $"A tensor cannot have {n} elements along an axis.", "Use a size of 0 or more."));
                    ok = false;
                    continue;
                }
                dims.Add(SDim.Const((long)n));
            }
            else dims.Add(SDim.Dyn);
        }
        return ok ? dims : null;
    }

    private static bool TryConstNumber(Expr e, out double value)
    {
        switch (e)
        {
            case IntLiteral i: value = i.Value; return true;
            case FloatLiteral f: value = f.Value; return true;
            case UnaryExpr { Op: TokenKind.Minus, Operand: var o } when TryConstNumber(o, out var v): value = -v; return true;
            default: value = 0; return false;
        }
    }

    private SType CheckArange(CallExpr c, NameExpr name, Scope scope, SType? hint)
    {
        if (!Arity(c, "arange", scope, 1, 3)) return SType.Error;
        var elem = hint is TensorSType ht ? ht.Elem : Types.F32;
        var types = new List<SType>();
        bool allInt = true, failed = false;
        // First pass without a hint to learn whether the arguments are integers.
        foreach (var a in c.Args)
        {
            var t = IsFlexible(a.Value) && a.Value is not FloatLiteral && !(a.Value is UnaryExpr { Operand: FloatLiteral })
                ? Infer(a.Value, scope, TryConstInt(a.Value, out _) ? null : elem) : Infer(a.Value, scope, elem);
            types.Add(t);
            if (t.IsError) failed = true;
            else if (!Types.IsInt(t)) allInt = false;
        }
        if (failed) return SType.Error;
        SType resultElem = elem;
        if (!allInt)
        {
            resultElem = types.FirstOrDefault(t => Types.IsFloat(t)) ?? elem;
            for (int i = 0; i < types.Count; i++)
                if (!types[i].IsAny && !types[i].Equals(resultElem))
                {
                    Mismatch(c.Args[i].Value.Span, types[i], resultElem);
                    return SType.Error;
                }
        }
        if (!RuntimeElemOrReport(resultElem, c.Span, "'arange'")) return SType.Error;
        SDim len = SDim.Dyn;
        var consts = new double[c.Args.Count];
        if (c.Args.Select((a, i) => TryConstNumber(a.Value, out consts[i])).All(x => x))
        {
            double start = c.Args.Count == 1 ? 0 : consts[0], stop = c.Args.Count == 1 ? consts[0] : consts[1], step = c.Args.Count == 3 ? consts[2] : 1;
            if (step == 0) { Report(Templates.BadStep(c.Args[^1].Value.Span, "0")); return SType.Error; }
            len = SDim.Const((long)Math.Max(0, Math.Ceiling((stop - start) / step)));
        }
        else if (c.Args.Count == 3 && TryConstNumber(c.Args[2].Value, out var st) && st == 0)
        {
            Report(Templates.BadStep(c.Args[2].Value.Span, "0"));
            return SType.Error;
        }
        _info.Set(name, new FuncSType(types, new TensorSType(resultElem, [len])));
        return new TensorSType(resultElem, [len]);
    }

    private SType CheckLinspace(CallExpr c, NameExpr name, Scope scope, SType? hint)
    {
        if (!Arity(c, "linspace", scope, 3, 3)) return SType.Error;
        var elem = hint is TensorSType ht ? ht.Elem : Types.F32;
        var (a, b) = InferPair(c.Args[0].Value, c.Args[1].Value, scope, elem);
        var nt = Infer(c.Args[2].Value, scope, Types.Usize);
        if (a.IsError || b.IsError || nt.IsError) return SType.Error;
        if (!(a.IsAny || Types.IsFloat(a)) || !(b.IsAny || a.IsAny || a.Equals(b)))
        {
            Report(Templates.BadOperator(c.Span, "linspace", N(a), N(b), "linspace(start, stop, count) takes two floats of one type and an integer count."));
            return SType.Error;
        }
        if (!Types.IsInt(nt) && !nt.IsAny) { Mismatch(c.Args[2].Value.Span, nt, Types.Usize); return SType.Error; }
        var resultElem = a.IsAny ? elem : a;
        if (!RuntimeElemOrReport(resultElem, c.Span, "'linspace'")) return SType.Error;
        SDim len = TryConstInt(c.Args[2].Value, out var n) && n >= 0 ? SDim.Const((long)n) : SDim.Dyn;
        var rt = new TensorSType(resultElem, [len]);
        _info.Set(name, new FuncSType([a, b, nt], rt));
        return rt;
    }

    private SType CheckReshape(CallExpr c, NameExpr name, Scope scope)
    {
        if (c.Args.Count < 2) { Arity(c, "reshape", scope, 2, 99); return SType.Error; }
        var t = Infer(c.Args[0].Value, scope);
        var dims = SizeArgs(c.Args.Skip(1).ToList(), scope, "reshape");
        if (t.IsError || dims is null) return SType.Error;
        if (t.IsAny) return SType.Any;
        if (t is not TensorSType ts)
        {
            Report(Templates.TypeMismatch(c.Args[0].Value.Span, N(t), "a tensor"));
            return SType.Error;
        }
        var result = new TensorSType(ts.Elem, dims);
        if (ts.StaticCount is { } have && result.StaticCount is { } want && have != want)
        {
            Report(Templates.GeometryCrime(c.Span, $"Tried to reshape a '{N(ts)}' ({have} elements) into a '{N(result)}' ({want} elements).",
                "A reshape keeps the element count: the new dimensions must multiply to the same number."));
            return SType.Error;
        }
        _info.Set(name, new FuncSType([t], result));
        return result;
    }

    // ---- Phase 14: norms ----

    private SType CheckNorm(NormExpr nm, Scope scope, SType? hint)
    {
        var t = Infer(nm.Operand, scope);
        if (t.IsError || t.IsAny) return t.IsAny ? SType.Any : SType.Error;
        string text = nm.Kind switch { NormKind.L1 => "||x||_1", NormKind.LInf => "||x||_inf", NormKind.Frobenius => "||A||_F", _ => "||x||" };
        if (t is TensorSType ts)
        {
            if (!RuntimeElemOrReport(ts.Elem, nm.Span, $"'{text}'")) return SType.Error;
            return ts.Elem; // the norm of a tensor is a scalar of its element type
        }
        if (Types.IsNumeric(t)) return t; // |x| for a plain number
        Report(Templates.BadOperator(nm.Span, text, N(t), null, "A norm measures a tensor (or a number); this is neither."));
        return SType.Error;
    }

    // ---- Phase 14: sums and products ----

    private SType CheckReduce(ReduceExpr rd, Scope scope, SType? hint)
    {
        string sym = rd.IsProduct ? "∏" : "∑";
        var rangeHint = hint is PrimType { Kind: var hk } && Types.IsInt(hk) ? new RangeSType(hint) : null;
        var rt = Infer(rd.Range, scope, rangeHint);
        RequireStep(rd.Range, rt);
        SType elem = SType.Error;
        if (!rt.IsError)
        {
            if (ElementOf(rt) is { } e) elem = e;
            else if (rt.IsAny) elem = SType.Any;
            else Report(Templates.TypeMismatch(rd.Range.Span, N(rt), NotIterable));
        }
        var body = new Scope(scope);
        Declare(body, new Symbol(rd.Variable, SymbolKind.Variable, elem, false, rd.VariableSpan), rd.VariableSpan);
        var bt = Infer(rd.Body, body, hint is PrimType { Kind: var bk } && Types.IsNumeric(bk) ? hint : null);
        _info.SetPureReduction(rd, Purity.IsPure(rd.Body, body));
        if (bt.IsError || rt.IsError) return SType.Error;
        if (bt.IsAny) return SType.Any;
        if (!Types.IsNumeric(bt))
        {
            Report(Templates.TypeMismatch(rd.Body.Span, N(bt), $"a number (the body of {sym}[...] is summed or multiplied term by term)"));
            return SType.Error;
        }
        return bt;
    }

    // ---- Phase 14: integrals ----

    private SType CheckIntegral(IntegExpr ig, Scope scope, SType? hint)
    {
        var floatHint = hint is PrimType { Kind: var hk } && Types.IsFloat(hk) ? hint : null;
        SType? t = null;
        bool failed = false;
        foreach (var range in ig.Ranges)
        {
            var be = (BinaryExpr)range;
            var (lo, hi) = InferPair(be.Left, be.Right, scope, t ?? floatHint);
            _info.Set(range, new RangeSType(lo));
            if (lo.IsError || hi.IsError) { failed = true; continue; }
            if (lo.IsAny || hi.IsAny) continue;
            if (!Types.IsFloat(lo) || !lo.Equals(hi))
            {
                Report(Templates.TypeMismatch(range.Span, $"{N(lo)}..{N(hi)}", "float bounds of one type, for example 0.0..3.0 (no implicit int to float)"));
                failed = true;
                continue;
            }
            if (t is null) t = lo;
            else if (!t.Equals(lo)) { Mismatch(range.Span, lo, t); failed = true; }
        }
        t ??= floatHint ?? Types.F64;

        foreach (var opt in ig.Options)
        {
            switch (opt.Name)
            {
                case "tol":
                {
                    var ot = Infer(opt.Value, scope, t);
                    if (!ot.IsError && !Types.Compatible(ot, t)) Mismatch(opt.Value.Span, ot, t);
                    break;
                }
                case "samples":
                {
                    var ot = Infer(opt.Value, scope, Types.I64);
                    if (!ot.IsError && !ot.IsAny && !Types.IsInt(ot)) Mismatch(opt.Value.Span, ot, Types.I64);
                    break;
                }
                default:
                    Infer(opt.Value, scope);
                    Report(Templates.UnexpectedToken(opt.Span, opt.Name ?? "an option", "'tol = 1e-8' or 'samples = 100000'"));
                    break;
            }
        }

        int n = ig.Ranges.Count;
        if (ig.Integrand.Params.Count != n)
        {
            Report(Templates.WrongArgCount(ig.Integrand.Span, "the integrand", n, ig.Integrand.Params.Count));
            failed = true;
        }
        else
        {
            var want = new FuncSType(Enumerable.Repeat(t, n).ToList(), t);
            var ft = Infer(ig.Integrand, scope, want) as FuncSType;
            if (ft is not null && !ft.Ret.IsError && !Types.Compatible(ft.Ret, t)) { Mismatch(ig.Integrand.Body.Span, ft.Ret, t); failed = true; }
        }
        return failed ? SType.Error : t;
    }
}
