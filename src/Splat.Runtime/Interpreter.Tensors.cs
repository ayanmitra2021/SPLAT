using System.Globalization;
using System.Text;
using Splat.Core;
using Splat.Data;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Runtime;

// Milestones 3 (Phases 12-14): tensors at run time. Shapes are always re-checked here (a `?` dimension is only known now),
// so every failure is a spanned GEOMETRY CRIME even when the static checker could not catch it.
public sealed partial class Interpreter
{
    private ITensorKernels _kernels = ManagedTensorKernels.Instance;
    private Random _rng = new(0x5EED);

    // ---- small helpers ----

    private static TensorElem ElemOfType(SType t) => t is PrimType { Kind: Prim.F16 } ? TensorElem.F16 : t is PrimType { Kind: Prim.F64 } ? TensorElem.F64 : TensorElem.F32;

    /// <summary>A scalar operand as a double (an integer exponent in <c>A ** 2</c> arrives as an integer).</summary>
    private static double NumberOf(Value v) => v.IsFloat ? v.AsDouble : (double)v.AsInt128;

    private static VKind KindOf(TensorElem e) => e switch { TensorElem.F16 => VKind.F16, TensorElem.F32 => VKind.F32, _ => VKind.F64 };

    private static Value ScalarValue(TensorElem e, double d) => Value.Float(KindOf(e), d);

    /// <summary>A rank-0 result is just a number; anything else stays a tensor.</summary>
    private static Value TensorResult(Tensor t) => t.Rank == 0 ? ScalarValue(t.Elem, t.ReadAt(t.Offset)) : Value.OfTensor(t);

    private T Guarded<T>(SourceSpan span, Func<T> f)
    {
        try { return f(); }
        catch (TensorError e) when (e.MatMul is { } mm)
        {
            throw Fail(Templates.ShapeMismatch(span, mm.ShapeA, mm.ShapeB, mm.K1.ToString(CultureInfo.InvariantCulture), mm.K2.ToString(CultureInfo.InvariantCulture), mm.Transposable));
        }
        catch (TensorError e)
        {
            throw Fail(Templates.GeometryCrime(span, e.Message, e.Fix ?? "Check the shapes of the operands."));
        }
        catch (TensorMathError e)
        {
            throw Fail(Templates.MathDomain(span, e.Message));
        }
    }

    private static long ToDim(Interpreter self, Value v, SourceSpan span)
    {
        var n = v.AsInt128;
        if (n < 0) throw self.Fail(Templates.MathDomain(span, $"A tensor cannot have {n} elements along an axis."));
        if (n > int.MaxValue) throw self.Fail(Templates.MathDomain(span, $"{n} elements along one axis is more tensor than this interpreter can hold."));
        return (long)n;
    }

    private Tensor NewTensor(TensorElem elem, long[] shape, SourceSpan span)
    {
        long n = 1;
        foreach (var d in shape)
        {
            n = checked(n * Math.Max(d, 0));
            if (n * Tensor.SizeOf(elem) > int.MaxValue)
                throw Fail(Templates.MathDomain(span, $"A tensor of shape [{string.Join(", ", shape)}] has {n} elements, more than this interpreter can hold."));
        }
        return Tensor.Zeros(elem, shape);
    }

    // ---- binary operators ----

    private static TokenKind? BaseOpOf(TokenKind assign) => assign switch
    {
        TokenKind.PlusAssign => TokenKind.Plus,
        TokenKind.MinusAssign => TokenKind.Minus,
        TokenKind.StarAssign => TokenKind.Star,
        TokenKind.SlashAssign => TokenKind.Slash,
        TokenKind.AtAssign => TokenKind.At,
        TokenKind.DotStarAssign => TokenKind.DotStar,
        TokenKind.DotSlashAssign => TokenKind.DotSlash,
        _ => null,
    };

    private Value TensorBinary(TokenKind op, Value a, Value b, SourceSpan span)
    {
        if (op is TokenKind.EqEq or TokenKind.BangEq)
        {
            bool eq = a.Kind == VKind.Tensor && b.Kind == VKind.Tensor && Tensor.ValuesEqual(a.AsTensor, b.AsTensor);
            return Value.Bool(op == TokenKind.EqEq ? eq : !eq);
        }
        Tensor? ta = a.Kind == VKind.Tensor ? a.AsTensor : null, tb = b.Kind == VKind.Tensor ? b.AsTensor : null;
        var elem = (ta ?? tb)!.Elem;
        var left = ta ?? Tensor.Scalar(elem, NumberOf(a));
        var right = tb ?? Tensor.Scalar(elem, NumberOf(b));
        string text = op switch
        {
            TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Star => "*", TokenKind.Slash => "/", TokenKind.StarStar => "**",
            TokenKind.DotPlus => ".+", TokenKind.DotMinus => ".-", TokenKind.DotStar => ".*", TokenKind.DotSlash => "./",
            TokenKind.At => "@", TokenKind.Backslash => "\\", _ => "·",
        };

        return Guarded(span, () =>
        {
            switch (op)
            {
                case TokenKind.At: return TensorResult(_kernels.MatMul(left, right));
                case TokenKind.MiddleDot:
                    if (left.Rank != 1 || right.Rank != 1)
                        throw new TensorError($"Tried to take the dot product of a '{left.ShapeText}' and a '{right.ShapeText}'. '·' is for two vectors.", "Use '@' for matrices.");
                    return TensorResult(_kernels.MatMul(left, right));
                case TokenKind.Backslash: return TensorResult(TensorOps.Solve(left, right));
                case TokenKind.Plus or TokenKind.Minus when ta is not null && tb is not null && !ta.Shape.AsSpan().SequenceEqual(tb.Shape):
                    throw new TensorError($"Tried to '{text}' a '{ta.ShapeText}' with a '{tb.ShapeText}'. The shapes differ.",
                        $"'{text}' needs identical shapes; use '.{text}' to broadcast, or reshape one side.");
            }
            var bin = op switch
            {
                TokenKind.Plus or TokenKind.DotPlus => BinOp.Add,
                TokenKind.Minus or TokenKind.DotMinus => BinOp.Sub,
                TokenKind.Star or TokenKind.DotStar => BinOp.Mul,
                TokenKind.Slash or TokenKind.DotSlash => BinOp.Div,
                _ => BinOp.Pow,
            };
            return TensorResult(_kernels.Binary(bin, left, right, text));
        });
    }

    // ---- value semantics ----

    /// <summary>
    /// Binding a tensor that already has a name copies it (so <c>var B = A; B[0, 0] = 1</c> never changes A). A fresh result and a view
    /// made by slicing, transposing or reshaping are bound as they are, so a view stays a view.
    /// </summary>
    private static bool IsFreshTensor(Expr e) => e switch
    {
        ListLiteral or BinaryExpr or UnaryExpr or PostfixExpr or IndexExpr or NormExpr or ReduceExpr => true,
        CallExpr { Callee: NameExpr n } => Prelude.Tensor.Contains(n.Name),
        _ => false,
    };

    // ---- literals ----

    private double ElemDouble(Expr item, Env env)
    {
        switch (item)
        {
            case IntLiteral il when _info.TypeOf(il) is PrimType { Kind: Prim.F16 or Prim.F32 or Prim.F64 }: return il.Value;
            case UnaryExpr { Op: TokenKind.Minus, Operand: IntLiteral nl } when _info.TypeOf(nl) is PrimType { Kind: Prim.F16 or Prim.F32 or Prim.F64 }: return -(double)nl.Value;
        }
        var v = Eval(item, env);
        return v.IsFloat ? v.AsDouble : (double)v.AsInt128;
    }

    private Tensor BuildTensor(ListLiteral ll, TensorSType ts, TensorElem elem, Env env)
    {
        if (ts.Rank == 1)
        {
            var values = new double[ll.Items.Count];
            for (int i = 0; i < values.Length; i++) values[i] = ElemDouble(ll.Items[i], env);
            return Tensor.FromDoubles(elem, [values.Length], values);
        }
        var inner = ts.WithDims(ts.Dims.Skip(1).ToList());
        var rows = new List<Tensor>();
        foreach (var item in ll.Items)
        {
            if (item is ListLiteral sub) rows.Add(BuildTensor(sub, _info.TypeOf(sub) as TensorSType ?? inner, elem, env));
            else rows.Add(Eval(item, env).AsTensor);
        }
        long[] rowShape = rows.Count > 0
            ? rows[0].Shape
            : inner.Dims.Select(d => Shapes.R(d) is ConstSDim c ? c.N : 0L).ToArray();
        foreach (var r in rows)
            if (!r.Shape.AsSpan().SequenceEqual(rowShape))
                throw Fail(Templates.GeometryCrime(ll.Span, $"Tried to stack a '{r.ShapeText}' row onto '{rows[0].ShapeText}' rows. The rows of a tensor literal must all have the same shape.",
                    "Make every row the same length."));
        var result = NewTensor(elem, [rows.Count, .. rowShape], ll.Span);
        for (int i = 0; i < rows.Count; i++) result.Row(i).AssignFrom(rows[i]);
        return result;
    }

    private Value EvalTensorList(ListLiteral ll, TensorSType ts, Env env) =>
        Value.OfTensor(BuildTensor(ll, ts, ElemOfType(ts.Elem), env));

    // ---- indexing ----

    private Tensor SliceView(Tensor t, IndexExpr ix, Env env)
    {
        long offset = t.Offset;
        var shape = new List<long>();
        var strides = new List<long>();
        for (int axis = 0; axis < t.Rank; axis++)
        {
            long dim = t.Shape[axis], stride = t.Strides[axis];
            if (axis >= ix.Indices.Count || ix.Indices[axis] is FullSlice) { shape.Add(dim); strides.Add(stride); continue; }
            var idxExpr = ix.Indices[axis];
            var v = Eval(idxExpr, env);
            if (v.Kind == VKind.Range)
            {
                var r = v.AsRange;
                Int128 start = r.Lo.AsInt128, end = r.Hi.AsInt128 + (r.Inclusive ? 1 : 0), step = r.Step;
                Int128 len = end > start ? (end - start + step - 1) / step : 0;
                if (len == 0) { shape.Add(0); strides.Add(stride); continue; }
                Int128 last = start + (len - 1) * step;
                if (start < 0 || start >= dim) throw Fail(Templates.IndexOutOfRange(idxExpr.Span, (long)start, dim, $"tensor axis {axis}"));
                if (last >= dim) throw Fail(Templates.IndexOutOfRange(idxExpr.Span, (long)last, dim, $"tensor axis {axis}"));
                if (r.Reversed) { offset += (long)last * stride; strides.Add(-(long)step * stride); }
                else { offset += (long)start * stride; strides.Add((long)step * stride); }
                shape.Add((long)len);
            }
            else
            {
                var i = v.AsInt128;
                if (i < 0 || i >= dim)
                    throw Fail(Templates.IndexOutOfRange(idxExpr.Span, (long)Int128.Clamp(i, long.MinValue, long.MaxValue), dim, $"tensor axis {axis}"));
                offset += (long)i * stride;
            }
        }
        return t.View(offset, shape.ToArray(), strides.ToArray());
    }

    private Value TensorIndex(Tensor t, IndexExpr ix, Env env) => TensorResult(SliceView(t, ix, env));

    private void ExecTensorAssign(AssignStmt a, IndexExpr ix, Tensor t, Env env)
    {
        var view = SliceView(t, ix, env);
        var rhs = a.Op == TokenKind.Assign ? EvalOwned(a.Value, env) : Eval(a.Value, env);
        var next = rhs;
        if (a.Op != TokenKind.Assign)
        {
            var op = BaseOpOf(a.Op) ?? throw Fail(Templates.Internal(a.Span, $"assignment operator {a.Op}"));
            next = Binary(op, TensorResult(view), rhs, a.Span);
        }
        Guarded(a.Span, () =>
        {
            if (next.Kind == VKind.Tensor) view.AssignFrom(next.AsTensor);
            else view.Fill(next.AsDouble);
            return 0;
        });
    }

    private IEnumerable<Value> IterateTensor(Tensor t)
    {
        if (t.Rank == 0) yield break;
        for (long i = 0; i < t.Shape[0]; i++)
            yield return t.Rank == 1 ? ScalarValue(t.Elem, t.ReadAt(t.Offset + i * t.Strides[0])) : Value.OfTensor(t.Row(i));
    }

    private void CheckGuard(Expr e, Value v)
    {
        if (_info.Guard(e) is not { } expected) return;
        var t = v.AsTensor;
        for (int i = 0; i < expected.Rank && i < t.Rank; i++)
            if (Shapes.R(expected.Dims[i]) is ConstSDim c && c.N != t.Shape[i])
                throw Fail(Templates.GeometryCrime(e.Span,
                    $"A '{expected.Name}' was required here, but the tensor is a '{t.ShapeText}': axis {i} is {t.Shape[i]}, not {c.N}.",
                    "A '?' dimension is only checked when it flows into a slot that needs a known size. Pass a tensor of the right shape."));
    }

    // ---- unary, transpose, norms ----

    private Value EvalTranspose(PostfixExpr p, Env env)
    {
        var v = Eval(p.Operand, env);
        return Value.OfTensor(v.AsTensor.Transposed());
    }

    private Value EvalNorm(NormExpr nm, Env env)
    {
        var v = Eval(nm.Operand, env);
        if (v.Kind != VKind.Tensor)
        {
            if (v.IsFloat) return Value.Float(v.Kind, Math.Abs(v.AsDouble));
            var n = v.AsInt128;
            return CheckedInt(v.Kind, n < 0 ? -n : n, "||x||", nm.Span);
        }
        var t = v.AsTensor;
        var red = nm.Kind switch { NormKind.L1 => RedOp.NormL1, NormKind.LInf => RedOp.NormInf, _ => RedOp.NormL2 };
        return ScalarValue(t.Elem, Guarded(nm.Span, () => _kernels.Reduce(red, t)));
    }

    // ---- sums and products ----

    private Value EvalReduce(ReduceExpr rd, Env env)
    {
        var type = _info.TypeOf(rd) as PrimType ?? Types.I32;
        var kind = Kinds.Of(type);
        var subject = Eval(rd.Range, env);
        string op = rd.IsProduct ? "∏" : "∑";
        if (Kinds.IsInt(kind))
        {
            Int128 acc = rd.IsProduct ? 1 : 0, min = Types.Min(type.Kind), max = Types.Max(type.Kind);
            foreach (var item in Iterate(subject))
            {
                var iter = new Env(env);
                iter.Define(rd.Variable, item, false);
                var term = Eval(rd.Body, iter).AsInt128;
                Int128 next;
                try { next = checked(rd.IsProduct ? acc * term : acc + term); }
                catch (OverflowException) { next = max + 1; }
                if (next < min || next > max)
                    throw Fail(Templates.IntegerOverflow(rd.Span, type.Name, rd.IsProduct ? "*" : "+",
                        $"The running {(rd.IsProduct ? "product" : "total")} of '{op}' left the range while taking the term for {rd.Variable} = {Formatter.Display(item)}."));
                acc = next;
            }
            return Value.FromInt128(kind, acc);
        }
        var sum = new PairwiseSum();
        double prod = 1;
        foreach (var item in Iterate(subject))
        {
            var iter = new Env(env);
            iter.Define(rd.Variable, item, false);
            double term = Eval(rd.Body, iter).AsDouble;
            if (rd.IsProduct) prod *= term; else sum.Add(term);
        }
        return Value.Float(kind, rd.IsProduct ? prod : sum.Total);
    }

    // ---- integrals ----

    private Value EvalIntegral(IntegExpr ig, Env env)
    {
        var type = _info.TypeOf(ig) as PrimType ?? Types.F64;
        var kind = Kinds.Of(type);
        int n = ig.Ranges.Count;
        var lo = new double[n];
        var hi = new double[n];
        for (int i = 0; i < n; i++)
        {
            var be = (BinaryExpr)ig.Ranges[i];
            lo[i] = Eval(be.Left, env).AsDouble;
            hi[i] = Eval(be.Right, env).AsDouble;
        }
        double eps = kind switch { VKind.F64 => 2.220446049250313e-16, VKind.F32 => 1.1920929e-7, _ => 9.765625e-4 };
        double tol = kind switch { VKind.F64 => 1e-8, VKind.F32 => 1e-5, _ => 1e-2 };
        int samples = 1 << 16;
        foreach (var opt in ig.Options)
        {
            var v = Eval(opt.Value, env);
            if (opt.Name == "tol") tol = v.AsDouble;
            else if (opt.Name == "samples") samples = (int)Int128.Clamp(v.AsInt128, 1, 100_000_000);
        }
        var fn = (Closure)Eval(ig.Integrand, env).Ref!;
        var has = Enumerable.Repeat(true, n).ToArray();
        double F(double[] x)
        {
            var args = new Value[n];
            for (int i = 0; i < n; i++) args[i] = Value.Float(kind, x[i]);
            return Call(fn, args, has, ig.Span).AsDouble;
        }
        try
        {
            return Value.Float(kind, Integrator.Integrate(F, lo, hi, tol, eps, samples));
        }
        catch (IntegrandFault f)
        {
            throw Fail(Templates.MathDomain(ig.Span,
                $"The integrand returned {Formatter.Display(Value.Float(VKind.F64, f.Value))} at ({string.Join(", ", f.At.Select(x => x.ToString("R", CultureInfo.InvariantCulture)))}). NaN and infinity have no area under them."));
        }
        catch (IntegralDidNotConverge e)
        {
            throw Fail(Templates.MathDomain(ig.Span, $"The integral did not converge: {e.Message}. A divergent integrand (1/x across 0, say) has no finite area."));
        }
    }

    // ---- prelude: constructors, math, reductions ----

    private static UnOp? UnOpOf(string name) => name switch
    {
        "sqrt" => UnOp.Sqrt, "exp" => UnOp.Exp, "ln" => UnOp.Ln, "sin" => UnOp.Sin, "cos" => UnOp.Cos, "tan" => UnOp.Tan,
        "tanh" => UnOp.Tanh, "erf" => UnOp.Erf, "floor" => UnOp.Floor, "ceil" => UnOp.Ceil, "abs" => UnOp.Abs, _ => null,
    };

    private static double ScalarUnary(UnOp op, double x)
    {
        var m = Transcendentals.Current;
        return op switch
        {
            UnOp.Sqrt => Math.Sqrt(x), UnOp.Exp => m.Exp(x), UnOp.Ln => m.Ln(x), UnOp.Sin => m.Sin(x), UnOp.Cos => m.Cos(x),
            UnOp.Tan => m.Tan(x), UnOp.Tanh => m.Tanh(x), UnOp.Erf => m.Erf(x), UnOp.Floor => Math.Floor(x), UnOp.Ceil => Math.Ceiling(x),
            UnOp.Abs => Math.Abs(x), _ => -x,
        };
    }

    private Value CallTensorBuiltin(string name, CallExpr c, Value[] args)
    {
        var resultType = _info.TypeOf(c);
        var span = c.Span;
        switch (name)
        {
            case "zeros" or "ones" or "rand":
            {
                var ts = (TensorSType)resultType!;
                var elem = ElemOfType(ts.Elem);
                var t = NewTensor(elem, args.Select(a => ToDim(this, a, span)).ToArray(), span);
                if (name == "ones") t.Fill(1);
                else if (name == "rand")
                    foreach (var p in t.Positions())
                        t.WriteAt(p, elem switch
                        {
                            TensorElem.F64 => _rng.NextDouble(),
                            TensorElem.F32 => _rng.NextSingle(),
                            _ => Math.Min((double)(Half)_rng.NextSingle(), 0.99951171875),
                        });
                return Value.OfTensor(t);
            }
            case "eye":
            {
                var elem = ElemOfType(((TensorSType)resultType!).Elem);
                long n = ToDim(this, args[0], span);
                var t = NewTensor(elem, [n, n], span);
                for (long i = 0; i < n; i++) t.WriteAt(t.Offset + i * (n + 1), 1);
                return Value.OfTensor(t);
            }
            case "arange": return Arange(args, ((TensorSType)resultType!).Elem, span);
            case "linspace":
            {
                var elem = ElemOfType(((TensorSType)resultType!).Elem);
                double a = args[0].AsDouble, b = args[1].AsDouble;
                long n = ToDim(this, args[2], span);
                var t = NewTensor(elem, [n], span);
                for (long i = 0; i < n; i++)
                    t.WriteAt(t.Offset + i, n == 1 ? a : i == n - 1 ? b : a + i * ((b - a) / (n - 1)));
                return Value.OfTensor(t);
            }
            case "reshape":
            {
                var src = args[0].AsTensor;
                var shape = args.Skip(1).Select(a => ToDim(this, a, span)).ToArray();
                return Value.OfTensor(Guarded(span, () => src.Reshaped(shape)));
            }
            case "seed":
                _rng = new Random((int)(args[0].AsInt128 & int.MaxValue));
                return Value.Unit;
            case "load_csv": return LoadCsvTensor(args[0].AsString, ElemOfType(((TensorSType)resultType!).Elem), span);
            case "save_csv": SaveCsvTensor(args[0].AsString, args[1], span); return Value.Unit;

            case "pow":
                if (args[0].Kind == VKind.Tensor || args[1].Kind == VKind.Tensor) return TensorBinary(TokenKind.StarStar, args[0], args[1], span);
                return Value.Float(args[0].Kind, Transcendentals.Current.Pow(args[0].AsDouble, args[1].AsDouble));

            case "min" or "max" when args.Length == 2:
            {
                var a = args[0]; var b = args[1];
                if (a.IsFloat) return Value.Float(a.Kind, name == "min" ? Math.Min(a.AsDouble, b.AsDouble) : Math.Max(a.AsDouble, b.AsDouble));
                var x = a.AsInt128; var y = b.AsInt128;
                return Value.FromInt128(a.Kind, name == "min" ? Int128.Min(x, y) : Int128.Max(x, y));
            }
            case "min" or "max" or "sum" or "prod" or "mean" or "argmax" or "argmin":
                return args[0].Kind == VKind.Tensor ? TensorReduction(name, args[0].AsTensor, span) : ListReduction(name, args[0].AsList, resultType, span);
        }

        // Elementwise math: sqrt exp ln sin cos tan tanh erf floor ceil abs.
        var op = UnOpOf(name) ?? throw Fail(Templates.Internal(span, $"prelude function '{name}'"));
        var v = args[0];
        if (v.Kind == VKind.Tensor) return Value.OfTensor(Guarded(span, () => _kernels.Unary(op, v.AsTensor)));
        if (v.IsFloat) return Value.Float(v.Kind, ScalarUnary(op, v.AsDouble));
        var n2 = v.AsInt128; // abs of an integer
        return CheckedInt(v.Kind, n2 < 0 ? -n2 : n2, "abs", span);
    }

    private Value Arange(Value[] args, SType elemType, SourceSpan span)
    {
        var elem = ElemOfType(elemType);
        double start = 0, stop, step = 1;
        if (args.Length == 1) stop = Num(args[0]);
        else { start = Num(args[0]); stop = Num(args[1]); if (args.Length == 3) step = Num(args[2]); }
        if (step == 0) throw Fail(Templates.BadStep(span, "0"));
        long n = (long)Math.Max(0, Math.Ceiling((stop - start) / step));
        var t = NewTensor(elem, [n], span);
        for (long i = 0; i < n; i++) t.WriteAt(t.Offset + i, start + i * step);
        return Value.OfTensor(t);

        static double Num(Value v) => v.IsFloat ? v.AsDouble : (double)v.AsInt128;
    }

    private Value TensorReduction(string name, Tensor t, SourceSpan span)
    {
        var op = name switch
        {
            "sum" => RedOp.Sum, "prod" => RedOp.Prod, "mean" => RedOp.Mean, "max" => RedOp.Max, "min" => RedOp.Min,
            "argmax" => RedOp.ArgMax, _ => RedOp.ArgMin,
        };
        double r = Guarded(span, () => _kernels.Reduce(op, t));
        return op is RedOp.ArgMax or RedOp.ArgMin ? Value.Int(VKind.Usize, (long)r) : ScalarValue(t.Elem, r);
    }

    private Value ListReduction(string name, SplatList list, SType? resultType, SourceSpan span)
    {
        var items = list.Items;
        bool isArg = name.StartsWith("arg", StringComparison.Ordinal);
        var type = resultType as PrimType ?? Types.I32;
        var kind = isArg ? (items.Count > 0 ? items[0].Kind : VKind.F64) : Kinds.Of(type);
        if (items.Count == 0)
        {
            switch (name)
            {
                case "sum": return Kinds.IsInt(kind) ? Value.FromInt128(kind, 0) : Value.Float(kind, 0);
                case "prod": return Kinds.IsInt(kind) ? Value.FromInt128(kind, 1) : Value.Float(kind, 1);
                case "mean": return Value.Float(kind, double.NaN);
                default: throw Fail(Templates.MathDomain(span, $"There is nothing to take the {name} of: the list is empty."));
            }
        }
        if (Kinds.IsInt(items[0].Kind))
        {
            var ik = items[0].Kind;
            Int128 min = Types.Min(Kinds.P(ik)), max = Types.Max(Kinds.P(ik));
            if (isArg)
            {
                int best = 0;
                for (int i = 1; i < items.Count; i++)
                    if (name == "argmax" ? items[i].AsInt128 > items[best].AsInt128 : items[i].AsInt128 < items[best].AsInt128) best = i;
                return Value.Int(VKind.Usize, best);
            }
            if (name is "max" or "min")
                return items.Aggregate((x, y) => (name == "max" ? y.AsInt128 > x.AsInt128 : y.AsInt128 < x.AsInt128) ? y : x);
            Int128 acc = name == "sum" ? 0 : 1;
            foreach (var it in items)
            {
                Int128 next;
                try { next = checked(name == "sum" ? acc + it.AsInt128 : acc * it.AsInt128); }
                catch (OverflowException) { next = max + 1; }
                if (next < min || next > max) throw Fail(Templates.IntegerOverflow(span, Kinds.Name(ik), name == "sum" ? "+" : "*"));
                acc = next;
            }
            return Value.FromInt128(ik, acc);
        }
        var d = items.Select(x => x.AsDouble).ToArray();
        var fk = items[0].Kind;
        return name switch
        {
            "sum" => Value.Float(fk, Stats.Sum<double>(d)),
            "prod" => Value.Float(fk, Reduce.Prod<double>(d)),
            "mean" => Value.Float(fk, Stats.Mean<double>(d)),
            "max" => Value.Float(fk, Stats.Max<double>(d)),
            "min" => Value.Float(fk, Stats.Min<double>(d)),
            "argmax" => Value.Int(VKind.Usize, Stats.ArgMax<double>(d)),
            _ => Value.Int(VKind.Usize, Stats.ArgMin<double>(d)),
        };
    }

    // ---- CSV ----

    private Value LoadCsvTensor(string path, TensorElem elem, SourceSpan span)
    {
        if (!File.Exists(path)) throw Fail(Templates.FileNotFound(span, path));
        var lines = Io(span, path, () => File.ReadAllLines(path));
        var values = new List<double>();
        long rows = 0, cols = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = lines[i].Split(',');
            var row = new double[cells.Length];
            bool ok = true;
            for (int j = 0; j < cells.Length && ok; j++)
                ok = double.TryParse(cells[j].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out row[j]);
            if (!ok)
            {
                if (rows == 0 && i == 0) continue; // a first row of text is a header
                throw Fail(Templates.IoError(span, $"'{path}' line {i + 1}", "this CSV cell is not a number (only a first header row may be text)"));
            }
            if (cols >= 0 && cells.Length != cols)
                throw Fail(Templates.IoError(span, $"'{path}' line {i + 1}", $"this row has {cells.Length} cells but the rows before it have {cols}"));
            cols = cells.Length;
            values.AddRange(row);
            rows++;
        }
        return Value.OfTensor(Tensor.FromDoubles(elem, [rows, Math.Max(cols, 0)], values.ToArray()));
    }

    private void SaveCsvTensor(string path, Value data, SourceSpan span)
    {
        var sb = new StringBuilder();
        string Cell(double d, VKind k) => Formatter.FloatText(k, d);
        if (data.Kind == VKind.Tensor)
        {
            var t = data.AsTensor;
            var k = KindOf(t.Elem);
            if (t.Rank == 1) sb.Append(string.Join(",", t.ToDoubles().Select(d => Cell(d, k)))).Append('\n');
            else
            {
                var all = t.ToDoubles();
                long cols = t.Shape[1];
                for (long r = 0; r < t.Shape[0]; r++)
                    sb.Append(string.Join(",", all.Skip((int)(r * cols)).Take((int)cols).Select(d => Cell(d, k)))).Append('\n');
            }
        }
        else
        {
            foreach (var row in data.AsList.Items)
                sb.Append(string.Join(",", row.AsList.Items.Select(c => Cell(c.AsDouble, c.Kind)))).Append('\n');
        }
        Io(span, path, () => { File.WriteAllText(path, sb.ToString()); return 0; });
    }
}
