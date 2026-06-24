# Font packaging change — zero-setup Arabic (no Font Asset Creator step)

## What changed
- **New:** `Editor/HeshamRTLFontBootstrap.cs` — on first import it generates
  `Fonts/NotoKufiArabic SDF.asset` from the bundled `NotoKufiArabic-Regular.ttf`
  (Unicode ranges `20-7E, FB50-FDFF, FE70-FEFF`, render mode **SDFAA**) and pins it
  to a **stable GUID** (`fe09dc87ba65fe43dc21ba09fe87dc65`).
- **New:** `Fonts/NotoKufiArabic-Regular.ttf.meta` — pins the ttf's GUID
  (`ab12cd34ef56ab78cd90ef12ab34cd56`) so the generated asset's source reference is
  deterministic, and embeds font data so TMP can read the outlines.
- **Changed:** `Editor/HeshamRTLWindow.cs` — the **Fallback Arabic font** field is
  now auto-resolved to the bundled asset on a fresh open, with a **Use bundled**
  button. Manual override is preserved (assigning your own font wins and is never
  overwritten). *No baking-pipeline code was touched.*
- **Unchanged:** `Editor/HeshamRTLShaper.cs`, `Editor/HeshamRTLBaker.cs`,
  `Fonts/NotoKufiArabic-Regular.ttf`, `Fonts/OFL.txt`.

Result: import → bake → display → correct Arabic, **no manual Font Asset Creator
step, no tofu**.

## The one deviation from "ship a pre-baked .asset", and why
A TMP Font Asset's SDF atlas is produced by Unity's **native font engine**
(FreeType + Unity's SDF generator). It cannot be authored correctly outside Unity,
and a hand-written `.asset` would also have to embed a `TMP_FontAsset` **script
GUID** that may not match your installed TMP version — yielding a "missing script"
asset, which is a *worse* failure than the manual step we're removing.

So instead of shipping a fragile pre-baked binary, the package generates the
`.asset` **once, in your editor**, via TMP's own public API
(`TMP_FontAsset.CreateFontAsset`). Unity builds a correct atlas with the right
script GUID for your project, the result is committed as a normal asset with the
pinned GUID, and the integrator does nothing. Net effect is identical to (more
robust than) a shipped `.asset`.

If you specifically need a literal `.asset` file inside the zip, the only reliable
way is to run that one generation and commit the output — which the bootstrap does
for you automatically on first import.

## Build footprint
The generated `.asset` is in `Fonts/` (a normal folder), **not** `Editor/`, because
an Editor-folder asset can't be referenced by a shipped scene. Unity's normal
stripping then applies: the font asset (and its `.ttf`) ship **only if you assign
them to a component you ship**; unreferenced, they're stripped. The generation
*code* is in `Editor/` and never ships.

Population mode is **Dynamic** (renders any glyph on demand → extra anti-tofu
safety). To drop the runtime `.ttf` dependency, switch the generated asset to
**Static** (it already contains the warmed ranges) — see §7 of the setup doc.

## License
`Fonts/OFL.txt` (SIL OFL 1.1) ships next to the `.ttf`, as required. Using the font
as a fallback places no restriction on your game.

---

## Revision 2 — refinements after first-project testing

1. **Self-healing generation when TMP isn't imported yet.** On a brand-new project
   with no TMP Essentials, `CreateFontAsset` used to throw once and give up. Now the
   bootstrap gates on TMP readiness (presence of the `TMP_Settings` asset). If TMP
   isn't ready it **defers quietly** (one friendly console note, no error) and
   re-arms on `AssetDatabase.importPackageCompleted` (the TMP Essentials import) and
   on each domain reload — so generation happens automatically once TMP is in, with
   no trip to the Regenerate menu.
2. **Auto-set target box alignment to Right on bake.** New toggle *Set target box
   alignment to Right* (default ON). At bake time the target box's **horizontal**
   alignment is set to Right (vertical untouched), undoable and dirtied so it
   persists. Turn it off to keep a custom alignment. This is the one persistent edit
   the tool makes to the target box (see §6 of the setup doc).
3. **Rebrand `Arabic Baker` → `HeshamRTL`.** Window title, menu paths
   (`Tools/HeshamRTL/Open` and `Tools/HeshamRTL/Regenerate Bundled Font Asset`), log
   prefix (`[HeshamRTL]`), namespace (`HeshamRTL`), class names (`HeshamRTLWindow`,
   `HeshamRTLBaker`, `HeshamRTLShaper`, `HeshamRTLFontBootstrap`) and the source file
   names. **Unchanged on purpose:** the pinned GUID constants, the font file name
   `NotoKufiArabic-Regular.ttf`, and the generated asset name `NotoKufiArabic SDF.asset`
   (font identity, separate from the tool brand). Also resolved a latent menu-path
   collision by moving the window opener under the `HeshamRTL` submenu as `Open`.

## Revision 3 (v1.1) — note

v1.1 adds an I/O adapter layer (`Editor/HeshamRTLAdapters.cs`: YAML/CSV/JSON), a
per-box **scene batch** bake, and `{N}` width reservation. None of this touches
font packaging — the bootstrap, the pinned GUIDs and the bundled `.ttf` are
**unchanged**. See `HESHAMRTL_SETUP.md` §12 for those features.
