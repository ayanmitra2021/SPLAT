# SPLAT design decisions

One line per decision, newest last. Format: `[Phase N] decision — reason`.

- [Phase 1] Target `net10.0` (SDK 10.0.404 installed); warnings are errors via `Directory.Build.props`.
- [Phase 1] `SourceSpan` lines/columns are 1-based; `Length` counts UTF-16 chars; end-of-file spans have length 0 and render with one caret.
- [Phase 1] A span running past its first line is underlined to the line end plus `...`; tabs in the source are copied into the caret padding so alignment holds at any tab width.
- [Phase 1] Spec section 9 messages are verbatim in `Templates`; the `Fix:` line is separate technical text. Quip rotation (new categories only) is seeded by line number so output is deterministic.
- [Phase 1] Color only when stderr is not redirected and `NO_COLOR` is unset.
