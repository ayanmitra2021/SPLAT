using System.Runtime.CompilerServices;
using Splat.Data;

namespace Splat.Runtime;

/// <summary>The element types the runtime can store and compute with (spec 4.1). <c>bf16</c> and the sub-byte types arrive with the numeric engine.</summary>
public enum TensorElem { F16, F32, F64 }

/// <summary>How a tensor's elements sit in its buffer (the numeric engine, Phase 29, chooses kernels by this).</summary>
public enum TensorLayout
{
    /// <summary>Dense row-major: element strides are exactly the products of the trailing dimensions.</summary>
    Contiguous,
    /// <summary>A view with arbitrary element strides (a slice, a transpose, a broadcast).</summary>
    Strided,
    /// <summary>Sub-byte elements packed several to a byte (reserved: Phase 33).</summary>
    PackedSubByte,
}

/// <summary>A run-time shape or domain problem raised by a kernel; the interpreter turns it into a spanned diagnostic.</summary>
internal sealed class TensorError(string message, string? fix = null) : Exception(message)
{
    public string? Fix { get; } = fix;
    /// <summary>Set for a failed matrix product so the report can use the spec's wording.</summary>
    public (string ShapeA, string ShapeB, long K1, long K2, bool Transposable)? MatMul { get; init; }
}

/// <summary>A numeric failure that is not about shapes (a singular matrix, an empty reduction).</summary>
internal sealed class TensorMathError(string message) : Exception(message);

/// <summary>
/// A dense N-dimensional array of f16, f32 or f64: a shared <see cref="AlignedBuffer"/>, an element offset, a shape and element
/// strides. Slices, transposes and rows are VIEWS (they share the buffer); everything that computes returns a new contiguous tensor.
/// The metadata (element type, dims, strides, layout, alignment, <see cref="BufferId"/>) is what the CRUMPLE engine of
/// Phase 29 consumes, so it is kept exact from the start.
/// </summary>
public sealed class Tensor
{
    public AlignedBuffer Buffer { get; }
    public TensorElem Elem { get; }
    /// <summary>Offset of element [0, 0, ...] in the buffer, in elements.</summary>
    public long Offset { get; }
    public long[] Shape { get; }
    /// <summary>Distance, in elements, between neighbours along each axis.</summary>
    public long[] Strides { get; }

    public Tensor(AlignedBuffer buffer, TensorElem elem, long offset, long[] shape, long[] strides)
    {
        Buffer = buffer; Elem = elem; Offset = offset; Shape = shape; Strides = strides;
    }

    public int Rank => Shape.Length;
    public long Count => Shape.Aggregate(1L, (a, b) => a * b);
    public int ElemSize => SizeOf(Elem);
    public long BufferId => Buffer.Id;

    public static int SizeOf(TensorElem e) => e switch { TensorElem.F16 => 2, TensorElem.F32 => 4, _ => 8 };
    public static string NameOf(TensorElem e) => e switch { TensorElem.F16 => "f16", TensorElem.F32 => "f32", _ => "f64" };
    public string ElemName => NameOf(Elem);

    /// <summary>"f32[3, 4]": the text used in diagnostics and by <c>txt(...)</c> of a shape.</summary>
    public string ShapeText => $"{ElemName}[{string.Join(", ", Shape)}]";

    /// <summary>"f32[,]": element type and rank, the key <c>extend</c> blocks use for this receiver.</summary>
    public string BaseKey => ElemName + "[" + new string(',', Math.Max(0, Rank - 1)) + "]";

    public bool IsContiguous
    {
        get
        {
            long expected = 1;
            for (int i = Rank - 1; i >= 0; i--)
            {
                if (Shape[i] == 1) continue;
                if (Strides[i] != expected) return false;
                expected *= Shape[i];
            }
            return true;
        }
    }

    public TensorLayout Layout => IsContiguous ? TensorLayout.Contiguous : TensorLayout.Strided;

    /// <summary>The largest power of two, up to 64, that divides the byte address of the first element (what a vector kernel may assume).</summary>
    public int BaseAlignment
    {
        get
        {
            ulong addr = (ulong)Buffer.Address + (ulong)(Offset * ElemSize);
            return addr == 0 ? AlignedBuffer.Alignment : (int)Math.Min((ulong)AlignedBuffer.Alignment, addr & (~addr + 1));
        }
    }

    // ---- construction ----

    public static long[] RowMajorStrides(long[] shape)
    {
        var s = new long[shape.Length];
        long acc = 1;
        for (int i = shape.Length - 1; i >= 0; i--) { s[i] = acc; acc *= Math.Max(shape[i], 1); }
        return s;
    }

    /// <summary>A new zero-filled contiguous tensor.</summary>
    public static Tensor Zeros(TensorElem elem, long[] shape)
    {
        long n = shape.Aggregate(1L, (a, b) => a * b);
        var buf = AlignedBuffer.Allocate(n * SizeOf(elem));
        return new Tensor(buf, elem, 0, shape, RowMajorStrides(shape));
    }

    /// <summary>A new contiguous tensor holding <paramref name="values"/> (row-major), each rounded to the element type.</summary>
    public static Tensor FromDoubles(TensorElem elem, long[] shape, ReadOnlySpan<double> values)
    {
        var t = Zeros(elem, shape);
        switch (elem)
        {
            case TensorElem.F64: values.CopyTo(t.ContiguousSpan<double>()); break;
            case TensorElem.F32: { var d = t.ContiguousSpan<float>(); for (int i = 0; i < values.Length; i++) d[i] = (float)values[i]; break; }
            default: { var d = t.ContiguousSpan<Half>(); for (int i = 0; i < values.Length; i++) d[i] = (Half)values[i]; break; }
        }
        return t;
    }

    /// <summary>A rank-0 tensor (what a scalar becomes when it takes part in a broadcast).</summary>
    public static Tensor Scalar(TensorElem elem, double value) => FromDoubles(elem, [], [value]);

    // ---- element access ----

    /// <summary>The whole buffer as <typeparamref name="T"/>; offsets passed to the view helpers are absolute buffer positions.</summary>
    internal Span<T> BufferSpan<T>() where T : unmanaged => Buffer.AsSpan<T>();

    /// <summary>The elements as a flat span; only valid for a contiguous tensor.</summary>
    public Span<T> ContiguousSpan<T>() where T : unmanaged
    {
        if (!IsContiguous) throw new InvalidOperationException("tensor is not contiguous");
        return Buffer.AsSpan<T>(Offset, Count);
    }

    /// <summary>Reads the element at an absolute buffer position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double ReadAt(long pos) => Elem switch
    {
        TensorElem.F64 => Buffer.AsSpan<double>()[(int)pos],
        TensorElem.F32 => Buffer.AsSpan<float>()[(int)pos],
        _ => (double)Buffer.AsSpan<Half>()[(int)pos],
    };

    internal void WriteAt(long pos, double v)
    {
        switch (Elem)
        {
            case TensorElem.F64: Buffer.AsSpan<double>()[(int)pos] = v; break;
            case TensorElem.F32: Buffer.AsSpan<float>()[(int)pos] = (float)v; break;
            default: Buffer.AsSpan<Half>()[(int)pos] = (Half)v; break;
        }
    }

    /// <summary>Visits the absolute buffer position of every element in row-major (logical) order.</summary>
    internal IEnumerable<long> Positions()
    {
        int r = Rank;
        long n = Count;
        if (n == 0) yield break;
        if (r == 0) { yield return Offset; yield break; }
        var idx = new long[r];
        long pos = Offset;
        for (long k = 0; k < n; k++)
        {
            yield return pos;
            for (int d = r - 1; d >= 0; d--)
            {
                idx[d]++; pos += Strides[d];
                if (idx[d] < Shape[d]) break;
                pos -= Strides[d] * Shape[d];
                idx[d] = 0;
            }
        }
    }

    /// <summary>The elements in logical order, as doubles (exact for every element type).</summary>
    public double[] ToDoubles()
    {
        var result = new double[Count];
        int i = 0;
        if (IsContiguous)
        {
            switch (Elem)
            {
                case TensorElem.F64: ContiguousSpan<double>().CopyTo(result); break;
                case TensorElem.F32: foreach (var v in ContiguousSpan<float>()) result[i++] = v; break;
                default: foreach (var v in ContiguousSpan<Half>()) result[i++] = (double)v; break;
            }
            return result;
        }
        foreach (var pos in Positions()) result[i++] = ReadAt(pos);
        return result;
    }

    /// <summary>One element by multi-index (all indices must be in range).</summary>
    public double At(params long[] index)
    {
        long pos = Offset;
        for (int i = 0; i < index.Length; i++) pos += index[i] * Strides[i];
        return ReadAt(pos);
    }

    // ---- views and copies ----

    /// <summary>This tensor if it is contiguous, otherwise a contiguous copy.</summary>
    public Tensor Contiguous() => IsContiguous ? this : Clone();

    /// <summary>A new contiguous tensor with the same elements (never shares the buffer).</summary>
    public Tensor Clone()
    {
        var t = Zeros(Elem, (long[])Shape.Clone());
        if (IsContiguous) { Copy(this, t); return t; }
        long i = 0;
        switch (Elem)
        {
            case TensorElem.F64: { var d = t.ContiguousSpan<double>(); var s = Buffer.AsSpan<double>(); foreach (var p in Positions()) d[(int)i++] = s[(int)p]; break; }
            case TensorElem.F32: { var d = t.ContiguousSpan<float>(); var s = Buffer.AsSpan<float>(); foreach (var p in Positions()) d[(int)i++] = s[(int)p]; break; }
            default: { var d = t.ContiguousSpan<Half>(); var s = Buffer.AsSpan<Half>(); foreach (var p in Positions()) d[(int)i++] = s[(int)p]; break; }
        }
        return t;
    }

    private static void Copy(Tensor from, Tensor to)
    {
        switch (from.Elem)
        {
            case TensorElem.F64: from.ContiguousSpan<double>().CopyTo(to.ContiguousSpan<double>()); break;
            case TensorElem.F32: from.ContiguousSpan<float>().CopyTo(to.ContiguousSpan<float>()); break;
            default: from.ContiguousSpan<Half>().CopyTo(to.ContiguousSpan<Half>()); break;
        }
    }

    /// <summary>A view sharing this buffer (<paramref name="offset"/> is absolute, in elements).</summary>
    public Tensor View(long offset, long[] shape, long[] strides) => new(Buffer, Elem, offset, shape, strides);

    /// <summary>Axes <c>[..., a, b]</c> become <c>[..., b, a]</c>; a vector is its own transpose. A view, no copy.</summary>
    public Tensor Transposed()
    {
        if (Rank < 2) return this;
        var shape = (long[])Shape.Clone();
        var strides = (long[])Strides.Clone();
        (shape[^1], shape[^2]) = (shape[^2], shape[^1]);
        (strides[^1], strides[^2]) = (strides[^2], strides[^1]);
        return View(Offset, shape, strides);
    }

    /// <summary>The same elements with another shape. A view when the tensor is contiguous, otherwise a copy.</summary>
    public Tensor Reshaped(long[] shape)
    {
        long n = shape.Aggregate(1L, (a, b) => a * b);
        if (n != Count) throw new TensorError($"Tried to reshape a '{ShapeText}' ({Count} elements) into {NameOf(Elem)}[{string.Join(", ", shape)}] ({n} elements).",
            "A reshape keeps the element count: the new dimensions must multiply to the same number.");
        var src = Contiguous();
        return src.View(src.Offset, shape, RowMajorStrides(shape));
    }

    /// <summary>Row <paramref name="i"/> along axis 0 as a view (rank - 1 axes).</summary>
    public Tensor Row(long i) =>
        View(Offset + i * Strides[0], Shape.Skip(1).ToArray(), Strides.Skip(1).ToArray());

    /// <summary>Copies the elements of <paramref name="src"/> (broadcast to this shape) into this tensor, writing through views.</summary>
    internal void AssignFrom(Tensor src)
    {
        if (ReferenceEquals(src.Buffer, Buffer)) src = src.Clone(); // overlapping views: read everything before writing anything
        var expanded = TensorOps.BroadcastTo(src, Shape);
        var dst = Positions().GetEnumerator();
        foreach (var sp in expanded.Positions())
        {
            dst.MoveNext();
            WriteAt(dst.Current, src.ReadAt(sp));
        }
    }

    internal void Fill(double v)
    {
        foreach (var p in Positions()) WriteAt(p, v);
    }

    // ---- equality ----

    /// <summary>Same shape and every element equal (NaN is not equal to itself, like a float).</summary>
    public static bool ValuesEqual(Tensor a, Tensor b)
    {
        if (a.Rank != b.Rank || !a.Shape.AsSpan().SequenceEqual(b.Shape)) return false;
        using var ea = a.Positions().GetEnumerator();
        using var eb = b.Positions().GetEnumerator();
        while (ea.MoveNext() && eb.MoveNext())
            if (a.ReadAt(ea.Current) != b.ReadAt(eb.Current)) return false;
        return true;
    }
}
