namespace Splat.Core;

public enum Severity { Warning, Error }

public enum DiagnosticCategory
{
    // Spec section 9
    MissingSemicolon,
    ShapeMismatch,
    TypeMismatch,
    UseAfterMove,
    ArenaEscape,
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
    // Phase 5: type checker and interpreter
    NotCallable,
    BadOperator,
    Redeclared,
    LoopControlOutside,
    ReturnOutside,
    MissingReturn,
    BadFormat,
    CannotInfer,
    IntegerOverflow,
    Panic,
    BadConversion,
    MathDomain,
    // Phase 6: iteration core and REPL
    BadStep,
    ChainMismatch,
    UnknownCommand,
    Internal,
    // Phase 7: ADTs, collections, generics
    NonExhaustivePick,
    UnreachableArm,
    IndexOutOfRange,
    ImmutableMutation,
    MutateWhileIterating,
    VariantNotFound,
    WrongVariantArity,
    NotHashable,
    BadTypeArgs,
    KeyNotFound,
    // Phases 8-11: classes, traits, extend, errors, files, time, net
    SecretAccess,
    BadOverride,
    SuperInitRequired,
    BadOverload,
    MissingTraitMethods,
    AmbiguousMember,
    InheritanceCycle,
    UninitializedField,
    SelfOutside,
    MissingRangeStep,
    IoError,
    BadJson,
    TimeOverflow,
    NetworkFail,
}

/// <summary>One compile-time or runtime problem: comedic headline, location, and a technical fix.</summary>
public sealed record Diagnostic(
    DiagnosticCategory Category,
    SourceSpan Span,
    string Message,
    string? Fix = null,
    Severity Severity = Severity.Error,
    IReadOnlyList<string>? Trace = null)
{
    /// <summary>Stable ID such as <c>SE00001</c> (error) or <c>SW00001</c> (warning); see <see cref="DiagnosticCodes"/>.</summary>
    public string Code => DiagnosticCodes.For(Category);
}
