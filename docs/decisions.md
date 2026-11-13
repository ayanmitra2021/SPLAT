# SPLAT design decisions

One line per decision, newest last. Format: `[Phase N] decision — reason`.

- [Phase 1] Target `net10.0` (SDK 10.0.404 installed); warnings are errors via `Directory.Build.props`.
- [Phase 1] `SourceSpan` lines/columns are 1-based; `Length` counts UTF-16 chars; end-of-file spans have length 0 and render with one caret.
- [Phase 1] A span running past its first line is underlined to the line end plus `...`; tabs in the source are copied into the caret padding so alignment holds at any tab width.
- [Phase 1] Spec section 9 messages are verbatim in `Templates`; the `Fix:` line is separate technical text. Quip rotation (new categories only) is seeded by line number so output is deterministic.
- [Phase 1] Color only when stderr is not redirected and `NO_COLOR` is unset.
- [Phase 2] `#- -#` block comments nest, so commented-out code may itself contain block comments. An unterminated one is reported at its opening `#-`.
- [Phase 2] Strings may span lines. Escapes: `\n \t \r \0 \" \ \{ \}`; an unknown escape is an error but is kept literally. A literal `{` must be written `\{`; a bare `}` is literal.
- [Phase 2] A string always lexes to one `String` token carrying `Parts` (`TextPart` / `ExprPart`). `ExprPart.Offset` is the file offset of the expression, so the parser re-lexes that range with `new Lexer(src, diag, offset, offset+len)` and gets correct spans. The format spec starts at the first top-level single `:` (not `::`/`:=`, not inside `()[]{}` or nested strings) and runs to the `}`.
- [Phase 2] `c"..."` is a `Char` token only when `c` is immediately followed by `"`; value is the Unicode scalar (int). Must contain exactly one scalar.
- [Phase 2] Integer literal value is `ulong` (range checking against i32/etc. and negation are the type checker's job); float value is `double`. Underscores are allowed anywhere inside digit runs. A number running straight into a letter/digit/underscore (`12abc`, `0b102`) is an error.
- [Phase 2] `:` inside `[...]` (innermost open bracket is `[`) is `SliceColon`; inside `(...)` within brackets it is a block `Colon` again. `::` and `:=` are always `DoubleColon` / `ColonAssign`.
- [Phase 2] `'` is `Transpose` only after Ident, `)`, `]`, `me`, or another transpose; `''` is one `ConjTranspose` token (so `A'''` = `''` then `'`). Elsewhere it is an error whose fix points at `c"a"`.
- [Phase 2] `||` is two adjacent `Pipe` tokens; the parser tells norm bars from lambda bars using `Token.SpaceBefore` (no space = adjacent). `|>` is `PipeGreater`, so write `||v|| > 3` with a space, not `||v||>3`.
- [Phase 2] `>>` is always `GreaterGreater`; the parser must split it when closing nested generics (`Maybe<Maybe<i32>>`).
- [Phase 2] Norm suffixes `_1`, `_inf`, `_F` lex as ordinary identifiers directly after the closing bars (`SpaceBefore == false`); the parser consumes them.
- [Phase 2] `grad/∂`, `intg/∫`, `sum/∑`, `prod/∏` share one `TokenKind` each. Because `sum`/`prod`/`grad`/`intg` are lexed as keywords, the parser (Phase 4/14) must accept them as plain function names when not followed by `[` (reduction/integral) or `(` (grad); until then they are reserved.
- [Phase 2] Extra assignment operators lexed beyond the spec list: `*=`, `/=`, `./=`. Bad characters and bad literals never stop lexing; `Lexer.Tokenize` never throws and always ends with `EndOfFile`.
- [Phase 2] `splat tokens file.splat` dumps tokens (stdout) and diagnostics (stderr, exit 65).
- [Spec v0.3] Added Milestone 5, the built-in data engine ("crates", spec §11, build plan Phases 19-28). It needs these later lexer changes (Phase 20): keyword `crunch`, duration literals (`5s`, `1mo`), date/stamp literals (`d"..."`, `t"..."`). The Phase 2 lexer is unchanged until then; today `5s` is reported as a bad number.
