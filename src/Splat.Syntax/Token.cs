using Splat.Core;

namespace Splat.Syntax;

/// <summary>A piece of a string literal: either plain text or an embedded <c>{expr:format}</c>.</summary>
public abstract record StringPart(SourceSpan Span);

public sealed record TextPart(string Value, SourceSpan Span) : StringPart(Span);

/// <summary>
/// An interpolated expression. <see cref="Offset"/> is where <see cref="Source"/> starts in the file, so the parser can
/// re-lex exactly that range and keep correct spans. <see cref="Format"/> is the text after the top-level ':' (e.g. ".3").
/// </summary>
public sealed record ExprPart(string Source, int Offset, SourceSpan Span, string? Format, SourceSpan? FormatSpan) : StringPart(Span);

/// <summary>The decoded value of a <see cref="TokenKind.Temporal"/> token (date, time, stamp, duration or calendar literal).</summary>
public readonly record struct TemporalValue(TemporalKind Kind, long Value);

/// <summary>
/// One lexeme. <see cref="Value"/> holds the decoded literal: <c>ulong</c> for Int, <c>double</c> for Float,
/// <c>int</c> (Unicode scalar) for Char, <c>string</c> for Ident.
/// </summary>
public sealed record Token(
    TokenKind Kind,
    string Text,
    SourceSpan Span,
    int Offset,
    object? Value = null,
    bool SpaceBefore = true,
    bool NewlineBefore = false,
    IReadOnlyList<StringPart>? Parts = null)
{
    public int End => Offset + Text.Length;

    /// <summary>True for a string literal with no interpolation.</summary>
    public bool IsPlainString => Kind == TokenKind.String && Parts!.All(p => p is TextPart);

    public override string ToString() => $"{Kind} '{Text}'";
}
