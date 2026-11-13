namespace Splat.Core;

/// <summary>Collects diagnostics and source buffers. Never throws; stops recording after <see cref="MaxErrors"/>.</summary>
public sealed class DiagnosticEngine
{
    private readonly List<Diagnostic> _items = [];
    private readonly Dictionary<string, SourceText> _sources = new(StringComparer.Ordinal);

    public int MaxErrors { get; }

    public DiagnosticEngine(int maxErrors = 20) => MaxErrors = maxErrors;

    public IReadOnlyList<Diagnostic> Diagnostics => _items;
    public int ErrorCount { get; private set; }
    public bool HasErrors => ErrorCount > 0;
    /// <summary>True once the error cap was hit and further errors were dropped.</summary>
    public bool Truncated { get; private set; }

    public void AddSource(SourceText source) => _sources[source.File] = source;

    public SourceText? GetSource(string file) => _sources.GetValueOrDefault(file);

    public void Report(Diagnostic d)
    {
        if (d.Severity == Severity.Error)
        {
            if (ErrorCount >= MaxErrors) { Truncated = true; return; }
            ErrorCount++;
        }
        _items.Add(d);
    }

    public void Clear()
    {
        _items.Clear();
        ErrorCount = 0;
        Truncated = false;
    }
}
