// HeshamRTLLocalization.cs — OPTIONAL integration with the Unity Localization package.
// Compiles ONLY when com.unity.localization is installed (HESHAMRTL_LOCALIZATION is auto-managed
// by HeshamRTLLocalizationBootstrap, and gated by this assembly's defineConstraints). It lives in a
// SEPARATE assembly (HeshamRTL.Localization.Editor) and registers its UI drawer into HeshamRTLWindow
// (see LocalizationSection); the bake driver calls the window's internal v1.2 engine (BakeValue,
// HandleFallback) on the live window instance, so nothing about shaping/wrapping is re-implemented.
//
// WHY A TABLE BAKER (the problem). Real games do not type Arabic into each box; strings live in
// String Tables (one per locale) and load at runtime via a LocalizeStringEvent on each box. So
// the bake must target the Arabic TABLE, not the scene boxes. But a table is only key -> value:
// it has no width, and width is what wrapping needs.
//
// THE MISSING LINK (the solution). Each LocalizeStringEvent gives us BOTH halves: the entry key
// (StringReference.TableReference + TableEntryReference) AND, through its OnUpdateString target,
// the TMP box whose RectTransform carries the width + font + size. We harvest (key <-> box) pairs
// from the scene, group boxes per key, bake each key's Arabic value against its NARROWEST box (so
// text baked on the tightest box never overflows a wider one), and write the result back into the
// table. The game keeps pulling by key — zero manual paste, zero runtime code, the game's own
// loading path untouched. (We deliberately do NOT use a runtime ITablePostprocessor: that would
// ship shaping code that runs on every table load — the exact opposite of this tool's contract.)
//
// REUSE. The baking itself is the unchanged v1.2 engine: BakeValue -> Protect / Shape /
// MeasureWrap(TMP) / BalanceSpans / VisualOrder / SwapPairs / Restore, the {N} "Fat-8" width
// buffer, R5/R6, the bundled fallback-font wiring, and the NoWrap / auto-size warnings. Nothing
// about shaping/reversal/wrapping is re-implemented here. We measure on the REAL narrowest box,
// so the box's true width/font/size/margins drive the wrap — strictly better than a width number.
//
// CORRUPTION IS IMPOSSIBLE (the governing rule). The tool is generic: many translators work
// straight in the Arabic table with no separate source. So strategy (c): bake only RAW text,
// detect already-baked values with IsBaked and skip them, and SNAPSHOT the original of every
// entry BEFORE its first bake into an editor-only sidecar. Every bake is therefore fully
// reversible (the Unbake button restores exact originals), and an entry we did not snapshot is
// never touched. No matter how the translator works, their translation cannot be lost.
#if UNITY_EDITOR && HESHAMRTL_LOCALIZATION
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.Tables;
using UnityEngine.SceneManagement;

namespace HeshamRTL
{
    [InitializeOnLoad]
    static class HeshamRTLLocalizationPanel
    {
        // Register the drawer the instant this (optional) assembly loads. When the package is
        // absent this assembly is not compiled, so HeshamRTLWindow.LocalizationSection stays null.
        static HeshamRTLLocalizationPanel() { HeshamRTLWindow.LocalizationSection = Draw; }

        // ---- Localization UI state (the fallback font / alignment toggles in the top section
        // of the window are shared — this is the same window instance). --------------------------
        static bool   _locFoldout = true;
        static string _locArabicCode = "";              // blank = auto-detect every "ar" / "ar-*" / "ar_*" locale
        static bool   _locScanAllLoadedScenes = true;

        // ===== UI (called from OnGUI; no-op when the package is absent) ==========================
        internal static void Draw(HeshamRTLWindow w)
        {
            EditorGUILayout.Space();
            _locFoldout = EditorGUILayout.Foldout(_locFoldout, "Unity Localization — bake Arabic String Table", true);
            if (!_locFoldout) return;

            EditorGUILayout.HelpBox(
                "Bakes the Arabic String Table(s) IN PLACE. For every LocalizeStringEvent in the loaded scene(s) it " +
                "reads the entry key + the bound TMP box's width, groups boxes per key, bakes each key's Arabic value " +
                "against its NARROWEST box (so it never overflows a wider one), and writes the result back into the " +
                "table. The game still pulls by key — zero manual paste, zero runtime code. Same engine, {N} buffer, " +
                "fallback font and warnings as the bakers above.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "CANNOT CORRUPT YOUR TRANSLATION: only raw text is baked, already-baked entries are skipped, and each " +
                "entry's original is snapshotted before its first bake. 'Unbake' restores the exact originals anytime.",
                MessageType.None);

            _locArabicCode = EditorGUILayout.TextField(
                new GUIContent("Arabic locale code",
                    "Blank = auto-detect every locale whose code is 'ar' or starts with 'ar-' / 'ar_'. Set an exact " +
                    "code (e.g. ar-SA) to target just one."),
                _locArabicCode);
            _locScanAllLoadedScenes = EditorGUILayout.ToggleLeft(
                new GUIContent("Scan all loaded scenes (not just the active one)",
                    "Gather LocalizeStringEvents from every loaded scene. Boxes that live only in unopened prefabs are " +
                    "not measured — open a scene that instantiates them so a width is available."),
                _locScanAllLoadedScenes);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake Arabic String Table(s)", GUILayout.Height(28))) BakeLocalizationTables(w);
                if (GUILayout.Button(new GUIContent("Unbake (restore originals)",
                        "Restore every baked Arabic entry to its snapshotted original raw text."),
                        GUILayout.Height(28), GUILayout.Width(190))) UnbakeLocalizationTables(w);
            }
        }

        // ===== BAKE =============================================================================
        static void BakeLocalizationTables(HeshamRTLWindow w)
        {
            w._log = "";
            w._anomalies = 0;
            w._sessionHarakat.Clear();
            w._nbspSafeCache.Clear();
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) { w.Log("ERROR: exit Play mode before baking tables."); return; }

                // 1) Harvest LocalizeStringEvents -> group candidate (box,width) per (collection,key).
                var events = CollectStringEvents();
                if (events.Count == 0) { w.Log("No LocalizeStringEvent components found in the loaded scene(s)."); return; }
                w.Log($"Localization bake: scanning {events.Count} LocalizeStringEvent(s).");

                var groups            = new Dictionary<LocKey, LocGroup>();
                var collectionsByGuid = new Dictionary<string, StringTableCollection>();
                int noColl = 0, noKey = 0, noBox = 0, noWidth = 0;

                foreach (var lse in events)
                {
                    var sr = lse.StringReference;
                    if (sr == null) continue;

                    StringTableCollection coll;
                    try { coll = LocalizationEditorSettings.GetStringTableCollection(sr.TableReference); }
                    catch { coll = null; }
                    if (coll == null || coll.SharedData == null) { noColl++; continue; }   // empty/unresolved table ref

                    string keyName;
                    try { keyName = sr.TableEntryReference.ResolveKeyName(coll.SharedData); }
                    catch { keyName = null; }
                    if (string.IsNullOrEmpty(keyName)) { noKey++; continue; }

                    TMP_Text box = ResolveBoundBox(lse);
                    if (box == null) { noBox++; continue; }

                    float width = BoxWidth(box);
                    if (width <= 1f) { noWidth++; continue; }   // unresolved layout (e.g. Layout-Group driven) -> can't measure

                    string guid = coll.SharedData.TableCollectionNameGuid.ToString();
                    collectionsByGuid[guid] = coll;
                    var k = new LocKey(guid, keyName);
                    if (!groups.TryGetValue(k, out var g)) { g = new LocGroup(); groups[k] = g; }
                    g.Add(box, width);
                }

                if (noColl  > 0) w.Log($"  note: {noColl} event(s) reference a table collection that could not be resolved — skipped.");
                if (noKey   > 0) w.Log($"  note: {noKey} event(s) had an unresolved entry key — skipped.");
                if (noBox   > 0) w.Log($"  WARNING: {noBox} event(s) had no bound TMP_Text (no 'set_text' listener and no TMP_Text on the same object) — skipped.");
                if (noWidth > 0) w.Log($"  WARNING: {noWidth} box(es) had no resolved width at edit time (likely Layout-Group driven) — skipped; open them so a width exists, or set a fixed width.");
                if (groups.Count == 0) { w.Log("Nothing to bake (no key had a measurable box)."); return; }

                // 2) Bake each collection's Arabic table(s).
                var emitted        = new HashSet<char>();
                var touchedFonts   = new HashSet<TMP_FontAsset>();
                var richTextOffBoxes = new HashSet<TMP_Text>();
                int bakedCount = 0, rebakedCount = 0, failedGuard = 0;
                int skippedNoEntry = 0, skippedNoBackup = 0, ltrCount = 0, autoSizeOn = 0;
                var work = new List<LocWork>();                                   // PASS 1 output
                var preForms = new Dictionary<TMP_FontAsset, HashSet<char>>();    // font -> needed forms (F7)

                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("HeshamRTL: bake Arabic String Table(s)");

                foreach (var kvCol in collectionsByGuid)
                {
                    string guid = kvCol.Key;
                    var coll = kvCol.Value;

                    var arabicTables = ArabicTables(coll, _locArabicCode);
                    if (arabicTables.Count == 0)
                    {
                        w.Log($"  WARNING: collection '{coll.TableCollectionName}' has no Arabic String Table" +
                            (string.IsNullOrWhiteSpace(_locArabicCode) ? " (no locale matching 'ar*')" : $" matching '{_locArabicCode}'") +
                            " — skipped.");
                        continue;
                    }

                    foreach (var kv in groups)
                    {
                        if (kv.Key.Guid != guid) continue;

                        var narrow = kv.Value.Narrowest();
                        if (narrow.Differing)
                            w.Log($"  note: key '{Trunc(kv.Key.Name)}' shows in boxes of differing widths — baked to the narrowest ({narrow.Width:0}px).");

                        foreach (var arTable in arabicTables)
                        {
                            string code = SafeCode(arTable);

                            StringTableEntry entry;
                            try { entry = arTable.GetEntry(kv.Key.Name); } catch { entry = null; }
                            if (entry == null) { skippedNoEntry++; continue; }   // this key has no Arabic translation yet

                            string cur = entry.Value ?? "";
                            long   id  = SafeId(entry);

                            // ---- anti-corruption decision (strategy c) -----------------------------
                            string raw;
                            bool isRebake = false;
                            if (HeshamRTLBaker.IsBaked(cur))
                            {
                                string snapshot = HeshamRTLLocBackup.Get(guid, code, id);
                                if (snapshot == null)
                                {
                                    skippedNoBackup++;   // already baked, no snapshot on record -> NEVER touch it
                                    continue;
                                }
                                raw = snapshot; isRebake = true;            // re-bake from snapshot (e.g. widths changed)
                            }
                            else
                            {
                                string snapshot = HeshamRTLLocBackup.Get(guid, code, id);
                                if (snapshot == null || snapshot != cur)
                                    HeshamRTLLocBackup.Set(guid, coll.TableCollectionName, code, id, cur); // first bake / re-translated
                                raw = cur;
                            }

                            if (!HeshamRTLBaker.HasArabic(raw)) { ltrCount++; continue; }   // pure-LTR translation: nothing to shape

                            var usedPua = new HashSet<char>();
                            foreach (char c in raw) if (c >= '\uE000' && c <= '\uF8FF') usedPua.Add(c);

                            // PASS 1 (F7): defer the bake — record the work item and the forms this
                            // value will emit, so every fallback is wired BEFORE any measurement.
                            work.Add(new LocWork { Table = arTable, Entry = entry, Raw = raw, Cur = cur,
                                                   IsRebake = isRebake, Box = narrow.Box, Key = kv.Key.Name,
                                                   Code = code, UsedPua = usedPua });
                            if (narrow.Box.font != null)
                            {
                                HashSet<char> f;
                                if (!preForms.TryGetValue(narrow.Box.font, out f)) { f = new HashSet<char>(); preForms[narrow.Box.font] = f; }
                                HeshamRTLWindow.CollectForms(raw, usedPua, f, w._sessionHarakat);
                            }

                        }
                    }
                }

                // PASS 1 done — wire every needed fallback BEFORE any measurement (F7).
                foreach (var kvF in preForms) w.PreflightFont(kvF.Key, kvF.Value);

                // PASS 2 — measure + bake + write, fail-closed per entry (F8).
                foreach (var it in work)
                {
                    string bakedText;
                    try { bakedText = w.BakeValue(it.Box, it.Raw, emitted, it.Key, it.UsedPua); }   // <-- the v1.2 engine
                    catch (HeshamRTLGuardException gex)
                    {
                        failedGuard++;
                        w.Log($"  FAILED (guard) key '{Trunc(it.Key)}' [{it.Code}]: {gex.Message} — skipped (original is safe).");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        w.Log($"  ERROR baking key '{Trunc(it.Key)}' [{it.Code}]: {ex.Message} — skipped (original is safe).");
                        continue;
                    }

                    // Right-alignment stays PER-LOCALE: the tag is embedded in the Arabic VALUE
                    // only (front-of-string, idempotent, snapshot-reversible); the shared box is
                    // never touched. Full rationale in LOCALIZATION_INTEGRATION.md §5.1.
                    if (w._setRightAlignment)
                    {
                        bakedText = ApplyAlignRight(bakedText);
                        if (!it.Box.richText) richTextOffBoxes.Add(it.Box);
                    }

                    if (bakedText != it.Cur)
                    {
                        Undo.RecordObject(it.Table, "HeshamRTL: bake " + it.Key);
                        it.Entry.Value = bakedText;
                        EditorUtility.SetDirty(it.Table);
                    }
                    if (it.IsRebake) rebakedCount++; else bakedCount++;
                    if (it.Box.font != null) touchedFonts.Add(it.Box.font);
                    if (it.Box.enableAutoSizing) autoSizeOn++;
                }

                // 3) Persist (only entry VALUES changed -> mark the tables; SharedData is untouched).
                HeshamRTLLocBackup.SaveAll();
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);

                w.Log($"Localization DONE. baked={bakedCount}, re-baked={rebakedCount}, passed-through LTR={ltrCount}, " +
                    $"no-Arabic-entry skipped={skippedNoEntry}, already-baked-without-snapshot skipped={skippedNoBackup}, failed={failedGuard}.");
                if (failedGuard > 0)
                    w.Log($"!!! {failedGuard} entry(ies) FAILED the loss/duplication gate and were skipped — originals are safe; inspect the keys above.");
                if (w._anomalies > 0)
                    w.Log($"!!! {w._anomalies} line(s) tripped the loss/duplication/contiguity guard — INSPECT (see warnings above).");
                if (skippedNoBackup > 0)
                    w.Log($"  note: {skippedNoBackup} entry(ies) were already baked with NO snapshot on record (baked by hand, or the " +
                        "sidecar was lost) — left untouched so nothing is corrupted. Unbake only restores entries this tool baked.");
                if (autoSizeOn > 0)
                    w.Log($"WARNING (auto-size): {autoSizeOn} display box(es) have auto-sizing (Best Fit) ENABLED. Pre-baked wrap needs " +
                        "a FIXED font size — disable auto-sizing on those boxes or the baked breaks overflow at runtime.");
                if (richTextOffBoxes.Count > 0)
                    w.Log($"WARNING (rich text): {richTextOffBoxes.Count} display box(es) have Rich Text DISABLED, so the embedded " +
                        "<align=right> tag (and any rich-text styling) would render as literal text. Enable Rich Text on those " +
                        "boxes for the per-locale Arabic alignment to take effect.");

                // NoWrap is a property of the DISPLAY boxes (your scene/runtime objects), so this is advisory.
                w.Log("REMINDER: every TMP box that DISPLAYS a baked entry must be set to NoWrap, or TMP re-wraps the " +
                    "pre-wrapped text and the line order inverts.");

                // R6 tofu check + bundled fallback wiring, against each font that received baked text.
                foreach (var fa in touchedFonts)
                {
                    if (fa == null) continue;
                    var missing = new List<char>();
                    foreach (char c in emitted) if (!fa.HasCharacter(c)) missing.Add(c);
                    if (missing.Count > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (char c in missing) sb.Append($"U+{(int)c:X4} ");
                        w.Log($"WARNING (R6): {missing.Count} form(s) still MISSING from font '{fa.name}' AFTER pre-flight -> tofu: {sb}");
                        w.HandleFallback(fa, missing);   // shared bundled-font fallback wiring from v1.2
                        w.Log("  NOTE: these forms were measured without real glyph metrics — wrap may be APPROXIMATE. Fix the font/fallback and RE-BAKE.");
                    }
                    else w.Log($"R6 OK: every emitted form exists in font '{fa.name}'.");
                    w.CheckHarakaAdvances(fa);           // F13 honesty valve
                }

                AssetDatabase.Refresh();
            }
            catch (Exception e) { w.Log("EXCEPTION: " + e); }
        }

        // ===== UNBAKE (restore exact originals) =================================================
        static void UnbakeLocalizationTables(HeshamRTLWindow w)
        {
            w._log = "";
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) { w.Log("ERROR: exit Play mode before unbaking."); return; }

                int restored = 0, scanned = 0;
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("HeshamRTL: unbake Arabic String Table(s)");

                foreach (var coll in LocalizationEditorSettings.GetStringTableCollections())
                {
                    if (coll == null || coll.SharedData == null) continue;
                    string guid = coll.SharedData.TableCollectionNameGuid.ToString();

                    foreach (var arTable in ArabicTables(coll, _locArabicCode))
                    {
                        string code = SafeCode(arTable);
                        foreach (var entry in arTable.Values)
                        {
                            scanned++;
                            string cur = entry.Value ?? "";
                            if (!HeshamRTLBaker.IsBaked(cur)) continue;     // only undo what is actually baked
                            long id = SafeId(entry);
                            string snapshot = HeshamRTLLocBackup.Get(guid, code, id);
                            if (snapshot == null || snapshot == cur) continue;

                            Undo.RecordObject(arTable, "HeshamRTL: unbake " + entry.Key);
                            entry.Value = snapshot;
                            EditorUtility.SetDirty(arTable);
                            restored++;
                        }
                    }
                }

                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
                w.Log($"Unbake DONE. restored {restored} entry(ies) to original raw text (scanned {scanned}).");
                if (restored == 0) w.Log("  (Nothing to restore — either no baked entries, or no snapshots on record.)");
            }
            catch (Exception e) { w.Log("EXCEPTION: " + e); }
        }

        // ===== helpers ==========================================================================

        // Every LocalizeStringEvent in the loaded scene(s) (inactive included, like the scene baker).
        static List<LocalizeStringEvent> CollectStringEvents()
        {
            var list = new List<LocalizeStringEvent>();
            var active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                if (!_locScanAllLoadedScenes && scene != active) continue;
                foreach (var go in scene.GetRootGameObjects())
                    list.AddRange(go.GetComponentsInChildren<LocalizeStringEvent>(true));
            }
            return list;
        }

        // The TMP box driven by this event: prefer the persistent listener whose method is the text
        // setter ("set_text"); else any TMP_Text target; else a TMP_Text on the same GameObject
        // (the layout the "Localize" context-menu / Add Component produces).
        static TMP_Text ResolveBoundBox(LocalizeStringEvent lse)
        {
            UnityEventBase ev = lse.OnUpdateString;            // a UnityEvent<string> IS-A UnityEventBase
            TMP_Text fallback = null;
            if (ev != null)
            {
                int n = ev.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    var tgt = ev.GetPersistentTarget(i) as TMP_Text;
                    if (tgt == null) continue;
                    if (ev.GetPersistentMethodName(i) == "set_text") return tgt;
                    if (fallback == null) fallback = tgt;
                }
            }
            return fallback != null ? fallback : lse.GetComponent<TMP_Text>();
        }

        // The width wrapping is measured against. (MeasureWrap actually re-measures on this very box,
        // so this value only PICKS the narrowest box among duplicates; an imperfect number never
        // changes the wrap, only which box wins.)
        static float BoxWidth(TMP_Text box)
        {
            var rt = box.rectTransform;
            if (rt == null) return 0f;
            float w = rt.rect.width;
            return (float.IsNaN(w) || float.IsInfinity(w)) ? 0f : w;
        }

        // All Arabic String Tables in a collection: exact code if one was typed, else any "ar*".
        static List<StringTable> ArabicTables(StringTableCollection coll, string exactCode)
        {
            var result = new List<StringTable>();
            foreach (var t in coll.StringTables)
            {
                if (t == null) continue;
                string code = SafeCode(t);
                bool match = string.IsNullOrWhiteSpace(exactCode)
                    ? IsArabicCode(code)
                    : string.Equals(code, exactCode.Trim(), StringComparison.OrdinalIgnoreCase);
                if (match) result.Add(t);
            }
            return result;
        }

        static bool IsArabicCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            if (code.Equals("ar", StringComparison.OrdinalIgnoreCase)) return true;
            return code.StartsWith("ar-", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("ar_", StringComparison.OrdinalIgnoreCase);
        }

        // Per-locale right-alignment: prepend a block <align=right> control tag to the (already
        // fully baked) Arabic value. Front-of-string and OUTSIDE the per-line reversal, so it acts
        // as a block directive, not visual text. Idempotent: never stacks (the clean pre-bake
        // snapshot is what a re-bake starts from, so this is applied to fresh output each time).
        const string AlignRightTag = "<align=right>";
        static string ApplyAlignRight(string baked)
        {
            if (string.IsNullOrEmpty(baked)) return baked;
            if (baked.StartsWith(AlignRightTag, StringComparison.Ordinal)) return baked;   // never stack the tag
            return AlignRightTag + baked;
        }

        // Local copy of the window's trivial display-truncation helper (pure formatter; kept here
        // so this assembly needs no extra internal access for it).
        static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 24 ? s.Substring(0, 24) : s);

        static string SafeCode(StringTable t)
        {
            try { return t.LocaleIdentifier.Code ?? ""; } catch { return ""; }
        }

        // Stable per-entry id for the snapshot key (rename-proof). KeyId is the entry's id and is
        // present on every Localization version; it widens to long.
        static long SafeId(StringTableEntry e)
        {
            try { return e.KeyId; } catch { return 0; }
        }

        // deferred bake unit for the two-pass (F7) flow: everything PASS 2 needs.
        sealed class LocWork
        {
            public StringTable Table;
            public StringTableEntry Entry;
            public string Raw, Cur, Key, Code;
            public bool IsRebake;
            public TMP_Text Box;
            public HashSet<char> UsedPua;
        }

        // (collection-guid, key-name) identity for grouping boxes per key.
        readonly struct LocKey : IEquatable<LocKey>
        {
            public readonly string Guid;
            public readonly string Name;
            public LocKey(string guid, string name) { Guid = guid; Name = name; }
            public bool Equals(LocKey o) => Guid == o.Guid && Name == o.Name;
            public override bool Equals(object o) => o is LocKey k && Equals(k);
            public override int GetHashCode() => ((Guid?.GetHashCode() ?? 0) * 397) ^ (Name?.GetHashCode() ?? 0);
        }

        // The boxes that display one key, and the pick of the narrowest (the safety margin).
        sealed class LocGroup
        {
            readonly List<TMP_Text> _boxes  = new List<TMP_Text>();
            readonly List<float>    _widths = new List<float>();
            public void Add(TMP_Text b, float w) { _boxes.Add(b); _widths.Add(w); }

            public struct Pick { public TMP_Text Box; public float Width; public bool Differing; }
            public Pick Narrowest()
            {
                int best = 0;
                float min = _widths[0], max = _widths[0];
                for (int i = 1; i < _widths.Count; i++)
                {
                    if (_widths[i] < _widths[best]) best = i;
                    if (_widths[i] < min) min = _widths[i];
                    if (_widths[i] > max) max = _widths[i];
                }
                return new Pick { Box = _boxes[best], Width = _widths[best], Differing = (max - min) > 0.5f };
            }
        }
    }

    // ===== Reversible snapshot store (editor-only sidecar) ======================================
    // Per-collection JSON under the tool's Editor/Backups folder, so it NEVER ships in a build and
    // adds ZERO runtime footprint (no SerializeReference metadata type to go missing at runtime).
    // Keyed by (collection-guid, locale-code, entry-id), so it survives key renames and table moves.
    // This is what makes "corruption impossible": the exact pre-bake raw is always recoverable, and
    // an entry with no snapshot is never overwritten.
    static class HeshamRTLLocBackup
    {
        [Serializable] sealed class Pair  { public string k; public string v; }
        [Serializable] sealed class Store { public string collection; public string guid; public List<Pair> entries = new List<Pair>(); }

        static readonly Dictionary<string, Store> _cache = new Dictionary<string, Store>();   // guid -> store
        static readonly HashSet<string>           _dirty = new HashSet<string>();

        // The MonoScript path of the tool's own window (its class name matches its filename, so the
        // lookup is reliable). Used to locate the tool's Editor/ folder AND to detect an immutable
        // (read-only) package install.
        static string WindowScriptPath()
        {
            foreach (var g in AssetDatabase.FindAssets("HeshamRTLWindow t:MonoScript"))
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(p) && p.EndsWith("HeshamRTLWindow.cs", StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        static string EditorDir()
        {
            string p = WindowScriptPath();
            if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(p).Replace('\\', '/');
            return "Assets/HeshamRTL/Editor";   // fall back to a fixed path
        }

        // True only when the tool lives in an immutable, READ-ONLY package location (added via
        // "Add package from git URL" or a registry). Embedded and local "file:" packages live under
        // Packages/ too but ARE writable, so they are treated like a normal Assets/ install.
        static bool IsImmutableInstall()
        {
            string anyToolAssetPath = WindowScriptPath();
            if (string.IsNullOrEmpty(anyToolAssetPath)) return false;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(anyToolAssetPath);
            if (info == null) return false;
            return info.source != UnityEditor.PackageManager.PackageSource.Embedded
                && info.source != UnityEditor.PackageManager.PackageSource.Local;
        }

        static string Dir()
        {
            // Manual / embedded / local install: the tool's own Editor/ folder is writable, so backups
            // live there (UNCHANGED). Immutable package install: that folder is read-only, so redirect
            // to a fixed writable Editor/ folder under Assets/. Either way it stays under an Editor/
            // folder (never ships in a build) and version-controlled with the project. This is what
            // keeps the Localization bake non-destructive (snapshot / Unbake) in BOTH install modes.
            string dir = IsImmutableInstall() ? "Assets/HeshamRTL/Editor/Backups"
                                              : EditorDir() + "/Backups";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        static string PathFor(string guid)      => Dir() + "/HeshamRTL_originals_" + guid + ".json";
        static string Composite(string code, long id) => code + "|" + id.ToString(CultureInfo.InvariantCulture);

        static Store Load(string guid)
        {
            if (_cache.TryGetValue(guid, out var s)) return s;
            string path = PathFor(guid);
            if (File.Exists(path))
            {
                try { s = JsonUtility.FromJson<Store>(File.ReadAllText(path)); } catch { s = null; }
            }
            if (s == null)         s = new Store { guid = guid };
            if (s.entries == null) s.entries = new List<Pair>();
            _cache[guid] = s;
            return s;
        }

        public static string Get(string guid, string code, long id)
        {
            var s = Load(guid);
            string key = Composite(code, id);
            foreach (var p in s.entries) if (p.k == key) return p.v;
            return null;
        }

        public static void Set(string guid, string collectionName, string code, long id, string original)
        {
            var s = Load(guid);
            s.collection = collectionName;
            string key = Composite(code, id);
            foreach (var p in s.entries) if (p.k == key) { p.v = original; _dirty.Add(guid); return; }
            s.entries.Add(new Pair { k = key, v = original });
            _dirty.Add(guid);
        }

        public static void SaveAll()
        {
            foreach (var guid in _dirty)
            {
                if (!_cache.TryGetValue(guid, out var s)) continue;
                try { File.WriteAllText(PathFor(guid), JsonUtility.ToJson(s, true)); }
                catch (Exception e) { Debug.LogWarning("[HeshamRTL] Could not write localization backup sidecar: " + e.Message); }
            }
            if (_dirty.Count > 0) { _dirty.Clear(); AssetDatabase.Refresh(); }
        }
    }
}
#endif
