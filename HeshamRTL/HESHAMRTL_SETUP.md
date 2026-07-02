# HeshamRTL — Unity Editor Tool — Setup & How It Works

An edit-time tool that bakes an Arabic **YAML / CSV / JSON** translation file into
**pre-shaped + pre-reversed + pre-wrapped** text so it renders correctly in
TextMeshPro with **zero runtime additions**. The developer presses one button;
the baked file ships as-is. A second button bakes **every Arabic `TMP_Text` in
the open scene in place**, each measured against its own width.

The span (paired rich-text tag) handling is a direct port of the proven, shipped
mechanics in **Sutoor (سطور)** `arabic_baker.py` — the same `_protect` /
`_balance_spans` / `_swap_pairs` / `visual_order` logic, validated byte-identical
to the original on the reproduction cases. The one difference: Sutoor computes
wrapping itself (advance-sum, tags = zero width); this tool asks **TMP** for the
real break positions instead.

---

## 1. Files

Drop the whole `HeshamRTL/` folder under `Assets/`. It already has the right
two-folder split — **don't flatten it**:

```
HeshamRTL/
  Editor/                            (editor-only code — never ships in a build)
    HeshamRTLShaper.cs
    HeshamRTLBaker.cs
    HeshamRTLWindow.cs
    HeshamRTLAdapters.cs
    HeshamRTLFontBootstrap.cs
    HeshamRTLLocalization.cs           (OPTIONAL — Unity Localization String-Table baker)
    HeshamRTLLocalizationBootstrap.cs  (auto-manages the integration's compile switch)
    Backups/                           (GENERATED — editor-only translation snapshots, never ships)
  Fonts/                             (normal folder — see footprint note below)
    NotoKufiArabic-Regular.ttf
    NotoKufiArabic-Regular.ttf.meta  (pins a stable GUID for the font)
    OFL.txt
    NotoKufiArabic SDF.asset         (GENERATED automatically on first import)
```

| File | Role |
|---|---|
| `HeshamRTLShaper.cs` | Arabic shaping → presentation forms (lam-alef ligature; harakat kept; Latin/numbers untouched). Tables generated from `arabic-reshaper`. Pure C#. |
| `HeshamRTLBaker.cs` | Tag **protect + span classify/pair**, per-line visual reversal (grapheme clusters; Latin/number islands kept LTR; brackets mirrored), **BalanceSpans** + **SwapPairs** (paired-tag support), idempotency + no-Arabic guards. Pure C#. |
| `HeshamRTLWindow.cs` | The Editor window. **Apply (Bake)**: reads a YAML/CSV/JSON file (format picked by extension), page/`<br>` segmentation, real TMP wrap measurement, the `{N}` "Fat-8" width buffer, the loss/duplication self-check, R5/R6 safety checks, fallback-font wiring. **Bake active scene (per-box)**: bakes every Arabic `TMP_Text` in the open scene in place, each measured against its own width, set to NoWrap, undoable. **Auto-resolves the bundled font into the "Fallback Arabic font" field.** |
| `HeshamRTLAdapters.cs` | The decoupled I/O layer (Adapter pattern): `YamlAdapter` (line-oriented, preserves comments/blank lines/formatting), `CsvAdapter` (comma/semicolon/tab auto-detect, RFC-4180 quoting, BOM-tolerant; bakes any cell containing Arabic), `JsonAdapter` (self-contained recursive-descent parser/serializer, nested-aware; bakes any string *value* containing Arabic, never keys). Pure C# — keeps the baker engine-agnostic. |
| `HeshamRTLFontBootstrap.cs` | Editor-only. On first import, **auto-generates the TMP Font Asset** from the bundled `.ttf` (Unicode ranges `20-7E,FB50-FDFF,FE70-FEFF`, SDFAA) and pins it to a stable GUID. No Font Asset Creator step. |
| `HeshamRTLLocalization.cs` *(optional)* | Editor-only **Unity Localization** integration (compiles only when `com.unity.localization` is installed). Adds a window section that bakes the Arabic **String Table(s) in place**: harvests each `LocalizeStringEvent`'s entry key + bound TMP box width, bakes each key against its **narrowest** box (via the same v1.2 engine), writes the result back into the table, and snapshots every original first so the bake is fully **reversible** (Unbake). See §13. |
| `HeshamRTLLocalizationBootstrap.cs` | Editor-only, **always compiles** (no package dependency). Keeps the `HESHAMRTL_LOCALIZATION` compile switch in sync with whether the Localization package is installed, so the integration above turns itself on/off automatically with zero manual steps and zero compile errors. |
| `NotoKufiArabic-Regular.ttf` | Bundled Arabic font. Its `.meta` pins a stable GUID so the generated asset's source reference is deterministic. Covers ASCII + all forms the baker emits. |
| `NotoKufiArabic SDF.asset` *(generated)* | The TMP Font Asset the tool uses as the default fallback. **Not shipped pre-baked** — Unity's own font engine builds its SDF atlas on first import (the only way to get a correct atlas + the right script GUID for your TMP version). |
| `OFL.txt` *(ship with the font)* | SIL Open Font License 1.1 for the bundled font. Keep it next to the `.ttf` if you redistribute. |

All five `.cs` share namespace `HeshamRTL`. `HeshamRTLWindow.cs` and
`HeshamRTLFontBootstrap.cs` use `UnityEditor`/`TMPro`; the other three
(`HeshamRTLShaper.cs`, `HeshamRTLBaker.cs`, `HeshamRTLAdapters.cs`) are plain
C# — `HeshamRTLAdapters.cs` calls into the baker for its Arabic test but
references no `UnityEngine` type, so the parsing/serialization stays engine-
agnostic. Everything under `Editor/` compiles into the **Editor assembly only**
and never ships in a player build.

**Build footprint.** The generated `.asset` lives in `Fonts/` (a *normal* folder,
**not** `Editor/`) on purpose: an Editor-folder asset can't be legally referenced
by a shipped scene. Unity's standard stripping then applies — the font asset (and
its source `.ttf`) end up in a player build **only if you assign them to a
component you ship**. Unreferenced, they're stripped. The *generation logic* never
ships (it's in `Editor/`).

---

## 2. Requirements

- Unity with **TextMeshPro** installed (any recent version — the wrap API is
  auto-detected by reflection, so both modern `textWrappingMode` and legacy
  `enableWordWrapping` work).
- The Arabic file in the **same YAML format as the source**: one
  `KEY: "value"` per physical line, **CRLF** line endings, no multi-line values
  (in-dialogue breaks are tags, not real newlines).

---

## 3. Install

1. Copy the whole `HeshamRTL/` folder into `Assets/` (keep the `Editor/` +
   `Fonts/` split shown in §1).
2. Let Unity compile and import. On first import the bundled TMP Font Asset
   (`Fonts/NotoKufiArabic SDF.asset`) is **generated automatically** — watch for
   `[HeshamRTL] Generated bundled font asset …` in the Console. (One-time; it
   takes a moment while TMP rasterizes the SDF atlas.)
3. That's it. Open **`Tools > HeshamRTL > Open`** and the **Fallback Arabic font**
   field is already filled with the bundled asset. **Zero manual Font Asset
   Creator step.** (If anything errors, see **§9 Troubleshooting**.)

> **First time on a brand-new project?** If TMP's *Essential Resources* aren't
> imported yet, generation **waits** and runs automatically the moment you import
> them (you'll see a one-line `[HeshamRTL] … will be generated automatically …`
> note — no action needed). You never have to touch the Regenerate menu.

> Regenerate at any time via **`Tools > HeshamRTL > Regenerate Bundled Font
> Asset`** (e.g. after deleting it or swapping the bundled `.ttf`).

---

## 4. Run

1. Open **`Tools > HeshamRTL > Open`**.
2. **Target TMP box**: drag in a *scene instance* of the text component whose
   width, font, and size define wrapping (e.g. the dialogue box). Wrapping is
   measured against this exact component.
3. **Fallback Arabic font**: **already pre-filled** with the bundled
   `NotoKufiArabic SDF` asset (see §7) — leave it as-is for zero-setup Arabic, or
   assign your own font to override. Click **Use bundled** to restore the default.
4. **Arabic file (input)**: browse to your Arabic YAML.
5. **Baked file (output)**: auto-suggested as `*_baked.yml` (change if you want).
6. **Set target box alignment to Right** *(toggle, default ON)*: on bake, sets the
   target box's **horizontal** alignment to Right (vertical alignment is left
   untouched) — Arabic reads RTL, so a default left-aligned box looks wrong. Turn
   it off to keep your own alignment.
7. Click **Apply (Bake)** and read the **Log**.
8. **Set the runtime display component to `NoWrap`** (see §6 — mandatory).
9. Ship the baked file in place of the Arabic file.

> Different boxes with different widths? Run once per box (each run measures
> against the selected component).

---

## 5. What it does (pipeline)

For every `KEY: "value"` line:

1. **Guards.** If the value is already baked (contains presentation forms) or
   contains no Arabic at all → passed through unchanged.
2. **Page split.** Split on `<page>`, `<hpage>`, `<page=X>` — kept **verbatim**
   as hard boundaries; each page baked independently.
3. **Forced lines.** Within a page, split on `<br>`.
4. **Protect (before measuring).** Replace every tag with a single placeholder
   char allocated from a PUA codepoint **not used by the file** (so game-icon
   glyphs in `U+E000–F8FF` are never collided with). Paired rich-text tags
   (`<b>`, `<i>`, `<color>`, `<size>`, `<link>`, …) are classified open/close and
   **paired by nesting**; everything else (`<sprite=…>`, `{0}`, `\"`, unmatched
   halves) is an atomic point tag.
5. **Shape once.** Convert the segment's Arabic to presentation forms (lam-alef
   becomes one glyph; harakat preserved; placeholders/Latin/digits untouched).
6. **Measure real breaks.** Set the shaped text on the target TMP box in
   naive-LTR with wrapping **ON** and rich-text **OFF**, `ForceMeshUpdate()`, and
   read the exact break positions TMP will use (same width, same spaces). Tag
   placeholders are one visible char here, so they never get eaten by the
   rich-text parser; under the NoWrap contract their width is cosmetic only.
7. **Rebuild lines safely.** Each wrapped line is reconstructed from the actual
   per-character **source indices**, with a **loss / duplication / contiguity
   self-check**; boundary spaces trimmed.
8. **Balance spans.** A paired span straddling a wrap is **closed at the line end
   and reopened at the next line start** (Sutoor `_balance_spans`), so the engine
   never styles across wrong ranges.
9. **Reverse + swap per line.** Reverse grapheme clusters; keep Latin/number
   islands (`config.json`, `v1.2`, `50%`) in internal order; mirror brackets;
   then **swap each pair's open/close** (Sutoor `_swap_pairs`) so positional
   reversal doesn't leave the close before the open.
10. **Restore + reassemble.** Restore placeholders → original tags; join lines
    with `<br>`, join pages with their page tags.
11. **Write.** Only the value is changed; key and structural quotes untouched;
    **CRLF preserved**; UTF-8 (no BOM).

Result: nothing is left for TMP to wrap or shape at runtime, so **line order
cannot invert**.

---

## 6. Runtime contract (critical — R10)

The baked text is valid **only** under a naive-LTR renderer with wrapping off.
On the component that displays the baked text at runtime:

- `isRightToLeftText = false` (we already did RTL)
- built-in RTL/bidi **off**
- **rich text ON** (so the preserved `<color>`/`<b>`/… actually style — only the
  *display* box needs rich text on; the tool turns it off only while measuring)
- **NoWrap** — `textWrappingMode = TextWrappingModes.NoWrap` (modern TMP) or
  `enableWordWrapping = false` (legacy)

If wrapping is left on, TMP re-wraps the already-wrapped reversed text and the
**line order inverts**. Undershoot (a line clipping past the edge if the box is
later resized) is only cosmetic and is fixed by re-baking; inversion is the only
catastrophic failure, and NoWrap prevents it.

Around measurement the tool saves and restores the target box's text, RTL flag,
rich-text flag, and wrap setting — those are never left changed. The **one**
persistent edit it makes to the target box is setting its **horizontal** alignment
to Right at bake time (default on; toggle off in the window to keep your own).
Vertical alignment is never touched, and the change is undoable (Ctrl+Z).

---

## 7. Fonts & tofu (R6) — the bundled fallback (now automatic)

If the target box's font is missing the Arabic presentation forms the baker
emits, they render as empty boxes (tofu). This is now handled **automatically**:

1. On first import, `HeshamRTLFontBootstrap` generates `NotoKufiArabic SDF`
   from the bundled `.ttf` with **Character Set = Unicode Range (Hex)**
   `20-7E,FB50-FDFF,FE70-FEFF`, render mode SDFAA — i.e. exactly what you'd enter
   in Font Asset Creator, done for you by TMP's engine. (Population mode is
   **Dynamic**, so any glyph outside those ranges still resolves on demand — an
   extra safety net against tofu.)
2. The window **pre-fills** that asset into **Fallback Arabic font**.
3. Keep **"Auto-add fallback to target font when forms are missing"** ON.
4. **Bake.** When R6 detects missing forms, the tool checks the fallback covers
   them and **adds it to the target font asset's fallback chain** (a persistent
   edit-time change to that font asset, logged clearly). Runtime then resolves
   the missing glyphs through the fallback — no per-scene changes needed.

So on a clean project: import → bake → display → correct Arabic, **no manual
steps**. The bundled font is OFL-1.1 (`OFL.txt`); using it as a fallback puts no
restriction on your game.

**Override.** Assign your own Arabic TMP Font Asset to **Fallback Arabic font** at
any time — your choice is never overwritten. **Use bundled** restores the default.

**Manual recovery (only if auto-generation can't run** — e.g. a very old TMP
without `CreateFontAsset`, see §9): *Window > TextMeshPro > Font Asset Creator* →
Source Font = the bundled ttf → Character Set = Unicode Range (Hex) →
`20-7E,FB50-FDFF,FE70-FEFF` → Generate → **Save next to the ttf as
`NotoKufiArabic SDF`**. The window then auto-resolves it.

**Want it stripped of the source `.ttf` in builds?** The Dynamic asset references
the `.ttf` (so the ttf travels into a build *only if you ship a component using
the asset*). If you'd rather ship a self-contained Static atlas with no ttf
dependency, open the generated asset's **Atlas & Material** and switch population
to **Static** (it already contains the warmed ranges). Functionally identical for
baked text, since the baker only emits glyphs in those ranges.

---

## 8. Built-in safety checks (read the Log)

- **R5 — PUA placeholders.** Tag placeholders are allocated from PUA codepoints
  **not used by the file**, so game icon/button glyphs (`U+E000–F8FF`) pass
  through untouched as atomic units (their position flips with the line, which is
  correct). No abort.
- **R6 — font tofu + fallback.** Warns about any emitted presentation form
  missing from the target box's base font, then wires the assigned fallback (§7).
- **Wrap self-check.** Every bake verifies no character is lost, duplicated, or
  reordered across wrap lines. Anomalies are logged with the key and counted; a
  final line flags if any key tripped the guard. Inspect those keys.
- **Multi-line guard.** A `KEY: "…` that opens a quote with no closing quote on
  the same physical line is reported and left un-baked (the format expects one
  line per value).
- **NoWrap reminder.** If the measured box itself isn't NoWrap, a reminder fires
  that the *display* box must be NoWrap.
- **Auto-size warning.** If a target/scene box has TMP auto-sizing (Best Fit)
  enabled, a warning fires: it is incompatible with pre-baked wrap (variable font
  size vs. frozen break points) and must be turned off for the wrap to hold (§12.3).

---

## 9. Troubleshooting

- **Compile error on wrapping property.** Shouldn't happen (reflection picks
  `textWrappingMode` or `enableWordWrapping`). If it does, your TMP exposes
  neither name — report it.
- **Wrap looks wrong / lines clipped.** Confirm the runtime box width matches
  the box you measured against, and that it is NoWrap. Re-bake against the
  actual box.
- **Color/bold lands on the wrong word.** The span open/close pairing assumes
  **properly nested** tags (like valid HTML). Overlapping tags
  (`<b>…<color>…</b>…</color>`) are invalid and not supported.
- **A `<color>` span loses its color after a hard `<br>`.** Paired spans are
  balanced **within** a `<br>` segment; a span that opens before a `<br>` and
  closes after it is auto-closed at the break (safe, never inverts) and its
  trailing half becomes inert. Keep paired spans inside one `<br>` segment. (Same
  limitation as Sutoor's cross-`\n` behavior.)
- **Mid-word tag breaks letter joining.** A tag *inside* a word
  (`كتا<b>بي`) breaks Arabic joining at that point, because shaping treats a tag
  boundary like a word boundary. Put paired tags at word/phrase boundaries (the
  normal case).
- **`NON-CONTIGUOUS source indices` warning.** TMP reordered characters on a
  line (unexpected in naive-LTR). Paste the log; this is the case to inspect.
- **Tofu (empty boxes) in-game.** The bundled fallback is normally auto-wired
  (§7). If you see tofu: confirm **Fallback Arabic font** is set (click **Use
  bundled**), the **Auto-add fallback…** toggle is ON, and you re-baked. If the
  `Fonts/NotoKufiArabic SDF.asset` is missing, run **Tools > HeshamRTL >
  Regenerate Bundled Font Asset**. If your TMP is too old to auto-generate, do the
  one-time manual build in §7 (Manual recovery).
- **`{0}`/`{1}` mis-rendered.** Kept as literal LTR placeholders, substituted by
  the engine at runtime. Inject only Western-digit/Latin values; Arabic-injected
  values have no offline fix.

---

## 10. Scope / limitations

- **Paired/range tags are supported** (`<color>…</color>`, `<b>`, `<i>`, `<u>`,
  `<s>`, `<size>`, `<mark>`, `<sub>`, `<sup>`, `<link>`, `<font>`, gradients,
  case tags, `<alpha>`, etc.) via Sutoor's span mechanics — including spans that
  wrap across measured lines.
- Plus the Silksong-style tag set: `<page>`, `<hpage>`, `<page=X>`, `<br>`,
  `{0}`, `{1}`, `<sprite=…>` (atomic).
- Paired spans must be **properly nested**. Since v1.4 they may freely cross
  `<br>` segments (protection is page-level: a crossing span closes at the break
  and reopens on the next line) and may sit **mid-word** (paired-tag placeholders
  are joining-transparent, so letters stay connected). Point tags (`<sprite>`,
  `{N}`) still break joining by design — they occupy visible width.
- One target box per run.
- Line-based YAML parsing for `KEY: "value"`; escaped quotes inside values are
  protected as atoms.
- Arabic-Indic digits are not treated as LTR islands (policy: Western digits
  only).
- **`{N}` placeholders that inject unbounded text** (player/item/location names)
  do **not** belong in the pre-baked path: their width is reserved by a fixed,
  configurable buffer, so a long injected value overflows under NoWrap. The tool
  reports every placeholder-bearing field and lets you raise the reserve, but a
  genuinely unbounded field should get a fixed font size sized for the worst case,
  or be shaped at runtime. See §12.3.

---

## 11. Verifying a bake

1. Assign the **baked** file + the injected Arabic font to the runtime box set
   to **NoWrap** with **rich text ON**; confirm in-scene that Arabic reads
   correctly (connected letters, right-to-left, correct top-to-bottom line order)
   and that any `<color>`/`<b>` styling lands on the right words.
2. **Idempotency:** re-baking an already-baked file leaves it unchanged.

---

## 12. Revision 3 (v1.1) — what's new

Four additions. The core baking pipeline (Protect → Shape → measure → BalanceSpans
→ VisualOrder → SwapPairs → Restore) is **unchanged** — it is only now reached
through a format adapter and can be pointed at any box.

### 12.1 Decoupled I/O — YAML / CSV / JSON (the Adapter pattern)
`HeshamRTLAdapters.cs` splits the tool into `[Input Parser] → [Baker] → [Output
Serializer]`. The format is chosen by the **input file extension**; the output is
written in the **same format**:

- **`.yml` / `.yaml` / `.txt`** → `YamlAdapter`. Line-oriented and
  **format-preserving**: comments, blank lines, indentation and trailing comments
  are written back verbatim; only the `"value"` of each `KEY: "value"` line is
  baked. (This is the original behaviour, now factored out.)
- **`.csv`** → `CsvAdapter`. Delimiter (`,` `;` or tab) auto-detected from the
  first line; RFC-4180 quotes and embedded newlines handled; UTF-8 BOM tolerated.
  **Any cell that contains Arabic is baked**; every other cell (keys, IDs, English
  source, notes) is passed through untouched, so the column layout is irrelevant.
- **`.json`** → `JsonAdapter`. A small self-contained recursive-descent
  parser/serializer (no external dependency); nested objects/arrays are supported.
  **Any string _value_ that contains Arabic is baked**; object **keys are never
  touched**, and numbers/booleans/null pass through verbatim. Output is
  re-serialized as 2-space-indented JSON.

A UTF-8 BOM on the input is preserved on the output. The Log prints the detected
format and (for CSV) the delimiter.

### 12.2 Scene batch — per-box wrapping (`Bake active scene`)
The single **Target TMP box** measures every value against *one* width, which is
wrong for differently-sized UI elements. The new **Bake active scene (per-box)**
button instead walks **every `TMP_Text` in the open scene** (including inactive
objects), and for each one that contains *un-baked* Arabic:

1. measures the wrap against **that box's own RectTransform width**,
2. writes the baked string back **in place**,
3. sets the box to **NoWrap** (so the runtime contract is satisfied right there),
4. applies Right alignment per the toggle.

It is **undoable** (one Undo step) and marks the scene dirty. Already-baked,
non-Arabic and empty boxes are skipped. The Target-box / file fields are not used
by this button. (Note: editing a box that is part of a prefab instance records a
prefab override, as expected.)

### 12.3 `{N}` runtime placeholders — the "Fat-8" width buffer (and its hard limit)

> **PLAIN LIMITATION — READ THIS.** A `{N}` whose runtime value is **unbounded text**
> (a player name, item name, location) is **fundamentally not a good fit for pre-baked
> wrap.** Pre-baking freezes the line breaks for one width at one fixed font size; an
> injected name can be far wider than any fixed reservation and will **overflow under
> NoWrap** (or push content past the frozen breaks). This is a real limitation of the
> bake-time approach, **not a bug.** For such a field, do one of:
> 1. give the box a **fixed font size and size it for the worst-case** value, or
> 2. **shape that one field at runtime** (leave it out of the pre-baked path).

A `{0}` measures as one narrow glyph, but at runtime it is replaced by a wider value —
which clips under NoWrap. To reserve space, during the **measurement tick only** each
numbered placeholder (already a single protected unit by then) is widened to a number of
digit glyphs, then the break positions are mapped back so the placeholder stays **one
atomic unit** in the baked line. The baked output still contains the original `{N}`. If a
reserved buffer ever wraps across a line (box narrower than the buffer), the placeholder is
kept whole on its first line and a `Fat-8` warning is logged (wrap there is approximate).

**Configurable per bake.** The reservation is the **"Placeholder width reserve ({N})"**
field in **Shared settings** (default **6**, range 1–256), used by all three bakers. `6`
fits a number; **raise it** for fields known to inject longer text. It is measurement-only —
the baked value always keeps `{N}`. (Widening the reserve helps a *bounded* longer value;
it cannot rescue genuinely unbounded text — see the callout above.)

**Reported per key.** Whenever a value contains `{N}` placeholders, the Log prints a
`NOTE key[…]` line stating how many it has and the reserve used, so you always know which
fields' on-screen fit depends on the injected value's length. Search the Log for `NOTE key`
to list every placeholder-bearing field and audit the risky ones.

**Why there is no auto-sizing valve.** TMP **auto-sizing (Best Fit) is fundamentally
incompatible with pre-baked wrap** and the tool never enables it. Pre-baked wrap freezes the
`<br>` break points for one width × **one fixed font size**; Best Fit changes the font size at
display time, so the frozen breaks no longer fit and the text overflows — and because Best Fit
is a **box-wide** property, it breaks *every* line in that box, including lines with no
placeholder at all. So if a target/scene box has auto-sizing enabled the tool **logs an
`auto-size` warning** — disable it (set a fixed font size) for the baked wrap to hold.

### 12.4 Normalized line-break splitting
Forced line breaks now split on `<br>` / `<br/>` / `<br />`, an **escaped** `\n`
(as it appears inside a quoted YAML scalar) **and** a **real** newline (as a
decoded CSV/JSON value can carry) — so every adapter feeds the baker correctly.
---

## 13. Revision 4 (v1.2 → v1.3) — Unity Localization integration (optional)

A new, **optional** front-end that bakes the Arabic **String Table** directly, so a
translator never pastes Arabic into scene boxes. It appears as a section in the
window **only when the `com.unity.localization` package is installed** — otherwise
it does not exist and the rest of the tool is completely unaffected. The full design
note (algorithm, anti-corruption strategy, and the exact Localization API used) is in
`LOCALIZATION_INTEGRATION.md`; the summary:

### 13.1 The problem it solves
Real games don't write Arabic in each box — strings live in **String Tables** (one per
locale) and load at runtime through a **`LocalizeStringEvent`** on each box. So the bake
must target the **Arabic table**, not the scene. But a table is only *key → value*: it
carries no width, and width is exactly what wrapping needs.

### 13.2 The missing link
Each `LocalizeStringEvent` supplies **both halves**: the entry key
(`StringReference.TableReference` + `TableEntryReference`) **and**, via its
`OnUpdateString` target, the **TMP box** whose `RectTransform` carries width + font +
size. The tool harvests `(key ↔ box)` pairs from the loaded scene(s), groups boxes per
key, bakes each key's Arabic value against its **narrowest** box (so text baked on the
tightest box never overflows a wider one), and writes the result **back into the table**.
The game keeps pulling by key — **zero manual paste, zero runtime code, the game's own
loading path untouched.** (A runtime `ITablePostprocessor` was deliberately **rejected**:
it would ship shaping that runs on every table load — the opposite of this tool's contract.)

### 13.3 Same engine, reused wholesale
The baking is the unchanged v1.2 core — `BakeValue` → Protect / Shape / **MeasureWrap (TMP)**
/ BalanceSpans / VisualOrder / SwapPairs / Restore — plus the `{N}` "Fat-8" buffer, R5/R6,
the bundled fallback-font wiring, and the NoWrap / auto-size warnings. Because it measures
on the **real** narrowest box, the box's true width/font/size/margins drive the wrap
(strictly better than a width number). Since v1.3.4 the integration lives in its own
**satellite editor assembly** (`Editor/Localization/`, its own asmdef with
`defineConstraints: HESHAMRTL_LOCALIZATION` + `versionDefines` on
`com.unity.localization`) and plugs into the window through a registration hook
(`HeshamRTLWindow.LocalizationSection`, granted access via `InternalsVisibleTo`).
Same surface, no duplication — and the assembly simply does not load when the
package is absent, so a hard assembly-load failure is impossible.

### 13.4 Corruption is impossible (the governing rule)
The tool is generic — many translators work straight in the Arabic table with no separate
source — so it adopts **strategy (c)**: bake only **raw** text, detect already-baked values
with `IsBaked` and **skip** them, and **snapshot** each entry's original **before** its
first bake into an **editor-only sidecar** (`Editor/Backups/…json`, keyed by
collection-GUID · locale · key-id; never ships, zero runtime footprint). Consequences:

- **First bake** → snapshot the raw, then bake.
- **Re-bake** (e.g. you changed a box's width) → restore from the snapshot, then re-bake.
- **Re-translated** (entry is raw again and differs) → the new raw becomes the snapshot, then bake.
- **Already baked with no snapshot** (pasted by hand, or sidecar lost) → **left untouched**.
- **Unbake** button → restores every baked entry to its exact original raw.

So no matter how the translator works, their translation **cannot be lost**.

### 13.5 The compile switch (automatic)
`HeshamRTLLocalizationBootstrap.cs` (which has **no** dependency on the package) probes
whether Localization is installed and toggles the `HESHAMRTL_LOCALIZATION` define
accordingly — adding it when the package is present, clearing it the moment the package is
removed (before the recompile). Together with the satellite asmdef's `defineConstraints`
(see 13.3) this is a two-layer guard: the define gates compilation AND the assembly never
loads without the package. You never touch a setting. *Manual recovery, only if the
package was deleted straight off disk:* remove `HESHAMRTL_LOCALIZATION` from **Project
Settings → Player → Scripting Define Symbols** (editable even while scripts fail to compile).

### 13.6 How to use it
1. Install **Window → Package Manager → Unity Localization** and set up your String Table
   Collection with an Arabic locale (`ar`, or e.g. `ar-SA`) as usual.
2. Open the scene(s) whose `LocalizeStringEvent` boxes you want measured.
3. **Tools → HeshamRTL → Open**, expand **"Unity Localization — bake Arabic String Table"**,
   leave the locale code blank to auto-detect `ar*` (or type an exact code), and press
   **Bake Arabic String Table(s)**.
4. Make sure every box that **displays** a baked entry is **NoWrap** with a **fixed** font
   size (the same runtime contract as everywhere else). Press **Unbake** anytime to revert.

> **Right-alignment is per-locale here.** Because one box renders every locale, the tool does
> not set the box's alignment (that would right-align English/French too). With the
> "Set alignment to Right" toggle on, it instead embeds an `<align=right>` tag at the front of
> the **Arabic value only**, so other locales keep their alignment. This needs **Rich Text**
> enabled on the box (TMP default); the tool warns if it is off. *If you baked with the very
> first v1.3 build (which set box alignment), reset those boxes' alignment to their default once
> and re-bake.*

### 13.7 Limitations specific to this mode
- Only boxes in **loaded scenes** contribute a width — boxes that exist solely in unopened
  prefabs are skipped (open a scene that instantiates them, or an instance).
- A box whose width is **resolved only at runtime** by a Layout Group reads as width 0 at
  edit time and is skipped with a warning (give it a fixed width, or open it laid out).
- Keys with no Arabic entry yet are skipped (translate first, then bake).


## 14. Revision 5 (v1.3 → v1.4) — the correctness release

Every change below is enforced in code and proven by an external test harness
(byte-exact assertions against arabic-reshaper 3.0.0 and a simulated TMP,
including fault-injection tests). Behavior highlights:

### 14.1 Fail-closed baking (the guard is now a gate)
The loss/duplication self-check no longer just logs: if a measured value cannot
be rebuilt from its source with zero loss and zero duplication, the value is
**not written** (file path: passthrough; scene: box untouched; Localization:
entry skipped — original safe). Summaries report `failed=N`. Measurement also
neutralizes anything that can clip `textInfo` (Ellipsis/Truncate overflow,
`maxVisible*`, `firstVisibleCharacter`) and restores it afterwards.

### 14.2 Round-trip verification (every bake is proven)
After each bake the pipeline is inverted step by step — un-shape, un-reverse,
un-balance, restore recompute + placeholder-leak scan — and compared with the
source. Any mismatch fails closed with the step and position named. A baked
line is either mathematically equal to its source, or it is not written.

### 14.3 Inversion-proof spaces (NBSP hardening, on by default)
Internal spaces in baked lines become no-break spaces (identical rendering).
If a box is ever left with wrapping ON by mistake, the line offers no break
opportunity: the worst case is a cosmetic overshoot, never the bottom-to-top
line inversion. Auto-disabled per font (with a Log note) when NBSP is missing
or its width differs from the space; the bundled font now includes U+00A0.
Toggle: "Inversion-proof spaces (NBSP)" in the window.

### 14.4 Honest measurement
- Fallback fonts are wired **before** the first measurement (pre-flight), so
  wrap metrics are never taken against missing glyphs; if forms are still
  missing afterwards the log says wrap may be APPROXIMATE and asks for a re-bake.
- Harakat are zero-width in the wrap decision (they no longer inflate line
  width and cause premature wrapping); each mark travels with its base letter.
  If a font gives harakat a real advance, a named warning says so.

### 14.5 Text correctness fixes
- Multi-token LTR runs keep their order: "New York", "Half Life 2", "10 000",
  "v1.2 beta" no longer reverse (UAX #9 run coalescing; paired-tag placeholders
  still block the merge so styling stays addressable).
- Paired tags may cross `<br>` and sit mid-word (see §7 limitations update).
- `< ب >` and other math/text uses of `<` `>` are TEXT now: only tag-shaped
  `<name …>` sequences are protected (letters/#, optional attributes, optional
  self-close). A protected tag whose body contains Arabic is flagged in the Log.
- `{player{0}}`-style nested placeholders are one atomic unit (depth ≤ 3);
  unbalanced or deeper nesting fails closed with a named message.
- Input hygiene at the bake boundary: NFC normalization (decomposed hamza/madda
  input bakes identical to composed) and stripping of invisible bidi controls,
  ZWSP and soft hyphens — counted per key in the Log. ZWNJ is preserved; ZWJ is
  used for joining and then consumed, byte-identical to arabic-reshaper.
- YAML: `KEY: "value" # comment` lines are baked with the comment preserved
  verbatim; escaped quotes resolve correctly; the file's own newline style (LF
  or CRLF) is kept; unquoted Arabic values get a named diagnostic. CSV keeps
  the file's final newline.
