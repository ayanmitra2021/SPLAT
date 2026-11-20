using System.Globalization;

namespace Splat.Core;

/// <summary>
/// The registry of stable diagnostic IDs. Every diagnostic the SPLAT toolchain can produce has exactly one code:
/// <c>SE</c> + 5 digits for an error (<c>SE00001</c>) or <c>SW</c> + 5 digits for a warning (<c>SW00001</c>).
/// Error and warning numbers are separate sequences. A code, once released, is never renumbered or reused.
/// <para>
/// Adding a diagnostic: add a <see cref="DiagnosticCategory"/> value, add one line to <see cref="Table"/> with the next
/// free number in the right block, and add the factory to <c>Templates</c>. <c>DiagnosticCodeTests</c> fails if a category
/// has no entry, a number is reused, or a template's severity disagrees with its code prefix.
/// </para>
/// Number blocks (errors): 00001-00099 lexing/parsing, 00100-00199 names/types/shapes, 00200-00299 ownership/arenas,
/// 00300-00399 runtime, 00400-00499 OOP/patterns (Milestone 2), 01000-01999 data engine (Milestone 5), 90000+ internal.
/// Warnings use the same blocks in their own SW sequence.
/// </summary>
public static class DiagnosticCodes
{
    public readonly record struct Entry(Severity Kind, int Number);

    private static readonly Dictionary<DiagnosticCategory, Entry> Table = new()
    {
        // lexing / parsing
        [DiagnosticCategory.MissingSemicolon] = new(Severity.Error, 1),
        [DiagnosticCategory.UnexpectedToken] = new(Severity.Error, 2),
        [DiagnosticCategory.UnterminatedString] = new(Severity.Error, 3),
        [DiagnosticCategory.UnterminatedComment] = new(Severity.Error, 4),
        [DiagnosticCategory.BadCharacter] = new(Severity.Error, 5),
        [DiagnosticCategory.BadLiteral] = new(Severity.Error, 6),
        [DiagnosticCategory.StraySemicolon] = new(Severity.Error, 7),
        [DiagnosticCategory.OrphanBranch] = new(Severity.Error, 8),
        [DiagnosticCategory.NotAssignable] = new(Severity.Error, 9),
        [DiagnosticCategory.NotYetBuilt] = new(Severity.Error, 10),
        [DiagnosticCategory.TooDeep] = new(Severity.Error, 11),
        [DiagnosticCategory.UnknownCommand] = new(Severity.Error, 12),
        // names, types, shapes
        [DiagnosticCategory.TypeMismatch] = new(Severity.Error, 100),
        [DiagnosticCategory.UndefinedName] = new(Severity.Error, 101),
        [DiagnosticCategory.WrongArgCount] = new(Severity.Error, 102),
        [DiagnosticCategory.ImmutableAssign] = new(Severity.Error, 103),
        [DiagnosticCategory.ShapeMismatch] = new(Severity.Error, 104),
        [DiagnosticCategory.NotCallable] = new(Severity.Error, 105),
        [DiagnosticCategory.BadOperator] = new(Severity.Error, 106),
        [DiagnosticCategory.Redeclared] = new(Severity.Error, 107),
        [DiagnosticCategory.LoopControlOutside] = new(Severity.Error, 108),
        [DiagnosticCategory.ReturnOutside] = new(Severity.Error, 109),
        [DiagnosticCategory.MissingReturn] = new(Severity.Error, 110),
        [DiagnosticCategory.BadFormat] = new(Severity.Error, 111),
        [DiagnosticCategory.CannotInfer] = new(Severity.Error, 112),
        [DiagnosticCategory.BadStep] = new(Severity.Error, 113),
        [DiagnosticCategory.ChainMismatch] = new(Severity.Error, 114),
        // ownership and arenas
        [DiagnosticCategory.UseAfterMove] = new(Severity.Error, 200),
        [DiagnosticCategory.ArenaEscape] = new(Severity.Error, 201),
        // runtime
        [DiagnosticCategory.DivideByZero] = new(Severity.Error, 300),
        [DiagnosticCategory.StackOverflow] = new(Severity.Error, 301),
        [DiagnosticCategory.FileNotFound] = new(Severity.Error, 302),
        [DiagnosticCategory.NanMeltdown] = new(Severity.Error, 303),
        [DiagnosticCategory.IntegerOverflow] = new(Severity.Error, 304),
        [DiagnosticCategory.Panic] = new(Severity.Error, 305),
        [DiagnosticCategory.BadConversion] = new(Severity.Error, 306),
        [DiagnosticCategory.MathDomain] = new(Severity.Error, 307),
        // internal compiler faults
        [DiagnosticCategory.Internal] = new(Severity.Error, 90001),
        // Phase 7: OOP / ADTs / patterns (errors SE00400–SE00406)
        [DiagnosticCategory.NonExhaustivePick] = new(Severity.Error, 400),
        [DiagnosticCategory.VariantNotFound] = new(Severity.Error, 401),
        [DiagnosticCategory.WrongVariantArity] = new(Severity.Error, 402),
        [DiagnosticCategory.IndexOutOfRange] = new(Severity.Error, 403),
        [DiagnosticCategory.ImmutableMutation] = new(Severity.Error, 404),
        [DiagnosticCategory.MutateWhileIterating] = new(Severity.Error, 405),
        [DiagnosticCategory.NotHashable] = new(Severity.Error, 406),
        [DiagnosticCategory.BadTypeArgs] = new(Severity.Error, 115),
        [DiagnosticCategory.KeyNotFound] = new(Severity.Error, 308),
        // Phases 8-11 (errors): OOP, traits, extend, handled errors, I/O, time, networking
        [DiagnosticCategory.SecretAccess] = new(Severity.Error, 407),
        [DiagnosticCategory.BadOverride] = new(Severity.Error, 408),
        [DiagnosticCategory.SuperInitRequired] = new(Severity.Error, 409),
        [DiagnosticCategory.BadOverload] = new(Severity.Error, 410),
        [DiagnosticCategory.MissingTraitMethods] = new(Severity.Error, 411),
        [DiagnosticCategory.AmbiguousMember] = new(Severity.Error, 412),
        [DiagnosticCategory.InheritanceCycle] = new(Severity.Error, 413),
        [DiagnosticCategory.UninitializedField] = new(Severity.Error, 414),
        [DiagnosticCategory.SelfOutside] = new(Severity.Error, 415),
        [DiagnosticCategory.MissingRangeStep] = new(Severity.Error, 116),
        [DiagnosticCategory.IoError] = new(Severity.Error, 309),
        [DiagnosticCategory.BadJson] = new(Severity.Error, 310),
        [DiagnosticCategory.TimeOverflow] = new(Severity.Error, 311),
        [DiagnosticCategory.NetworkFail] = new(Severity.Error, 312),
        // Phase 7: warnings (SW00400)
        [DiagnosticCategory.UnreachableArm] = new(Severity.Warning, 400),
    };

    public static IReadOnlyDictionary<DiagnosticCategory, Entry> All => Table;

    public static bool IsRegistered(DiagnosticCategory c) => Table.ContainsKey(c);

    public static string Format(Severity kind, int number) =>
        (kind == Severity.Warning ? "SW" : "SE") + number.ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>The code for a category, e.g. <c>SE00001</c>. An unregistered category reports <c>SE99999</c> (a test guards against it).</summary>
    public static string For(DiagnosticCategory c) =>
        Table.TryGetValue(c, out var e) ? Format(e.Kind, e.Number) : "SE99999";
}
