using Splat.Core;
using Splat.Syntax;

namespace Splat.Tests;

internal static class LexHelper
{
    public static (List<Token> Tokens, DiagnosticEngine Diag, SourceText Source) Lex(string text)
    {
        var diag = new DiagnosticEngine();
        var src = new SourceText("t.splat", text);
        diag.AddSource(src);
        return (new Lexer(src, diag).Tokenize(), diag, src);
    }

    /// <summary>Lexes clean input and returns every kind except the trailing EOF.</summary>
    public static TokenKind[] Kinds(string text)
    {
        var (tokens, diag, _) = Lex(text);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics.Select(d => d.Message)));
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
        return tokens.Take(tokens.Count - 1).Select(t => t.Kind).ToArray();
    }

    public static Token Single(string text)
    {
        var (tokens, diag, _) = Lex(text);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, tokens.Count);
        return tokens[0];
    }
}

public class LexerLiteralTests
{
    [Theory]
    [InlineData("42", 42UL)]
    [InlineData("0", 0UL)]
    [InlineData("1_000", 1000UL)]
    [InlineData("0xFF", 255UL)]
    [InlineData("0xff", 255UL)]
    [InlineData("0b1010", 10UL)]
    [InlineData("0b1_0", 2UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    public void Integers(string text, ulong expected)
    {
        var t = LexHelper.Single(text);
        Assert.Equal(TokenKind.Int, t.Kind);
        Assert.Equal(expected, t.Value);
    }

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("1e-4", 1e-4)]
    [InlineData("1E5", 1e5)]
    [InlineData("2.5e+3", 2500.0)]
    [InlineData("1_0.5", 10.5)]
    [InlineData("0.5", 0.5)]
    public void Floats(string text, double expected)
    {
        var t = LexHelper.Single(text);
        Assert.Equal(TokenKind.Float, t.Kind);
        Assert.Equal(expected, (double)t.Value!);
    }

    [Fact]
    public void RangeAfterInteger_IsNotAFloat()
    {
        Assert.Equal([TokenKind.Int, TokenKind.DotDot, TokenKind.Int], LexHelper.Kinds("1..3"));
        Assert.Equal([TokenKind.Int, TokenKind.DotDotEq, TokenKind.Int], LexHelper.Kinds("1..=3"));
        Assert.Equal([TokenKind.Int, TokenKind.DotStar, TokenKind.Int], LexHelper.Kinds("2.*3"));
        Assert.Equal([TokenKind.Int, TokenKind.Dot, TokenKind.Ident], LexHelper.Kinds("3.x"));
    }

    [Fact]
    public void ExponentNeedsDigits_OtherwiseItIsAnIdentifierGlue()
    {
        // '1e' is not an exponent, so the 'e' glues onto the number and is reported.
        var (_, diag, _) = LexHelper.Lex("1e");
        Assert.Equal(DiagnosticCategory.BadLiteral, diag.Diagnostics.Single().Category);
    }

    [Fact]
    public void Booleans()
    {
        Assert.Equal([TokenKind.True, TokenKind.False], LexHelper.Kinds("true false"));
    }

    [Fact]
    public void Unit_IsParenPair()
    {
        Assert.Equal([TokenKind.LParen, TokenKind.RParen], LexHelper.Kinds("()"));
    }

    [Theory]
    [InlineData("c\"a\"", 97)]
    [InlineData("c\"\\n\"", 10)]
    [InlineData("c\"\\\"\"", 34)]
    [InlineData("c\"é\"", 0xE9)]
    [InlineData("c\"😀\"", 0x1F600)]
    public void Chars(string text, int expected)
    {
        var t = LexHelper.Single(text);
        Assert.Equal(TokenKind.Char, t.Kind);
        Assert.Equal(expected, t.Value);
    }

    [Fact]
    public void CharNeedsExactlyOneCharacter()
    {
        var (_, diag, _) = LexHelper.Lex("c\"ab\"");
        Assert.Equal(DiagnosticCategory.BadLiteral, diag.Diagnostics.Single().Category);
        var (_, diag2, _) = LexHelper.Lex("c\"\"");
        Assert.Equal(DiagnosticCategory.BadLiteral, diag2.Diagnostics.Single().Category);
    }

    [Fact]
    public void LoneC_IsAnIdentifier_AndSpacedCIsNotAChar()
    {
        Assert.Equal([TokenKind.Ident], LexHelper.Kinds("c"));
        Assert.Equal([TokenKind.Ident, TokenKind.String], LexHelper.Kinds("c \"a\""));
    }

    [Fact]
    public void Identifiers()
    {
        var t = LexHelper.Single("_under_score9");
        Assert.Equal(TokenKind.Ident, t.Kind);
        Assert.Equal("_under_score9", t.Value);
        Assert.Equal(TokenKind.Ident, LexHelper.Single("π").Kind);
        Assert.Equal(TokenKind.Ident, LexHelper.Single("_").Kind);
    }

    [Theory]
    [InlineData("12abc")]
    [InlineData("0b102")]
    [InlineData("1_000x")]
    [InlineData("0x")]
    [InlineData("0xZZ")]
    [InlineData("18446744073709551616")]
    [InlineData("1e999")]
    public void BadNumbers_ReportBadLiteral_AndStillProduceOneToken(string text)
    {
        var (tokens, diag, _) = LexHelper.Lex(text);
        Assert.Equal(DiagnosticCategory.BadLiteral, diag.Diagnostics.First().Category);
        Assert.Equal(2, tokens.Count);
    }
}

public class LexerKeywordAndOperatorTests
{
    [Theory]
    [InlineData("var", TokenKind.Var)] [InlineData("fn", TokenKind.Fn)] [InlineData("ret", TokenKind.Return)]
    [InlineData("if", TokenKind.If)] [InlineData("elif", TokenKind.Elif)] [InlineData("el", TokenKind.Else)]
    [InlineData("loop", TokenKind.Loop)] [InlineData("br", TokenKind.Break)] [InlineData("cont", TokenKind.Continue)]
    [InlineData("pick", TokenKind.Pick)] [InlineData("class", TokenKind.Class)] [InlineData("from", TokenKind.From)]
    [InlineData("with", TokenKind.With)] [InlineData("trait", TokenKind.Trait)] [InlineData("extend", TokenKind.Extend)]
    [InlineData("self", TokenKind.Self)] [InlineData("super", TokenKind.Super)] [InlineData("init", TokenKind.Init)]
    [InlineData("deinit", TokenKind.Deinit)] [InlineData("secret", TokenKind.Secret)] [InlineData("enum", TokenKind.Enum)]
    [InlineData("try", TokenKind.Try)] [InlineData("catch", TokenKind.Catch)] [InlineData("rip", TokenKind.Rip)]
    [InlineData("arena", TokenKind.Arena)] [InlineData("drop", TokenKind.Drop)] [InlineData("imp", TokenKind.Import)]
    [InlineData("regress", TokenKind.Regress)] [InlineData("classify", TokenKind.Classify)] [InlineData("network", TokenKind.Network)]
    [InlineData("train", TokenKind.Train)] [InlineData("fit", TokenKind.Fit)] [InlineData("on", TokenKind.On)]
    [InlineData("in", TokenKind.In)] [InlineData("while", TokenKind.While)] [InlineData("and", TokenKind.And)]
    [InlineData("or", TokenKind.Or)] [InlineData("true", TokenKind.True)] [InlineData("false", TokenKind.False)]
    [InlineData("grad", TokenKind.Grad)] [InlineData("intg", TokenKind.Intg)] [InlineData("sum", TokenKind.Sum)]
    [InlineData("prod", TokenKind.Prod)]
    public void Keywords(string text, TokenKind kind) => Assert.Equal(kind, LexHelper.Single(text).Kind);

    [Theory]
    [InlineData("∂", TokenKind.Grad)] [InlineData("∫", TokenKind.Intg)]
    [InlineData("∑", TokenKind.Sum)] [InlineData("∏", TokenKind.Prod)]
    [InlineData("·", TokenKind.MiddleDot)]
    public void UnicodeOperators(string text, TokenKind kind) => Assert.Equal(kind, LexHelper.Single(text).Kind);

    [Fact]
    public void KeywordsAreCaseSensitive_AndPrefixesAreIdentifiers()
    {
        Assert.Equal(TokenKind.Ident, LexHelper.Single("Var").Kind);
        Assert.Equal(TokenKind.Ident, LexHelper.Single("waggle").Kind);
        Assert.Equal(TokenKind.Ident, LexHelper.Single("f32").Kind);
        Assert.Equal(TokenKind.Ident, LexHelper.Single("print").Kind);
    }

    [Theory]
    [InlineData("+", TokenKind.Plus)] [InlineData("-", TokenKind.Minus)] [InlineData("*", TokenKind.Star)]
    [InlineData("/", TokenKind.Slash)] [InlineData("%", TokenKind.Percent)] [InlineData("**", TokenKind.StarStar)]
    [InlineData("@", TokenKind.At)] [InlineData("\\", TokenKind.Backslash)]
    [InlineData(".*", TokenKind.DotStar)] [InlineData("./", TokenKind.DotSlash)]
    [InlineData(".+", TokenKind.DotPlus)] [InlineData(".-", TokenKind.DotMinus)]
    [InlineData("..", TokenKind.DotDot)] [InlineData("..=", TokenKind.DotDotEq)]
    [InlineData("<", TokenKind.Lt)] [InlineData("<=", TokenKind.Le)] [InlineData(">", TokenKind.Gt)]
    [InlineData(">=", TokenKind.Ge)] [InlineData("==", TokenKind.EqEq)] [InlineData("!=", TokenKind.BangEq)]
    [InlineData("!", TokenKind.Bang)] [InlineData("&", TokenKind.Amp)] [InlineData("~", TokenKind.Tilde)]
    [InlineData("|", TokenKind.Pipe)] [InlineData("|>", TokenKind.PipeGreater)] [InlineData(">>", TokenKind.GreaterGreater)]
    [InlineData("=>", TokenKind.FatArrow)]
    [InlineData("=", TokenKind.Assign)] [InlineData("+=", TokenKind.PlusAssign)] [InlineData("-=", TokenKind.MinusAssign)]
    [InlineData("*=", TokenKind.StarAssign)] [InlineData("/=", TokenKind.SlashAssign)] [InlineData("@=", TokenKind.AtAssign)]
    [InlineData(".*=", TokenKind.DotStarAssign)] [InlineData("./=", TokenKind.DotSlashAssign)]
    [InlineData("$", TokenKind.Dollar)] [InlineData("?", TokenKind.Question)]
    [InlineData(",", TokenKind.Comma)] [InlineData(".", TokenKind.Dot)] [InlineData(";", TokenKind.Semicolon)]
    [InlineData(":", TokenKind.Colon)] [InlineData("::", TokenKind.DoubleColon)] [InlineData(":=", TokenKind.ColonAssign)]
    public void Operators(string text, TokenKind kind)
    {
        var t = LexHelper.Single(text);
        Assert.Equal(kind, t.Kind);
        Assert.Equal(text, t.Text);
    }

    [Fact]
    public void LongestMatchWins()
    {
        Assert.Equal([TokenKind.StarStar, TokenKind.Star], LexHelper.Kinds("*** "[..3]));
        Assert.Equal([TokenKind.DotStarAssign], LexHelper.Kinds(".*="));
        Assert.Equal([TokenKind.DotDotEq, TokenKind.Int], LexHelper.Kinds("..=3"));
        Assert.Equal([TokenKind.Le, TokenKind.Assign], LexHelper.Kinds("<= ="));
        Assert.Equal([TokenKind.EqEq, TokenKind.Assign], LexHelper.Kinds("== ="));
    }

    [Fact]
    public void Semicolons_AreSeparateTokens()
    {
        Assert.Equal([TokenKind.Semicolon, TokenKind.Semicolon, TokenKind.Semicolon], LexHelper.Kinds(";;;"));
    }

    [Fact]
    public void GenericsCloseAsGreaterGreater_ParserMustSplitIt()
    {
        Assert.Equal(
            [TokenKind.Ident, TokenKind.Lt, TokenKind.Ident, TokenKind.Lt, TokenKind.Ident, TokenKind.GreaterGreater],
            LexHelper.Kinds("Maybe<Maybe<i32>>"));
    }
}

public class LexerColonAndBracketTests
{
    [Fact]
    public void BlockColon_TypeColon_BindColon()
    {
        Assert.Equal([TokenKind.If, TokenKind.Ident, TokenKind.Colon], LexHelper.Kinds("if x:"));
        Assert.Equal([TokenKind.Ident, TokenKind.DoubleColon, TokenKind.Ident], LexHelper.Kinds("x::f32"));
        Assert.Equal([TokenKind.Ident, TokenKind.ColonAssign, TokenKind.Int], LexHelper.Kinds("x:=1"));
        Assert.Equal([TokenKind.Ident, TokenKind.DoubleColon, TokenKind.Ident, TokenKind.Assign, TokenKind.Float],
            LexHelper.Kinds("x::f32 = 1.0"));
    }

    [Fact]
    public void ColonInsideBrackets_IsSlice()
    {
        Assert.Equal(
            [TokenKind.Ident, TokenKind.LBracket, TokenKind.SliceColon, TokenKind.Comma, TokenKind.Int, TokenKind.DotDot,
             TokenKind.Int, TokenKind.RBracket],
            LexHelper.Kinds("A[:, 0..3]"));
        Assert.Equal(
            [TokenKind.Ident, TokenKind.LBracket, TokenKind.Int, TokenKind.SliceColon, TokenKind.Int, TokenKind.RBracket],
            LexHelper.Kinds("A[1:2]"));
    }

    [Fact]
    public void ColonAfterClosingBracket_IsBlockOpen()
    {
        var kinds = LexHelper.Kinds("if A[0] == 1:");
        Assert.Equal(TokenKind.Colon, kinds[^1]);
        Assert.DoesNotContain(TokenKind.SliceColon, kinds);
    }

    [Fact]
    public void ParenInsideBracket_ResetsToBlockColon_AndRestoresAfterwards()
    {
        Assert.Equal(
            [TokenKind.LBracket, TokenKind.LParen, TokenKind.Colon, TokenKind.RParen, TokenKind.SliceColon, TokenKind.RBracket],
            LexHelper.Kinds("[(:):]"));
    }

    [Fact]
    public void NestedBrackets()
    {
        Assert.Equal(
            [TokenKind.LBracket, TokenKind.LBracket, TokenKind.SliceColon, TokenKind.RBracket, TokenKind.SliceColon, TokenKind.RBracket],
            LexHelper.Kinds("[[:]:]"));
    }

    [Fact]
    public void TypeBracketsWithDims_LexCleanly()
    {
        Assert.Equal(
            [TokenKind.Ident, TokenKind.LBracket, TokenKind.Dollar, TokenKind.Ident, TokenKind.Comma, TokenKind.Question,
             TokenKind.Comma, TokenKind.Int, TokenKind.RBracket],
            LexHelper.Kinds("f32[$B, ?, 128]"));
    }

    [Fact]
    public void StrayClosers_DoNotCrashOrPoisonState()
    {
        // Unbalanced ')' is the parser's problem; the lexer must still treat later colons correctly.
        Assert.Equal([TokenKind.RParen, TokenKind.Colon], LexHelper.Kinds("):"));
    }

    [Fact]
    public void UnclosedBracketBeforeCloser_IsDroppedByMatchingOuterParen()
    {
        // '(' ... '[' ... ')' : the ')' unwinds past the unmatched '[', so the final ':' is a block colon.
        var kinds = LexHelper.Kinds("( [ ) :");
        Assert.Equal(TokenKind.Colon, kinds[^1]);
    }
}

public class LexerTransposeAndPipeTests
{
    [Fact]
    public void Transpose_AfterValues()
    {
        Assert.Equal([TokenKind.Ident, TokenKind.Transpose], LexHelper.Kinds("A'"));
        Assert.Equal([TokenKind.Ident, TokenKind.ConjTranspose], LexHelper.Kinds("A''"));
        Assert.Equal([TokenKind.Ident, TokenKind.ConjTranspose, TokenKind.Transpose], LexHelper.Kinds("A'''"));
        Assert.Equal([TokenKind.Ident, TokenKind.Transpose, TokenKind.Transpose], LexHelper.Kinds("A' '"));
        Assert.Equal(
            [TokenKind.LParen, TokenKind.Ident, TokenKind.At, TokenKind.Ident, TokenKind.RParen, TokenKind.Transpose],
            LexHelper.Kinds("(A@B)'"));
        Assert.Equal(
            [TokenKind.Ident, TokenKind.LBracket, TokenKind.Int, TokenKind.RBracket, TokenKind.Transpose],
            LexHelper.Kinds("A[0]'"));
        Assert.Equal([TokenKind.Self, TokenKind.Transpose], LexHelper.Kinds("self'"));
    }

    [Fact]
    public void Transpose_InExpression()
    {
        Assert.Equal(
            [TokenKind.Ident, TokenKind.Transpose, TokenKind.At, TokenKind.Ident],
            LexHelper.Kinds("A' @ B"));
    }

    [Fact]
    public void Tick_WithoutAValueBeforeIt_IsAnError_WithCharLiteralHint()
    {
        var (tokens, diag, _) = LexHelper.Lex("x = 'a'");
        var first = diag.Diagnostics.First();
        Assert.Equal(DiagnosticCategory.BadCharacter, first.Category);
        Assert.Contains("c\"a\"", first.Fix);
        Assert.Equal(1, first.Span.Length);
        Assert.Equal(5, first.Span.Col);
        Assert.Contains(tokens, t => t.Kind == TokenKind.Ident && (string)t.Value! == "a");
    }

    [Fact]
    public void TwoPipes_AreAdjacentTokens_WithSpaceFlag()
    {
        var (tokens, diag, _) = LexHelper.Lex("||v||");
        Assert.False(diag.HasErrors);
        Assert.Equal([TokenKind.Pipe, TokenKind.Pipe, TokenKind.Ident, TokenKind.Pipe, TokenKind.Pipe, TokenKind.EndOfFile],
            tokens.Select(t => t.Kind));
        Assert.False(tokens[1].SpaceBefore);
        Assert.False(tokens[4].SpaceBefore);
        Assert.True(tokens[0].SpaceBefore);
    }

    [Fact]
    public void LambdaBars_AreSeparatedFromNormBarsBySpacing()
    {
        var (tokens, _, _) = LexHelper.Lex("| |");
        Assert.True(tokens[1].SpaceBefore);
        var (lam, _, _) = LexHelper.Lex("|x| x");
        Assert.Equal([TokenKind.Pipe, TokenKind.Ident, TokenKind.Pipe, TokenKind.Ident, TokenKind.EndOfFile], lam.Select(t => t.Kind));
    }

    [Fact]
    public void NormSuffixes_AreIdentifiersAdjacentToTheClosingBars()
    {
        var (tokens, diag, _) = LexHelper.Lex("||A||_F ||v||_1 ||v||_inf");
        Assert.False(diag.HasErrors);
        var suffixes = tokens.Where(t => t.Kind == TokenKind.Ident && ((string)t.Value!).StartsWith('_')).ToList();
        Assert.Equal(["_F", "_1", "_inf"], suffixes.Select(t => (string)t.Value!));
        Assert.All(suffixes, t => Assert.False(t.SpaceBefore));
    }

    [Fact]
    public void PipeForward_And_NeuralPipe()
    {
        Assert.Equal([TokenKind.Ident, TokenKind.PipeGreater, TokenKind.Ident], LexHelper.Kinds("x |> f"));
        Assert.Equal([TokenKind.Ident, TokenKind.GreaterGreater, TokenKind.Ident], LexHelper.Kinds("lin >> gelu"));
    }

    [Fact]
    public void BooleanOrIsTheKeyword_NotPipes()
    {
        Assert.Equal([TokenKind.Ident, TokenKind.Or, TokenKind.Ident, TokenKind.And, TokenKind.Ident],
            LexHelper.Kinds("a or b and c"));
    }
}

public class LexerStringTests
{
    private static Token Str(string text)
    {
        var t = LexHelper.Single(text);
        Assert.Equal(TokenKind.String, t.Kind);
        return t;
    }

    [Fact]
    public void PlainString()
    {
        var t = Str("\"hello\"");
        Assert.True(t.IsPlainString);
        Assert.Equal("hello", ((TextPart)Assert.Single(t.Parts!)).Value);
        Assert.Equal("\"hello\"", t.Text);
    }

    [Fact]
    public void EmptyString_HasNoParts()
    {
        var t = Str("\"\"");
        Assert.Empty(t.Parts!);
        Assert.True(t.IsPlainString);
    }

    [Fact]
    public void Interpolation_ProducesTextExprTextParts()
    {
        var t = Str("\"hi {name}!\"");
        Assert.Equal(3, t.Parts!.Count);
        Assert.Equal("hi ", ((TextPart)t.Parts[0]).Value);
        var e = Assert.IsType<ExprPart>(t.Parts[1]);
        Assert.Equal("name", e.Source);
        Assert.Null(e.Format);
        Assert.Equal("!", ((TextPart)t.Parts[2]).Value);
        Assert.False(t.IsPlainString);
    }

    [Fact]
    public void FormatSpec_IsSplitFromExpression()
    {
        var t = Str("\"val = {x:.3}\"");
        var e = Assert.IsType<ExprPart>(t.Parts![1]);
        Assert.Equal("x", e.Source);
        Assert.Equal(".3", e.Format);
        Assert.NotNull(e.FormatSpan);
        Assert.Equal(2, e.FormatSpan!.Value.Length); // ".3"
    }

    [Fact]
    public void ExpressionColons_AreNotFormatSpecs()
    {
        var slice = Assert.IsType<ExprPart>(Str("\"{A[0:1]}\"").Parts![0]);
        Assert.Equal("A[0:1]", slice.Source);
        Assert.Null(slice.Format);

        var typed = Assert.IsType<ExprPart>(Str("\"{x::i32}\"").Parts![0]);
        Assert.Equal("x::i32", typed.Source);
        Assert.Null(typed.Format);

        var call = Assert.IsType<ExprPart>(Str("\"{f(a:b)}\"").Parts![0]);
        Assert.Equal("f(a:b)", call.Source);
        Assert.Null(call.Format);
    }

    [Fact]
    public void NestedBracesAndStrings_InsideInterpolation()
    {
        var e = Assert.IsType<ExprPart>(Str("\"a {f(\"b}c\")} d\"").Parts![1]);
        Assert.Equal("f(\"b}c\")", e.Source);

        var deep = Assert.IsType<ExprPart>(Str("\"{f(\"x{g}y\")}\"").Parts![0]);
        Assert.Equal("f(\"x{g}y\")", deep.Source);
    }

    [Fact]
    public void Escapes()
    {
        var t = Str("\"a\\nb\\tc\\\"d\\\\e\\{f\\}g\\0\"");
        Assert.Equal("a\nb\tc\"d\\e{f}g\0", ((TextPart)Assert.Single(t.Parts!)).Value);
    }

    [Fact]
    public void EscapedBrace_IsNotAnInterpolation()
    {
        var t = Str("\"\\{x\\}\"");
        Assert.True(t.IsPlainString);
        Assert.Equal("{x}", ((TextPart)t.Parts![0]).Value);
    }

    [Fact]
    public void BareClosingBrace_IsLiteral()
    {
        var t = Str("\"a } b\"");
        Assert.Equal("a } b", ((TextPart)Assert.Single(t.Parts!)).Value);
    }

    [Fact]
    public void ExprPartOffsetAndSpan_PointAtTheExpressionInTheFile()
    {
        const string text = "x := \"ab {name + 1} cd\"";
        var (tokens, _, src) = LexHelper.Lex(text);
        var e = tokens.Single(t => t.Kind == TokenKind.String).Parts!.OfType<ExprPart>().Single();
        Assert.Equal("name + 1", text.Substring(e.Offset, e.Source.Length));
        Assert.Equal(1, e.Span.Line);
        Assert.Equal(e.Offset + 1, e.Span.Col);
        Assert.Equal(e.Source.Length, e.Span.Length);

        // Re-lexing exactly that range yields tokens with correct file spans.
        var diag = new DiagnosticEngine();
        var sub = new Lexer(src, diag, e.Offset, e.Offset + e.Source.Length).Tokenize();
        Assert.False(diag.HasErrors);
        Assert.Equal([TokenKind.Ident, TokenKind.Plus, TokenKind.Int, TokenKind.EndOfFile], sub.Select(t => t.Kind));
        Assert.Equal(e.Span.Col, sub[0].Span.Col);
        Assert.Equal(e.Span.Col + 7, sub[2].Span.Col);
    }

    [Fact]
    public void MultiLineString_IsAllowed_AndSpansLines()
    {
        var (tokens, diag, _) = LexHelper.Lex("\"one\ntwo\" x");
        Assert.False(diag.HasErrors);
        Assert.Equal("one\ntwo", ((TextPart)tokens[0].Parts![0]).Value);
        Assert.Equal(TokenKind.Ident, tokens[1].Kind);
        Assert.Equal(2, tokens[1].Span.Line);
    }

    [Fact]
    public void Unterminated_ReportsAtOpeningQuote_AndStillEmitsToken()
    {
        var (tokens, diag, _) = LexHelper.Lex("x := \"catch\ny := 2");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.UnterminatedString, d.Category);
        Assert.Equal(1, d.Span.Line);
        Assert.Equal(6, d.Span.Col);
        Assert.Contains(tokens, t => t.Kind == TokenKind.String);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void UnterminatedInterpolation_IsUnterminatedString()
    {
        var (_, diag, _) = LexHelper.Lex("\"a {b");
        Assert.Equal(DiagnosticCategory.UnterminatedString, Assert.Single(diag.Diagnostics).Category);
    }

    [Fact]
    public void EmptyInterpolation_IsReported()
    {
        var (_, diag, _) = LexHelper.Lex("\"a {} b\"");
        Assert.Equal(DiagnosticCategory.BadLiteral, Assert.Single(diag.Diagnostics).Category);
        var (_, diag2, _) = LexHelper.Lex("\"a {  } b\"");
        Assert.Equal(DiagnosticCategory.BadLiteral, Assert.Single(diag2.Diagnostics).Category);
    }

    [Fact]
    public void UnknownEscape_IsReported_AndKeptLiterally()
    {
        var (tokens, diag, _) = LexHelper.Lex("\"a\\qb\"");
        Assert.Equal(DiagnosticCategory.BadLiteral, Assert.Single(diag.Diagnostics).Category);
        Assert.Equal("aqb", ((TextPart)tokens[0].Parts![0]).Value);
    }

    [Fact]
    public void HashInsideString_IsNotAComment()
    {
        var t = Str("\"# not a comment\"");
        Assert.Equal("# not a comment", ((TextPart)t.Parts![0]).Value);
    }
}

public class LexerCommentAndTriviaTests
{
    [Fact]
    public void LineComment_RunsToEndOfLine()
    {
        Assert.Equal([TokenKind.Ident, TokenKind.Ident], LexHelper.Kinds("a # comment with \"quote and 'tick\nb"));
    }

    [Fact]
    public void BlockComment_SpansLines()
    {
        Assert.Equal([TokenKind.Ident, TokenKind.Ident], LexHelper.Kinds("a #- line1\nline2 -# b"));
    }

    [Fact]
    public void BlockComments_Nest()
    {
        Assert.Equal([TokenKind.Ident, TokenKind.Ident], LexHelper.Kinds("a #- x #- y -# still comment -# b"));
    }

    [Fact]
    public void LineCommentInsideBlockComment_DoesNotMatter()
    {
        Assert.Equal([TokenKind.Ident], LexHelper.Kinds("#- # hi -# a"));
    }

    [Fact]
    public void UnterminatedBlockComment_IsReported_AtItsOpening()
    {
        var (tokens, diag, _) = LexHelper.Lex("a\n  #- never closed\nb");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.UnterminatedComment, d.Category);
        Assert.Equal(2, d.Span.Line);
        Assert.Equal(3, d.Span.Col);
        Assert.Equal([TokenKind.Ident, TokenKind.EndOfFile], tokens.Select(t => t.Kind));
    }

    [Fact]
    public void SpaceAndNewlineFlags()
    {
        var (tokens, _, _) = LexHelper.Lex("a+b  c\n d # note\n\ne");
        var by = tokens.Where(t => t.Kind == TokenKind.Ident).ToList();
        Assert.True(by[0].SpaceBefore);   // start of input
        Assert.False(tokens[1].SpaceBefore); // '+'
        Assert.False(by[1].SpaceBefore);  // b
        Assert.True(by[2].SpaceBefore);   // c
        Assert.False(by[2].NewlineBefore);
        Assert.True(by[3].NewlineBefore); // d
        Assert.True(by[4].NewlineBefore); // e, after a comment and blank line
    }

    [Fact]
    public void EmptyAndWhitespaceOnly_YieldJustEof()
    {
        Assert.Empty(LexHelper.Kinds(""));
        Assert.Empty(LexHelper.Kinds("  \n\t# nothing\n"));
    }
}

public class LexerSpanAndRecoveryTests
{
    [Fact]
    public void TokenSpans_AreLineColAccurate()
    {
        var (tokens, _, _) = LexHelper.Lex("ab := 12\n  cd");
        Assert.Equal((1, 1, 2), (tokens[0].Span.Line, tokens[0].Span.Col, tokens[0].Span.Length));
        Assert.Equal((1, 4, 2), (tokens[1].Span.Line, tokens[1].Span.Col, tokens[1].Span.Length));
        Assert.Equal((1, 7, 2), (tokens[2].Span.Line, tokens[2].Span.Col, tokens[2].Span.Length));
        Assert.Equal((2, 3, 2), (tokens[3].Span.Line, tokens[3].Span.Col, tokens[3].Span.Length));
        Assert.Equal(TokenKind.EndOfFile, tokens[4].Kind);
        Assert.Equal((2, 5, 0), (tokens[4].Span.Line, tokens[4].Span.Col, tokens[4].Span.Length));
    }

    [Fact]
    public void BadCharacter_IsReported_AndLexingContinues()
    {
        var (tokens, diag, _) = LexHelper.Lex("a ^ b");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(DiagnosticCategory.BadCharacter, d.Category);
        Assert.Equal(3, d.Span.Col);
        Assert.Equal([TokenKind.Ident, TokenKind.Ident, TokenKind.EndOfFile], tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Emoji_IsOneSurrogatePairDiagnostic()
    {
        var (tokens, diag, _) = LexHelper.Lex("a 😀 b");
        var d = Assert.Single(diag.Diagnostics);
        Assert.Equal(2, d.Span.Length);
        Assert.Equal(3, tokens.Count); // a, b, EOF
    }

    [Fact]
    public void ManyErrors_AreAllCollected()
    {
        var (_, diag, _) = LexHelper.Lex("^ \"open\n#- open");
        Assert.Equal(
            [DiagnosticCategory.BadCharacter, DiagnosticCategory.UnterminatedString],
            diag.Diagnostics.Select(d => d.Category).ToArray());
    }

    [Fact]
    public void Diagnostics_RenderWithCaretAndFix()
    {
        var (_, diag, _) = LexHelper.Lex("x := 12abc");
        var sw = new StringWriter();
        new ConsolePrinter(sw, false).Print(diag);
        var text = sw.ToString();
        Assert.Contains("12abc", text);
        Assert.Contains("^^^^^", text);
        Assert.Contains("Fix:", text);
    }

    [Fact]
    public void Lexer_NeverThrows_OnArbitraryInput()
    {
        var rng = new Random(1234);
        const string alphabet = "ab1_ \n\t\"'{}()[]:;.|<>=!&~@\\#-+*/%$?,cx0e·∑😀";
        for (int i = 0; i < 500; i++)
        {
            var chars = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray();
            var (tokens, _, _) = LexHelper.Lex(new string(chars));
            Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
        }
    }
}

public class LexerGoldenFileTests
{
    private static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "golden", name);

    [Theory]
    [InlineData("fizzbuzz.splat")]
    [InlineData("tensors.splat")]
    [InlineData("strings.splat")]
    public void GoldenPrograms_LexWithoutDiagnostics(string file)
    {
        var (tokens, diag, _) = LexHelper.Lex(File.ReadAllText(Golden(file)));
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics.Select(d => d.Message)));
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void FizzBuzz_TokenShape()
    {
        var (tokens, _, _) = LexHelper.Lex(File.ReadAllText(Golden("fizzbuzz.splat")));
        var kinds = tokens.Select(t => t.Kind).ToList();
        Assert.Equal(1, kinds.Count(k => k == TokenKind.Fn));
        Assert.Equal(1, kinds.Count(k => k == TokenKind.Loop));
        Assert.Equal(1, kinds.Count(k => k == TokenKind.If));
        Assert.Equal(2, kinds.Count(k => k == TokenKind.Elif));
        Assert.Equal(1, kinds.Count(k => k == TokenKind.Else));
        Assert.Equal(TokenKind.DotDotEq, kinds.Single(k => k == TokenKind.DotDotEq));
    }

    [Fact]
    public void Tensors_ExerciseTheMathTokens()
    {
        var (tokens, _, _) = LexHelper.Lex(File.ReadAllText(Golden("tensors.splat")));
        var kinds = tokens.Select(t => t.Kind).ToHashSet();
        Assert.Contains(TokenKind.Transpose, kinds);
        Assert.Contains(TokenKind.At, kinds);
        Assert.Contains(TokenKind.Backslash, kinds);
        Assert.Contains(TokenKind.MiddleDot, kinds);
        Assert.Contains(TokenKind.Intg, kinds);
        Assert.Contains(TokenKind.Sum, kinds);
        Assert.Contains(TokenKind.SliceColon, kinds);
        Assert.Contains(TokenKind.StarStar, kinds);
        Assert.Contains(TokenKind.And, kinds);
        // The nested block comment at the top leaves nothing behind.
        Assert.Equal(TokenKind.Ident, tokens[0].Kind);
        Assert.Equal("A", tokens[0].Value);
    }

    [Fact]
    public void Strings_ExerciseInterpolation()
    {
        var (tokens, _, _) = LexHelper.Lex(File.ReadAllText(Golden("strings.splat")));
        var exprs = tokens.Where(t => t.Kind == TokenKind.String).SelectMany(t => t.Parts!).OfType<ExprPart>().ToList();
        Assert.Contains(exprs, e => e.Source == "x" && e.Format == ".3");
        Assert.Contains(exprs, e => e.Source == "f(\"nested {name}\")");
        Assert.Equal(2, tokens.Count(t => t.Kind == TokenKind.Char));
    }
}
