namespace Splat.Core;

/// <summary>A region of source: 1-based line/column of the start plus a length in characters.</summary>
public readonly record struct SourceSpan(string File, int Line, int Col, int Length)
{
    /// <summary>Span used when no location is known (e.g. some runtime errors).</summary>
    public static SourceSpan None => new("", 0, 0, 0);

    public bool IsNone => Line <= 0;

    public override string ToString() => IsNone ? "<unknown>" : $"{File}:{Line}:{Col}";
}
