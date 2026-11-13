namespace Splat.Core;

public enum Severity { Warning, Error }

public enum DiagnosticCategory
{
    // Spec section 9
    MissingSemicolon,
    ShapeMismatch,
    TypeMismatch,
    UseAfterMove,
    ZoneEscape,
    NanMeltdown,
    FileNotFound,
    // Everyday errors later phases need
    UnexpectedToken,
    UnterminatedString,
    UnterminatedComment,
    BadCharacter,
    UndefinedName,
    WrongArgCount,
    DivideByZero,
    ImmutableAssign,
    StackOverflow,
    Internal,
}

/// <summary>One compile-time or runtime problem: comedic headline, location, and a technical fix.</summary>
public sealed record Diagnostic(
    DiagnosticCategory Category,
    SourceSpan Span,
    string Message,
    string? Fix = null,
    Severity Severity = Severity.Error);
