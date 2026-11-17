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
/// Number blocks (errors): 00001-00099 lexing/parsing, 00100-00199 names/types/shapes, 00200-00299 ownership/zones,
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
        [DiagnosticCategory.YeetOutside] = new(Severity.Error, 109),
        [DiagnosticCategory.MissingReturn] = new(Severity.Error, 110),
        [DiagnosticCategory.BadFormat] = new(Severity.Error, 111),
        [DiagnosticCategory.CannotInfer] = new(Severity.Error, 112),
        [DiagnosticCategory.BadStep] = new(Severity.Error, 113),
        [DiagnosticCategory.ChainMismatch] = new(Severity.Error, 114),
        // ownership and zones
        [DiagnosticCategory.UseAfterMove] = new(Severity.Error, 200),
        [DiagnosticCategory.ZoneEscape] = new(Severity.Error, 201),
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
    };

    public static IReadOnlyDictionary<DiagnosticCategory, Entry> All => Table;

    public static bool IsRegistered(DiagnosticCategory c) => Table.ContainsKey(c);

    public static string Format(Severity kind, int number) =>
        (kind == Severity.Warning ? "SW" : "SE") + number.ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>The code for a category, e.g. <c>SE00001</c>. An unregistered category reports <c>SE99999</c> (a test guards against it).</summary>
    public static string For(DiagnosticCategory c) =>
        Table.TryGetValue(c, out var e) ? Format(e.Kind, e.Number) : "SE99999";
}
