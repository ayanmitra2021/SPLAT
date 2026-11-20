using System.Globalization;
using System.Text;
using Splat.Semantics;

namespace Splat.Runtime;

/// <summary>Prints a tensor in aligned matrix form: <c>[[ 1.0, 22.5], [10.0, 3.0]]</c>, one row per line, long axes summarised with "...".</summary>
internal static class TensorFormat
{
    private const int EdgeLimit = 10;   // an axis longer than this shows its first 3 and last 3 entries
    private const int Edge = 3;

    public static string Display(Tensor t, FormatSpec? spec)
    {
        var kind = t.Elem switch { TensorElem.F16 => VKind.F16, TensorElem.F32 => VKind.F32, _ => VKind.F64 };
        string Elem(double d) => spec?.Precision is int p && double.IsFinite(d)
            ? d.ToString("F" + p, CultureInfo.InvariantCulture)
            : Formatter.FloatText(kind, d);

        if (t.Rank == 0) return Elem(t.ReadAt(t.Offset));
        if (t.Count == 0) return EmptyText(t, 0);

        // First pass: the widest element that will be shown, so columns line up.
        int width = 0;
        void Measure(int axis, long pos)
        {
            foreach (var i in Visible(t.Shape[axis]))
            {
                if (i is null) continue;
                long p = pos + i.Value * t.Strides[axis];
                if (axis == t.Rank - 1) width = Math.Max(width, Elem(t.ReadAt(p)).Length);
                else Measure(axis + 1, p);
            }
        }
        Measure(0, t.Offset);

        var sb = new StringBuilder();
        void Render(int axis, long pos)
        {
            sb.Append('[');
            bool first = true;
            foreach (var i in Visible(t.Shape[axis]))
            {
                if (!first)
                {
                    if (axis == t.Rank - 1) sb.Append(", ");
                    else sb.Append(',').Append('\n', t.Rank - axis - 1).Append(' ', axis + 1);
                }
                first = false;
                if (i is null) { sb.Append(axis == t.Rank - 1 ? "...".PadLeft(width) : "..."); continue; }
                long p = pos + i.Value * t.Strides[axis];
                if (axis == t.Rank - 1) sb.Append(Elem(t.ReadAt(p)).PadLeft(width));
                else Render(axis + 1, p);
            }
            sb.Append(']');
        }
        Render(0, t.Offset);
        return sb.ToString();
    }

    private static string EmptyText(Tensor t, int axis)
    {
        if (axis == t.Rank - 1 || t.Shape[axis] == 0) return "[]";
        return "[" + string.Join(", ", Enumerable.Range(0, (int)t.Shape[axis]).Select(_ => EmptyText(t, axis + 1))) + "]";
    }

    private static List<long?> Visible(long n)
    {
        var list = new List<long?>();
        if (n <= EdgeLimit) { for (long i = 0; i < n; i++) list.Add(i); return list; }
        for (long i = 0; i < Edge; i++) list.Add(i);
        list.Add(null);
        for (long i = n - Edge; i < n; i++) list.Add(i);
        return list;
    }
}
