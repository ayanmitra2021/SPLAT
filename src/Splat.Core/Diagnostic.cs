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
    BadLiteral,
    UndefinedName,
    WrongArgCount,
    DivideByZero,
    ImmutableAssign,
    StackOverflow,
    StraySemicolon,
    OrphanBranch,
    NotAssignable,
    NotYetBuilt,
    TooDeep,
    Internal,
}

/// <summary>One compile-time or runtime problem: comedic headline, location, and a technical fix.</summary>
public sealed record Diagnostic(
    DiagnosticCategory Category,
    SourceSpan Span,
    string Message,
    string? Fix = null,
    Severity Severity = Severity.Error)
{
    /// <summary>Stable ID such as <c>SE00001</c> (error) or <c>SW00001</c> (warning); see <see cref="DiagnosticCodes"/>.</summary>
    public string Code => DiagnosticCodes.For(Category);
}
