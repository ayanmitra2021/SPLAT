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
        $"👻 GHOST HUNTING [Line {span.Line}]: Tried to access '{variable}' after it died/moved on Line {prevLine}. We already held its deinit. Fix: Borrow with '&{variable}' instead.",
        $"Borrow with '&{variable}' instead of moving it on line {prevLine}.");

    public static Diagnostic ArenaEscape(SourceSpan span, string variable, string arenaName) => new(
        DiagnosticCategory.ArenaEscape, span,
        $"🚧 CONTAINMENT BREACH [Line {span.Line}]: Borrowed reference to '{variable}' tried to sneak out of arena '{arenaName}'. Values cannot outlive their arena.",
        $"Return '{variable}' by value (it is promoted to the parent scope) instead of by reference.");

    public static Diagnostic NanMeltdown(SourceSpan span, int epoch) => new(
        DiagnosticCategory.NanMeltdown, span,
        $"🌋 MATH MELTDOWN [Runtime @ epoch {epoch}]: Loss became NaN (Not a Number, Not a Good Time). Tip: Lower your learning rate; your weights are orbiting Mars.",
        "Lower 'lr', normalise your inputs, or clip gradients.");

    public static Diagnostic FileNotFound(SourceSpan span, string path) => new(
        DiagnosticCategory.FileNotFound, span,
        $"🕵️ DETECTIVE FAIL [Runtime]: Looked everywhere for \"{path}\" and found pure emptiness. Fix: Check path or wrap in 'try: ... catch e: ... ;'.",
        "Check the path, or wrap the call in 'try: ... catch e: ... ;'.");

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

    public static Diagnostic BadCharacter(SourceSpan span, char c, string? fix = null) => new(
        DiagnosticCategory.BadCharacter, span,
        $"👽 ALIEN GLYPH [Line {span.Line}]: Never seen '{c}' (U+{(int)c:X4}) in my life. SPLAT does not speak that.",
        fix ?? "Remove the character, or put it inside a string or comment.");

    public static Diagnostic BadLiteral(SourceSpan span, string problem, string fix) => new(
        DiagnosticCategory.BadLiteral, span,
        $"🤡 LITERALLY WRONG [Line {span.Line}]: {problem}",
        fix);

    public static Diagnostic UndefinedName(SourceSpan span, string name) => new(
        DiagnosticCategory.UndefinedName, span,
        $"🔍 WHO DIS [{Where(span)}]: '{name}' has never been introduced to me. Did you invent it in a dream?",
        $"Declare '{name}' with ':=' or 'var' before using it, or check the spelling.");

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
        $"Declare it with 'var {name} = ...' if it needs to change.");

    public static Diagnostic StackOverflow(SourceSpan span, int depth) => new(
        DiagnosticCategory.StackOverflow, span,
        $"🥞 STACK OVERFLOW [{Where(span)}]: {depth} calls deep and still going. Recursion has left the chat.",
        "Add a base case, or turn the recursion into a 'loop'.");

    // ---- Parser structure errors ----

    public static Diagnostic StraySemicolon(SourceSpan span) => new(
        DiagnosticCategory.StraySemicolon, span,
        $"🪦 ORPHAN SEMICOLON [Line {span.Line}]: This ';' is closing a block that was never opened. Nobody is home.",
        "Delete the extra ';', or add the ':' that should have opened the block.");

    public static Diagnostic OrphanBranch(SourceSpan span, string keyword) => new(
        DiagnosticCategory.OrphanBranch, span,
        $"🍂 ORPHAN BRANCH [Line {span.Line}]: '{keyword}' showed up with no 'if' to belong to.",
        "Start the chain with 'if condition:', and keep 'el' as the last branch.");

    public static Diagnostic NotAssignable(SourceSpan span) => new(
        DiagnosticCategory.NotAssignable, span,
        $"🚫 NOT A SLOT [Line {span.Line}]: You can't assign to that. It is a value, not a place.",
        "Assign to a variable ('x = ...'), an element ('a[i] = ...') or a field ('p.x = ...').");

    public static Diagnostic NotYetBuilt(SourceSpan span, string construct, int phase) => new(
        DiagnosticCategory.NotYetBuilt, span,
        $"🏗️ HARD HAT AREA [Line {span.Line}]: '{construct}' hasn't been built yet (it arrives in Phase {phase}). The parser walked around it politely.",
        $"Remove the '{construct}' construct for now.", Severity.Error);

    public static Diagnostic TooDeep(SourceSpan span) => new(
        DiagnosticCategory.TooDeep, span,
        $"🪆 NESTING DOLL [Line {span.Line}]: This is nested so deeply the parser got dizzy and sat down.",
        "Split the code into smaller functions or flatten the nesting.");

    // ---- Phase 5: type checker ----

    public static Diagnostic NotCallable(SourceSpan span, string found) => new(
        DiagnosticCategory.NotCallable, span,
        $"📞 WRONG NUMBER [Line {span.Line}]: Tried to call a '{found}'. It does not pick up.",
        "Only functions (fn declarations, lambdas, conversions like 'f32(x)') can be called.");

    public static Diagnostic BadOperator(SourceSpan span, string op, string left, string? right, string fix) => new(
        DiagnosticCategory.BadOperator, span,
        right is null
            ? $"🤹 WRONG TOOL [Line {span.Line}]: '{op}' has no idea what to do with a '{left}'."
            : $"🤹 WRONG TOOL [Line {span.Line}]: '{op}' has no idea what to do with a '{left}' and a '{right}'.",
        fix);

    public static Diagnostic Redeclared(SourceSpan span, string name, int prevLine) => new(
        DiagnosticCategory.Redeclared, span,
        $"🪞 DOPPELGANGER [Line {span.Line}]: '{name}' already lives in this scope (declared on line {prevLine}). Two of you is one too many.",
        $"Pick another name, or open a nested block to shadow '{name}'.");

    public static Diagnostic LoopControlOutside(SourceSpan span, string keyword) => new(
        DiagnosticCategory.LoopControlOutside, span,
        $"🎢 NO RIDE [Line {span.Line}]: '{keyword}' only works inside a 'loop', and you are not in one.",
        $"Move '{keyword}' into a 'loop' body.");

    public static Diagnostic ReturnOutside(SourceSpan span) => new(
        DiagnosticCategory.ReturnOutside, span,
        $"🏃 NOWHERE TO GO [Line {span.Line}]: 'ret' returns from a function and there is no function here.",
        "Use 'ret' inside a 'fn', or delete it.");

    public static Diagnostic MissingReturn(SourceSpan span, string function, string expected) => new(
        DiagnosticCategory.MissingReturn, span,
        $"🕳️ ENDS ABRUPTLY [Line {span.Line}]: '{function}' promises a '{expected}' but some path just stops talking.",
        $"Make every path 'ret' a '{expected}' (or end the body with a '{expected}' expression, and give 'if' chains an 'el').");

    public static Diagnostic BadFormat(SourceSpan span, string spec, string problem) => new(
        DiagnosticCategory.BadFormat, span,
        $"🎀 FORMAT FAUX PAS [Line {span.Line}]: The format spec '{spec}' {problem}.",
        "Specs look like '.3' (precision, floats), '8' (width), '<8' '>8' '^8' (align), '08' (zero pad), 'x' 'X' 'b' 'o' (integer bases).");

    public static Diagnostic CannotInfer(SourceSpan span, string what, string fix) => new(
        DiagnosticCategory.CannotInfer, span,
        $"🔮 CRYSTAL BALL BROKEN [Line {span.Line}]: Cannot work out {what}.",
        fix);

    // ---- Phase 6: iteration core, REPL ----

    public static Diagnostic BadStep(SourceSpan span, string step) => new(
        DiagnosticCategory.BadStep, span,
        $"🐌 STEP BROTHER [{Where(span)}]: A step of {step} would never get anywhere. Ranges walk forward in steps of 1 or more.",
        "Use a step of at least 1 (for example '0..10 by 2'); to count down, wrap the range in 'rev(...)'.");

    public static Diagnostic ChainMismatch(SourceSpan span, string found, string expected) => new(
        DiagnosticCategory.ChainMismatch, span,
        $"🔗 BROKEN CHAIN [Line {span.Line}]: Tried to link a '{found}' part onto a chain of '{expected}'. Every link must be the same metal.",
        $"Every part of a 'then' chain must produce '{expected}'; convert or replace this part.");

    public static Diagnostic UnknownCommand(SourceSpan span, string command) => new(
        DiagnosticCategory.UnknownCommand, span,
        $"🎛️ BUTTON NOT FOUND [REPL]: '{command}' is not one of my commands. I have a short menu.",
        "Type ':help' to see the commands: :help, :reset, :load <file>, :history, :quit.");

    // ---- Phase 5: runtime ----

    public static Diagnostic IntegerOverflow(SourceSpan span, string type, string op) => new(
        DiagnosticCategory.IntegerOverflow, span,
        $"💣 OVERFLOW [{Where(span)}]: '{op}' pushed a '{type}' past its limits. Integers have edges, and you found one.",
        "Use a wider type (for example i64), or check the operands first. SPLAT integer arithmetic is checked, never wrapping.");

    public static Diagnostic Panic(SourceSpan span, string message, IReadOnlyList<string>? trace) => new(
        DiagnosticCategory.Panic, span,
        $"☠️ RIP [{Where(span)}]: {message}",
        "'rip' is unrecoverable by design; fix the condition that led here.", Severity.Error, trace);

    public static Diagnostic BadConversion(SourceSpan span, string value, string target) => new(
        DiagnosticCategory.BadConversion, span,
        $"🔄 LOST IN TRANSLATION [{Where(span)}]: {value} does not fit in a '{target}'.",
        $"Check the range before converting to '{target}', or pick a wider type.");

    public static Diagnostic MathDomain(SourceSpan span, string problem) => new(
        DiagnosticCategory.MathDomain, span,
        $"📉 MATH SAYS NO [{Where(span)}]: {problem}",
        "Check the operands before the operation.");

    public static Diagnostic BadTypeArgs(SourceSpan span, string type, int expected, int found) => new(
        DiagnosticCategory.BadTypeArgs, span,
        $"🧩 WRONG TYPE ARGS [{Where(span)}]: '{type}' takes {expected} type argument(s) but got {found}. Generics are not a buffet.",
        expected == 0 ? $"Write '{type}' without '<...>'." : $"Write '{type}<{string.Join(", ", Enumerable.Range(1, expected).Select(i => "T" + i))}>' with exactly {expected} type argument(s).");

    public static Diagnostic KeyNotFound(SourceSpan span, string key) => new(
        DiagnosticCategory.KeyNotFound, span,
        $"🗝️ KEY NOT FOUND [{Where(span)}]: The map has no entry for {key}. It looked under the doormat too.",
        "Check with 'key in map' first, or use 'map.get(key)', which returns a Maybe.");

    public static Diagnostic Internal(SourceSpan span, string detail) => new(
        DiagnosticCategory.Internal, span,
        $"🔧 OUR BAD [{Where(span)}]: The compiler tripped over its own shoelaces: {detail}",
        "This is a SPLAT bug; please report it.");

    // ---- Phase 7: ADTs, collections, generics ----

    public static Diagnostic NonExhaustivePick(SourceSpan span, string missing) => new(
        DiagnosticCategory.NonExhaustivePick, span,
        $"🕵️ MISSING CASE [Line {span.Line}]: Your 'pick' forgot about variant(s): {missing}. The uncovered cases are filing a complaint.",
        $"Add arms for: {missing}, or add a wildcard '_' catch-all.");

    public static Diagnostic UnreachableArm(SourceSpan span) => new(
        DiagnosticCategory.UnreachableArm, span,
        $"💤 DEAD END [Line {span.Line}]: This 'pick' arm can never be reached. An earlier arm already covered all matching cases.",
        "Remove the unreachable arm, or reorder the arms.", Severity.Warning);

    public static Diagnostic IndexOutOfRange(SourceSpan span, long index, long length) => new(
        DiagnosticCategory.IndexOutOfRange, span,
        $"📍 OUT OF BOUNDS [{Where(span)}]: Index {index} is not in [0, {length}). The list does not go that far.",
        $"Use an index in 0..{length}, or check the length first.");

    public static Diagnostic ImmutableMutation(SourceSpan span, string name) => new(
        DiagnosticCategory.ImmutableMutation, span,
        $"🧊 ICE BREAKER [{Where(span)}]: Tried to mutate '{name}' in a read-only context. Some collections are immutable.",
        $"Declare '{name}' with 'var' to make it mutable.");

    public static Diagnostic MutateWhileIterating(SourceSpan span, string name) => new(
        DiagnosticCategory.MutateWhileIterating, span,
        $"🔄 DON'T TOUCH [{Where(span)}]: '{name}' is being iterated and you tried to modify it at the same time. This ends badly.",
        $"Iterate over a copy ('var copy = {name}') or collect the changes in another variable and apply them after the loop.");

    public static Diagnostic VariantNotFound(SourceSpan span, string enumName, string variant) => new(
        DiagnosticCategory.VariantNotFound, span,
        $"🔍 PHANTOM VARIANT [{Where(span)}]: '{enumName}' has no variant named '{variant}'. Did you dream it up?",
        $"Check the '{enumName}' declaration for the actual variant names.");

    public static Diagnostic WrongVariantArity(SourceSpan span, string variant, int expected, int found) => new(
        DiagnosticCategory.WrongVariantArity, span,
        $"🧮 WRONG PAYLOAD [{Where(span)}]: Variant '{variant}' expects {expected} payload(s) but got {found}.",
        $"Provide exactly {expected} payload(s) for '{variant}'.");

    public static Diagnostic NotHashable(SourceSpan span, string type) => new(
        DiagnosticCategory.NotHashable, span,
        $"🎲 NOT A KEY [{Where(span)}]: '{type}' cannot be a Map or Set key. Only integers, char, txt, bool and tuples thereof are hashable.",
        "Use a hashable key type (i32, txt, char, bool, or a tuple of those).");
}
