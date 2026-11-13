namespace Splat.Core;

/// <summary>Comedic diagnostic factories. Spec section 9 wording is used verbatim for the spec categories.</summary>
public static class Templates
{
    // Deterministic quip rotation: same input always yields the same joke, so golden tests stay stable.
    private static string Pick(int seed, params string[] options) => options[Math.Abs(seed) % options.Length];

    private static string Where(SourceSpan s) => s.IsNone ? "Runtime" : $"Line {s.Line}";

    public static Diagnostic MissingSemicolon(SourceSpan span, int openLine) => new(
        DiagnosticCategory.MissingSemicolon, span,
        $"💀 OOF [Line {span.Line}]: You opened a ':' and walked away like an unfinished text message. Expected ';' to close your thought, Shakespeare.",
        $"Add ';' to close the block opened on line {openLine}.");

    public static Diagnostic ShapeMismatch(SourceSpan span, string shapeA, string shapeB, string k1, string k2, bool suggestTranspose = true) => new(
        DiagnosticCategory.ShapeMismatch, span,
        $"📐 GEOMETRY CRIME [Line {span.Line}]: Tried to '@' a '{shapeA}' with a '{shapeB}'. {k1} != {k2}. Even in parallel universes, these matrices cannot hug."
        + (suggestTranspose ? " Tip: Transpose with \"'\"?" : ""),
        $"The inner dimensions must match: left has {k1}, right has {k2}.");

    public static Diagnostic TypeMismatch(SourceSpan span, string found, string expected) => new(
        DiagnosticCategory.TypeMismatch, span,
        $"🎭 IDENTITY CRISIS [Line {span.Line}]: Tried to shove a '{found}' into a '{expected}' slot. I'm strictly typed, bestie.",
        $"Provide a value of type '{expected}', or change the declared type to '{found}'.");

    public static Diagnostic UseAfterMove(SourceSpan span, string variable, int prevLine) => new(
        DiagnosticCategory.UseAfterMove, span,
        $"👻 GHOST HUNTING [Line {span.Line}]: Tried to access '{variable}' after it died/moved on Line {prevLine}. We already held its funeral. Fix: Borrow with '&{variable}' instead.",
        $"Borrow with '&{variable}' instead of moving it on line {prevLine}.");

    public static Diagnostic ZoneEscape(SourceSpan span, string variable, string zone) => new(
        DiagnosticCategory.ZoneEscape, span,
        $"🚧 CONTAINMENT BREACH [Line {span.Line}]: Borrowed reference to '{variable}' tried to sneak out of zone '{zone}'. Values cannot outlive their arena.",
        $"Return '{variable}' by value (it is promoted to the parent scope) instead of by reference.");

    public static Diagnostic NanMeltdown(SourceSpan span, int epoch) => new(
        DiagnosticCategory.NanMeltdown, span,
        $"🌋 MATH MELTDOWN [Runtime @ epoch {epoch}]: Loss became NaN (Not a Number, Not a Good Time). Tip: Lower your learning rate; your weights are orbiting Mars.",
        "Lower 'lr', normalise your inputs, or clip gradients.");

    public static Diagnostic FileNotFound(SourceSpan span, string path) => new(
        DiagnosticCategory.FileNotFound, span,
        $"🕵️ DETECTIVE FAIL [Runtime]: Looked everywhere for \"{path}\" and found pure emptiness. Fix: Check path or wrap in 'yolo: ... oops e: ... ;'.",
        "Check the path, or wrap the call in 'yolo: ... oops e: ... ;'.");

    // ---- Extra categories (same style) ----

    public static Diagnostic UnexpectedToken(SourceSpan span, string found, string expected) => new(
        DiagnosticCategory.UnexpectedToken, span,
        $"🤨 WAIT WHAT [{Where(span)}]: Found '{found}' where I wanted {expected}. {Pick(span.Line, "Bold choice.", "Were you typing with oven mitts?", "The parser is concerned.")}",
        $"Replace '{found}' with {expected}.");

    public static Diagnostic UnterminatedString(SourceSpan span) => new(
        DiagnosticCategory.UnterminatedString, span,
        $"🧵 DANGLING STRING [Line {span.Line}]: This string started and never ended, like a bad podcast.",
        "Add the closing '\"'.");

    public static Diagnostic UnterminatedComment(SourceSpan span) => new(
        DiagnosticCategory.UnterminatedComment, span,
        $"🕳️ COMMENT BLACK HOLE [Line {span.Line}]: A '#-' comment swallowed the rest of your file.",
        "Close the comment with '-#'.");

    public static Diagnostic BadCharacter(SourceSpan span, char c) => new(
        DiagnosticCategory.BadCharacter, span,
        $"👽 ALIEN GLYPH [Line {span.Line}]: Never seen '{c}' (U+{(int)c:X4}) in my life. SPLAT does not speak that.",
        "Remove the character, or put it inside a string or comment.");

    public static Diagnostic UndefinedName(SourceSpan span, string name) => new(
        DiagnosticCategory.UndefinedName, span,
        $"🔍 WHO DIS [{Where(span)}]: '{name}' has never been introduced to me. Did you invent it in a dream?",
        $"Declare '{name}' with ':=' or 'wag' before using it, or check the spelling.");

    public static Diagnostic WrongArgCount(SourceSpan span, string callee, int expected, int found) => new(
        DiagnosticCategory.WrongArgCount, span,
        $"🧮 COUNT YOUR ARGS [{Where(span)}]: '{callee}' wants {expected} argument(s) but you handed it {found}.",
        $"Call '{callee}' with exactly {expected} argument(s).");

    public static Diagnostic DivideByZero(SourceSpan span) => new(
        DiagnosticCategory.DivideByZero, span,
        $"🕳️ BLACK HOLE [{Where(span)}]: Divided by zero. Mathematicians are screaming.",
        "Check the divisor is non-zero before dividing.");

    public static Diagnostic ImmutableAssign(SourceSpan span, string name) => new(
        DiagnosticCategory.ImmutableAssign, span,
        $"🧊 FROZEN SOLID [{Where(span)}]: '{name}' was declared immutable and you tried to change it anyway.",
        $"Declare it with 'wag {name} = ...' if it needs to change.");

    public static Diagnostic StackOverflow(SourceSpan span, int depth) => new(
        DiagnosticCategory.StackOverflow, span,
        $"🥞 STACK OVERFLOW [{Where(span)}]: {depth} calls deep and still going. Recursion has left the chat.",
        "Add a base case, or turn the recursion into a 'spin' loop.");

    public static Diagnostic Internal(SourceSpan span, string detail) => new(
        DiagnosticCategory.Internal, span,
        $"🔧 OUR BAD [{Where(span)}]: The compiler tripped over its own shoelaces: {detail}",
        "This is a SPLAT bug; please report it.");
}
