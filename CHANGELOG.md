# Changelog

## v1.4.0 — the correctness release (2026-07-02)

Every fix below was reproduced first, fixed, then proven fixed in an external
test harness: byte-exact comparison against arabic-reshaper 3.0.0 on a 2,295
string corpus, a simulated TextMeshPro for wrap behavior, regression
fingerprints across the whole corpus after every change, and fault-injection
tests that corrupt each pipeline stage on purpose to prove the new verifier
catches it.

### Safety: the bake can no longer write a wrong value

- **Fail-closed measurement gate.** The loss/duplication self-check used to log
  an ERROR and write the value anyway. Now a tripped gate means the value is
  NOT written: file values pass through unbaked, scene boxes are left
  untouched, Localization entries are skipped with the original safe.
  Summaries report `failed=N`.
- **Round-trip verification on every bake.** After baking, the output is
  inverted step by step (un-shape, un-reverse, un-balance, restore recompute,
  placeholder-leak scan) and compared with the source. Any mismatch fails
  closed with the step and position named.
- **Measurement neutralizes clipping.** Overflow modes (Ellipsis/Truncate),
  `maxVisibleCharacters/Words/Lines` and `firstVisibleCharacter` are
  neutralized during the measurement tick and restored afterwards, so a
  display box's settings can never silently truncate what gets measured.

### Resilience: the one catastrophic failure is defused

- **Inversion-proof spaces (NBSP hardening), on by default.** Internal spaces
  in baked lines become no-break spaces (renders identically). If a box is
  ever left with wrapping ON by mistake, the baked lines offer no break
  opportunity: the worst case is a cosmetic overshoot, never the
  bottom-to-top line inversion. Auto-disabled per font (with a Log note) when
  NBSP is missing or its width differs from the space's; the bundled font now
  generates U+00A0.

### Measurement honesty

- **Fallback wired before the first measurement.** Fonts are pre-flighted per
  bake so wrap metrics are never taken against missing glyphs; remaining gaps
  escalate to "wrap may be APPROXIMATE — fix the font and re-bake".
- **Harakat are zero-width in the wrap decision.** Fully vowelized text no
  longer wraps earlier than its unvowelized twin; every mark travels with its
  base letter. A font that gives harakat real advances gets a named warning.

### Text correctness

- **Multi-token LTR runs keep their order.** "New York", "Half Life 2",
  "10 000", "v1.2 beta" no longer reverse (UAX #9 neutral-run coalescing).
  Point placeholders like `{0}` between Latin words join the run; paired
  styling tags still block it so `SwapPairs` stays addressable.
- **Paired tags may cross `<br>`.** Protection is page-level now: a span that
  crosses a forced break closes at the break and reopens on the next line
  instead of degrading into a stray half that styles the wrong words.
- **Paired tags may sit mid-word.** Their placeholders are joining-transparent,
  so `مك<b>تب</b>` keeps its letters connected and bolds exactly the intended
  letters. Point tags (`<sprite>`, `{N}`) still break joining by design.
- **`<` and `>` as text.** Only tag-shaped sequences are protected; `أ < ب > ج`
  shapes and mirrors correctly. A protected tag whose body contains Arabic is
  flagged in the Log (it is probably text, not a tag).
- **Nested placeholders survive.** `{player{0}}` is one atomic unit (nesting
  depth up to 3). Unbalanced or deeper nesting fails closed with a named
  message instead of producing a silently broken format string.
- **Input hygiene.** Every value is NFC-normalized (decomposed hamza/madda
  from CAT tools bakes identical to composed input) and stripped of invisible
  bidi control marks, zero-width spaces and soft hyphens — counted per key in
  the Log. ZWNJ is preserved (it breaks joining on purpose); ZWJ is used for
  joining and then consumed, byte-identical to arabic-reshaper.

### Adapters

- **YAML:** `KEY: "value" # comment` lines are now baked with the comment
  preserved verbatim; escaped quotes inside values resolve correctly even when
  a comment contains a quote; the file's own newline style (LF or CRLF) is
  detected and kept; unquoted Arabic values get a named diagnostic instead of
  a silent skip.
- **CSV:** the file's final newline is preserved on round-trip.

### Internal

- The wrap-rebuild logic moved into a pure, engine-agnostic core
  (`HeshamRTLMeasureCore.cs`) and the verification logic lives in
  `HeshamRTLVerify.cs` — both fully testable outside Unity, which is how this
  release was proven.

## v1.3.4

- UPM immutable-install fix: generated font assets and Localization backups
  are written under `Assets/HeshamRTL/` when the tool is installed from a git
  URL (read-only package folder). Localization integration moved into a
  satellite editor assembly with `defineConstraints` + `versionDefines`.

## v1.3.3

- First public release.
