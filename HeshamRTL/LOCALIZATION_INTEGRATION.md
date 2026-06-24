# HeshamRTL — Unity Localization integration (design + API reference)

This documents the **optional** String-Table baker added in v1.3
(`HeshamRTLLocalization.cs` + `HeshamRTLLocalizationBootstrap.cs`). It is self-contained
and reuses the v1.2 baking core unchanged.

---

## 1. Why a table baker

Real games do not type Arabic into each TMP box. Strings live in **String Tables** — one
table per locale inside a *String Table Collection* — and at runtime each box gets its text
from a **`LocalizeStringEvent`** component that looks the value up by **key**. So a correct
Arabic build means the **Arabic table** must contain pre-shaped + pre-reversed + pre-wrapped
text, while the game keeps pulling by key exactly as before.

The obstacle: a table is only **key → value**. Wrapping needs a **width**, and the table has
none. The width lives on the scene box, not in the table.

## 2. The missing link (key ↔ width)

Each `LocalizeStringEvent` carries **both** facts we need:

- **The key** — `LocalizeStringEvent.StringReference` is a `LocalizedString`, whose
  `TableReference` + `TableEntryReference` identify *(which collection, which entry)*.
- **The width** — `LocalizeStringEvent.OnUpdateString` is a `UnityEvent<string>`; its
  persistent listener targets the **`TMP_Text`** box (method `set_text`), whose
  `RectTransform` gives width and whose component gives font + size.

So the editor command can reconstruct `(key ↔ box)` for every localized box in the scene.

## 3. Algorithm

1. Walk every `LocalizeStringEvent` in the loaded scene(s) (inactive included). For each,
   read the entry **key** + the **bound box** + the box **width** + font/size.
2. Build a map `(collection, key) → list of (box, width)`.
3. For each key, take the **smallest** width — the safety margin: text baked on the tightest
   box never overflows a wider one. Warn when a key appears at differing widths.
4. Open the collection's **Arabic** table(s) (`ar`, or an exact code you type). For each key,
   bake its Arabic value against its chosen narrowest box and write it **back into the table**.
5. Result: the Arabic table is baked; the game pulls by key as always — **zero manual paste,
   zero code, the game's loading path unchanged.**

We measure on the **real** narrowest box (not a synthesized width), so the box's true
width/font/size/margins drive the wrap — strictly better than a width number. The width
figure only **selects** the narrowest box among duplicates.

### Why not a runtime `ITablePostprocessor`

Unity offers `ITablePostprocessor.PostprocessTable(LocalizationTable)`, assigned via
`LocalizationSettings…GetStringDatabase().TablePostprocessor`, which runs **after a table
loads but before it is used**. Shaping there would mean **shipping shaping code that executes
on every table load** — the exact opposite of this tool's "zero runtime additions" contract,
and it would re-introduce the runtime cost the whole project exists to avoid. We therefore
bake into the **table asset at edit time** and never register a postprocessor. (Its sample
also confirms the write path we use: `entry.Value = "…";`.)

## 4. Corruption is impossible (governing rule)

The tool is generic: many translators work **directly** in the Arabic table with no separate
source table. So it adopts **strategy (c)**:

- Bake only **raw** text. Detect already-baked values with `IsBaked` and **skip** them.
- **Snapshot** each entry's original **before** its first bake, so re-baking is always possible
  and the exact original is always recoverable.

The snapshot lives in an **editor-only sidecar** — `Editor/Backups/HeshamRTL_originals_<collGuid>.json`,
keyed by `localeCode | keyId`. It sits under an `Editor/` folder so it **never ships** in a
player build and adds **zero runtime footprint** (no `SerializeReference` metadata type that
could go missing at runtime and log warnings). It is plain, human-readable JSON and is
naturally version-controlled with the project.

Per-entry decision when baking:

| Current table value | Snapshot on record | Action |
|---|---|---|
| raw (not baked)     | none               | snapshot the raw, then bake |
| raw, **differs** from snapshot | yes     | snapshot becomes the new raw, then bake (re-translated) |
| raw, equals snapshot | yes               | bake (snapshot already correct; e.g. after Unbake) |
| **baked**            | yes               | restore snapshot → re-bake (e.g. a box width changed) |
| **baked**            | **none**          | **leave untouched** (hand-pasted, or sidecar lost) |

**Unbake** restores every baked entry to its exact snapshotted original. The net guarantee:
**no matter how the translator works, their translation cannot be lost.** (Aggressive cases
like Smart Strings are safe too — because every bake is reversible, an imperfect bake is
always undoable back to the exact source.)

## 5. Reuse of the v1.2 surface

Nothing about shaping/reversal/wrapping is re-implemented. The integration is a
`partial class` over `HeshamRTLWindow` and calls the shipped core directly:

- `BakeValue(box, value, emitted, keyForLog, usedPua)` — page/`<br>` split → Protect → Shape →
  **MeasureWrap (TMP)** → BalanceSpans → VisualOrder → SwapPairs → Restore.
- the `{N}` "Fat-8" width buffer (inside MeasureWrap), R5 (PUA), R6 (tofu + fallback wiring),
  the loss/duplication/contiguity self-check, the NoWrap / auto-size warnings, and the
  **shared bundled fallback font** field.

### 5.1 Right-alignment is PER-LOCALE (not per-box)

The file baker and the per-box scene baker set the **box's** horizontal alignment to Right,
which is correct there because each of those boxes only ever displays Arabic. The Localization
path is different: a `LocalizeStringEvent` shares **one** box across **every** locale (it only
swaps the box's *text value* when the locale changes), so forcing that box to Right would
right-align English, French and every other language in the same box.

So in the Localization path the tool does **not** touch the box. When the same
"Set alignment to Right" toggle is on, it instead embeds a block `<align=right>` tag at the
**front** of the **Arabic value only** (outside the per-line reversal — it is a control tag,
not visual text). Only the Arabic entry is baked, so only it carries the tag; other locales
keep the box's default alignment. This makes right-alignment **per-locale**, which is exactly
what a shared box needs. Consequences:

- It is **idempotent**: a re-bake always starts from the clean snapshot, so the tag is derived
  fresh and never stacks.
- It is **reversible**: the snapshot is the raw pre-bake value (no tag), so **Unbake** restores
  a clean original. `IsBaked` still detects a baked value through a leading tag (it scans the
  whole string for presentation forms, which sit after the tag).
- It needs **Rich Text** enabled on the display box (TMP's default). If a measured box has Rich
  Text off, the tool logs a warning, because the tag (and any rich-text styling) would otherwise
  render as literal text.
- The Localization bake therefore **never modifies a scene object** — only the table asset and
  the editor-only snapshot sidecar change.

## 6. The compile switch (automatic, robust)

`HeshamRTLLocalization.cs` is wrapped in `#if HESHAMRTL_LOCALIZATION` and **only compiles when
the package is present**. `HeshamRTLLocalizationBootstrap.cs` — which has **no** dependency on
the package and therefore always compiles — keeps that define in sync:

- **Reconcile** on every domain reload: probe for the Localization types and add/remove the
  define to match (writes only on a real change, so it self-stabilises and never loops).
- **Pre-clear** on package removal via `PackageManager.Events.registeringPackages`: drop the
  define the instant the package starts to be removed, *before* the recompile that would
  otherwise try to build the `#if`'d code against the now-missing assemblies.

The integration compiles into the predefined **Editor** assembly (same place TMP is used from),
so it reuses `BakeValue` with no assembly-definition restructuring. *Manual recovery* — only if
the package was deleted straight off disk so neither safeguard ran — remove
`HESHAMRTL_LOCALIZATION` from **Project Settings → Player → Scripting Define Symbols**.

---

## 7. Confirmed Unity Localization API (version-checked)

Verified against the official docs across Localization **1.0 → 1.5** (the package is
version-sensitive). The integration uses only members present throughout this range, and
reads ids via `KeyId` / key names via `ResolveKeyName` to avoid type churn (`KeyId` was
`uint` in 0.x previews, `long` in 1.x — both widen to `long`).

**Editor** (`UnityEditor.Localization`)

| Member | Signature |
|---|---|
| Resolve a collection | `LocalizationEditorSettings.GetStringTableCollection(TableReference)` → `StringTableCollection` (null if not found) |
| All collections | `LocalizationEditorSettings.GetStringTableCollections()` → `ReadOnlyCollection<StringTableCollection>` |
| Collection name / shared data | `StringTableCollection.TableCollectionName`, `.SharedData` (`SharedTableData`) |
| Tables in a collection | `StringTableCollection.StringTables` (resolved `StringTable`s) |
| Collection GUID | `collection.SharedData.TableCollectionNameGuid` (`System.Guid`) |

**Runtime** (`UnityEngine.Localization` and sub-namespaces)

| Member | Signature |
|---|---|
| Component | `UnityEngine.Localization.Components.LocalizeStringEvent` : `.StringReference` (`LocalizedString`), `.OnUpdateString` (a `UnityEvent<string>`) |
| Reference | `LocalizedString.TableReference` (`TableReference`), `.TableEntryReference` (`TableEntryReference`) |
| Resolve key name | `TableEntryReference.ResolveKeyName(SharedTableData)` → `string` (Name→Key; Id→looked up) |
| Read an entry | `StringTable.GetEntry(string)` / `GetEntryFromReference(TableEntryReference)` → `StringTableEntry` (null if missing) |
| Entry value | `StringTableEntry.Value` **get/set** (the write path); `.Key`, `.KeyId`, `.LocalizedValue`, `.MetadataEntries` |
| Locale code | `StringTable.LocaleIdentifier.Code` (`string`, e.g. `"ar"`, `"ar-SA"`) |
| Event listeners | `UnityEventBase.GetPersistentEventCount()`, `GetPersistentTarget(int)` (`UnityEngine.Object`), `GetPersistentMethodName(int)` (`string`) |
| Save | `EditorUtility.SetDirty(table)` then `AssetDatabase.SaveAssets()` (the Localization changelog explicitly notes tables lose edits if not marked dirty) |

**Deliberately not used:** `ITablePostprocessor` (`UnityEngine.Localization.Settings`) — a
**runtime** load-time hook (see §3).

> Note on `StringTableEntry.SharedEntry`: it was added in a later 1.x release, so the tool uses
> `KeyId` (present on every version) for the snapshot id instead. Custom `IMetadata` types are
> also avoided on purpose — a `SerializeReference` metadata type defined in an editor assembly
> would log a missing-managed-reference warning at runtime; the editor-only JSON sidecar carries
> the same data with no runtime trace.
