// HeshamRTLWindow.cs  — Phase 2-B: the Unity Editor "Apply" tool.  (rev 2)
// PUT THIS (and HeshamRTLShaper.cs + HeshamRTLBaker.cs) IN AN  Editor/  FOLDER so
// nothing ships in the game build. Edit-time only; zero runtime additions.
//
// Flow per YAML value:  page-split (<page>/<hpage>/<page=X> kept verbatim)
//   -> per page: <br>-split (forced lines)
//      -> per segment: Shape ONCE -> set on the target TMP box (naive-LTR,
//         wrap ON) -> ForceMeshUpdate -> read REAL break positions from
//         textInfo -> rebuild each line from source indices (loss/dup guarded)
//         -> trim boundary spaces -> reverse each line
//   -> rejoin lines with <br>.   *** THE RUNTIME BOX MUST BE SET TO NoWrap. ***
//
// Fixes in rev 2:
//   Bug 1 — wrap lines are rebuilt from the actual per-character source indices
//           with a contiguity + loss/duplication self-check that runs on every
//           bake; boundary spaces are trimmed before reversal.
//   Bug 2 — wrapping is toggled via reflection (textWrappingMode on modern TMP,
//           enableWordWrapping on old) so the tool COMPILES on any TMP version.
//   R5 (PUA abort) and R6 (font tofu warning) retained.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// The OPTIONAL Unity Localization integration lives in a separate, defineConstraint-gated assembly
// (HeshamRTL.Localization.Editor) so the main tool never hard-references com.unity.localization.
// That assembly calls a few internal members of this window (the v1.2 bake engine + log), so it is
// granted friend access here. It does NOT create a cyclic asmdef reference: it references this
// assembly; this assembly only exposes a hook field it fills (see LocalizationSection below).
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HeshamRTL.Localization.Editor")]

namespace HeshamRTL
{
    public partial class HeshamRTLWindow : EditorWindow
    {
        TMP_Text _target;
        TMP_FontAsset _fallbackFont;          // Arabic font (e.g. bundled Noto Kufi) to fill tofu
        bool _autoWireFallback = true;        // add it to the target font's fallback chain when R6 fires
        bool _userTouchedFallback;            // true once the integrator edits the field (manual override)
        internal bool _setRightAlignment = true;       // set the target box's HORIZONTAL alignment to Right on bake
        string _inputPath = "";
        string _outputPath = "";
        Vector2 _scroll;
        internal string _log = "";

        // Hook for the OPTIONAL Unity Localization UI. The integration lives in a SEPARATE assembly
        // (HeshamRTL.Localization.Editor) that compiles ONLY when com.unity.localization is installed,
        // so the main tool never references it. When present, that assembly registers its drawer here
        // (see HeshamRTLLocalizationPanel); when absent, this stays null and the OnGUI call below is a
        // no-op — no missing reference, no error. (Registration replaces the old partial-method hook,
        // which could not span an assembly boundary.)
        internal static Action<HeshamRTLWindow> LocalizationSection;

        [MenuItem("Tools/HeshamRTL/Open", priority = 1)]
        static void Open() => GetWindow<HeshamRTLWindow>("HeshamRTL");

        // On a fresh import / open, pre-populate "Fallback Arabic font" with the bundled
        // asset so Arabic bakes & displays with ZERO manual setup. Only fills when the
        // field is empty and the integrator hasn't deliberately set/cleared it — the
        // manual override stays fully intact.
        void OnEnable() => TryAutoResolveFallback(generateIfMissing: false);

        void TryAutoResolveFallback(bool generateIfMissing)
        {
            if (_fallbackFont != null || _userTouchedFallback) return;
            var fa = HeshamRTLFontBootstrap.LoadBundledFontAsset(generateIfMissing);
            if (fa != null) _fallbackFont = fa;
        }

        // <page>, <hpage>, <page=X> are HARD boundaries kept verbatim (capturing split)
        static readonly Regex PageSplit   = new Regex(@"(<page(?:=[^>]*)?>|<hpage>)", RegexOptions.Compiled);
        static readonly Regex PageTagFull = new Regex(@"^(?:<page(?:=[^>]*)?>|<hpage>)$", RegexOptions.Compiled);
        // Forced line break: <br> / <br/> / <br />, an ESCAPED newline (\n in a quoted
        // value), or a REAL newline (CSV/JSON values can carry one). All -> forced line.
        static readonly Regex BrSplit     = new Regex(@"<br\s*/?>|\\n|\r?\n", RegexOptions.Compiled);
        // A numbered runtime placeholder, e.g. {0}, {12}.  (Anchored form used to tag the
        // protected placeholders that should reserve width during measurement.)
        static readonly Regex NumberedPlaceholder = new Regex(@"\{[0-9]+\}", RegexOptions.Compiled);

        // A {N} placeholder is widened to this many digit glyphs DURING MEASUREMENT ONLY, to
        // reserve visual space for the value injected at runtime under the NoWrap contract — the
        // "Fat-8" buffer. Measurement-only: the baked file keeps {N}. CONFIGURABLE per bake from
        // the window (Shared settings): raise it for fields that inject LONG text (player/item
        // names). NOTE: a {N} whose runtime value is UNBOUNDED text cannot be fully reserved for —
        // such a field is not a good fit for pre-baked wrap (see the docs); size its box for the
        // worst case or shape that one field at runtime instead.
        int _placeholderPadWidth = 6;
        const int PlaceholderPadWidthMin = 1;
        const int PlaceholderPadWidthMax = 256;

        void OnGUI()
        {
            EditorGUILayout.LabelField("HeshamRTL (edit-time)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "RUNTIME CONTRACT: the component that DISPLAYS this baked text must be set to NoWrap. " +
                "Otherwise TMP re-wraps the already-wrapped text and the line order inverts.",
                MessageType.Warning);

            // ----- Shared settings (apply to all three bakers below) -----
            EditorGUILayout.LabelField("Shared settings", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var picked = (TMP_FontAsset)EditorGUILayout.ObjectField(
                    new GUIContent("Fallback Arabic font",
                        "Auto-resolved to the bundled NotoKufiArabic SDF asset on a fresh import — " +
                        "no manual Font Asset Creator step. Assign your own here to override; the " +
                        "bundled asset is regenerated automatically if it is ever missing."),
                    _fallbackFont, typeof(TMP_FontAsset), false);
                if (EditorGUI.EndChangeCheck()) { _fallbackFont = picked; _userTouchedFallback = true; }

                using (new EditorGUI.DisabledScope(_fallbackFont != null && !_userTouchedFallback))
                    if (GUILayout.Button(new GUIContent("Use bundled",
                            "Resolve (generating it on first use if needed) the bundled NotoKufiArabic SDF asset."),
                            GUILayout.Width(90)))
                    {
                        _userTouchedFallback = false;
                        _fallbackFont = HeshamRTLFontBootstrap.LoadBundledFontAsset(generateIfMissing: true);
                        GUI.FocusControl(null);
                    }
            }
            _autoWireFallback = EditorGUILayout.ToggleLeft(
                new GUIContent("Auto-add fallback to target font when forms are missing",
                    "Adds the fallback above to the target font asset's fallback chain (a persistent, " +
                    "edit-time change to that font asset) only when tofu is detected. Turn off to wire it yourself."),
                _autoWireFallback);
            _setRightAlignment = EditorGUILayout.ToggleLeft(
                new GUIContent("Set target box alignment to Right",
                    "At bake time, set the target box's HORIZONTAL alignment to Right (vertical alignment " +
                    "is left unchanged) — Arabic reads right-to-left, so a default left-aligned box looks " +
                    "wrong. Turn off to keep your own alignment. (In the Localization path a box is shared " +
                    "across locales, so instead of touching the box this embeds a per-locale <align=right> " +
                    "tag in the Arabic value only, leaving other locales' alignment untouched.)"),
                _setRightAlignment);

            _placeholderPadWidth = Mathf.Clamp(
                EditorGUILayout.IntField(
                    new GUIContent("Placeholder width reserve  ({N})",
                        "Digit-glyphs reserved for EACH {N} runtime placeholder DURING MEASUREMENT ONLY " +
                        "(the baked output still keeps {N}). 6 fits a number; RAISE it for fields that inject " +
                        "long text — player names, item names, locations. A field whose injected value is " +
                        "UNBOUNDED text is not a good fit for pre-baked wrap: size its box for the worst case, " +
                        "or shape that one field at runtime. Each value's placeholders are reported in the Log."),
                    _placeholderPadWidth),
                PlaceholderPadWidthMin, PlaceholderPadWidthMax);

            // ===== 1) Unity Localization — bake Arabic String Table (the flagship path) =====
            // Renders only when the com.unity.localization package is installed (registered hook).
            LocalizationSection?.Invoke(this);

            // ===== 2) Scene batch (per-box wrapping) =====
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene batch (per-box wrapping)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Bakes EVERY Arabic TMP_Text in the active scene IN PLACE, measuring each box's wrap against " +
                "its OWN width — no single target box. Each baked box is set to NoWrap (runtime contract). " +
                "Undoable; already-baked and non-Arabic boxes are skipped. The file baker's 'Target TMP box' / " +
                "file fields below are not used here.",
                MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Bake active scene (per-box)", GUILayout.Height(28))) BakeScene();

            // ===== 3) File bake — "Apply (Bake)" (the original file-based path) =====
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("File bake (YAML / CSV / JSON → baked file)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Bakes an Arabic YAML / CSV / JSON file: shaping + per-line reversal + wrap measured from the " +
                "chosen Target TMP box. The result is written to a new baked file.",
                MessageType.Info);
            _target = (TMP_Text)EditorGUILayout.ObjectField(
                new GUIContent("Target TMP box", "Scene instance whose width/font/size define wrapping."),
                _target, typeof(TMP_Text), true);
            using (new EditorGUILayout.HorizontalScope())
            {
                _inputPath = EditorGUILayout.TextField("Arabic file (input)", _inputPath);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string p = EditorUtility.OpenFilePanel("Select localization file", "", "yml,yaml,json,csv,txt");
                    if (!string.IsNullOrEmpty(p)) { _inputPath = p; if (string.IsNullOrEmpty(_outputPath)) _outputPath = DefaultOut(p); }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputPath = EditorGUILayout.TextField("Baked file (output)", _outputPath);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string ext = (Path.GetExtension(_inputPath) ?? ".yml").TrimStart('.');
                    if (string.IsNullOrEmpty(ext)) ext = "yml";
                    string p = EditorUtility.SaveFilePanel("Save baked file", "", Path.GetFileName(DefaultOut(_inputPath)), ext);
                    if (!string.IsNullOrEmpty(p)) _outputPath = p;
                }
            }
            using (new EditorGUI.DisabledScope(_target == null || string.IsNullOrEmpty(_inputPath) || string.IsNullOrEmpty(_outputPath)))
                if (GUILayout.Button("Apply (Bake)", GUILayout.Height(32))) Bake();

            // ----- Log -----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(180));
            EditorGUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        static string DefaultOut(string inp) => string.IsNullOrEmpty(inp) ? "" :
            Path.Combine(Path.GetDirectoryName(inp), Path.GetFileNameWithoutExtension(inp) + "_baked" + Path.GetExtension(inp));

        void Bake()
        {
            _log = "";
            try
            {
                if (!File.Exists(_inputPath)) { Log("ERROR: input not found: " + _inputPath); return; }
                bool hadBom;
                string raw = ReadTextStripBom(_inputPath, out hadBom);

                // Decoupled I/O: pick the input adapter by extension. YAML is the back-compat
                // default (also handles .txt); .csv and .json get their own parsers. The baker
                // itself is untouched — it only ever sees one value's text at a time.
                string ext = (Path.GetExtension(_inputPath) ?? "").TrimStart('.');
                LocAdapter adapter = AdapterRegistry.Pick(ext);
                Log("Input format: " + adapter.Id + (hadBom ? "  (UTF-8 BOM preserved)" : ""));
                Log("Wrap API in use: " + WrapApiName(_target));

                // R5 — note any game icon glyphs (U+E000–F8FF); tag placeholders are relocated
                // away from them inside Protect and the icons pass through as atomic units.
                var usedPua = new HashSet<char>();
                foreach (char c in raw) if (c >= '\uE000' && c <= '\uF8FF') usedPua.Add(c);
                if (usedPua.Count > 0)
                    Log($"Note: {usedPua.Count} distinct PUA char(s) (U+E000–F8FF) detected (likely icon/button " +
                        "glyphs) — passed through untouched; tag placeholders relocated to avoid them.");

                List<LocValue> values = adapter.Parse(raw);
                foreach (string w in adapter.ParseWarnings) Log("  WARNING (" + adapter.Id + "): " + w);

                int baked = 0, ltr = 0, already = 0;
                var emitted = new HashSet<char>();   // presentation forms produced (R6)
                _anomalies = 0;

                foreach (LocValue v in values)
                {
                    // .Baked already equals .Source (passthrough) until we overwrite it.
                    if (HeshamRTLBaker.IsBaked(v.Source)) { already++; continue; }
                    if (!HeshamRTLBaker.HasArabic(v.Source)) { ltr++; continue; }
                    v.Baked = BakeValue(_target, v.Source, emitted, v.Key, usedPua);
                    baked++;
                }

                string outText = adapter.Serialize();
                File.WriteAllText(_outputPath, outText, new UTF8Encoding(hadBom));
                Log($"DONE. format={adapter.Id}, baked={baked}, passed-through LTR={ltr}, already-baked={already}.");
                if (_anomalies > 0)
                    Log($"!!! {_anomalies} line(s) tripped the loss/duplication/contiguity guard — INSPECT those keys (see warnings above).");
                Log("Output: " + _outputPath);

                // item 5 — runtime NoWrap safety: warn if the measured box itself isn't NoWrap
                if (!IsNoWrap(GetWrap(_target)))
                    Log("WARNING (NoWrap): the target box is NOT NoWrap. The component that DISPLAYS this baked text " +
                        "at runtime MUST be NoWrap, or TMP re-wraps the pre-wrapped text and the line order inverts.");

                // Auto-sizing (Best Fit) is INCOMPATIBLE with pre-baked wrap: the <br> breaks are
                // frozen for one fixed font size, and auto-sizing rescales the font at display time
                // so they no longer fit. Warn (we never enable it) — disable it for the wrap to hold.
                if (_target.enableAutoSizing)
                    Log("WARNING (auto-size): the target box has auto-sizing (Best Fit) ENABLED. Pre-baked wrap " +
                        "requires a FIXED font size — auto-sizing rescales the font at runtime and the baked line " +
                        "breaks will overflow. Disable auto-sizing (set a fixed font size) on the box(es) that " +
                        "DISPLAY this baked text. The {N} width buffer already reserves space without auto-sizing.");

                // R6 — tofu check against the target box's font, with fallback wiring
                var fa = _target.font;
                if (fa != null)
                {
                    var missing = new List<char>();
                    foreach (char c in emitted) if (!fa.HasCharacter(c)) missing.Add(c);
                    if (missing.Count > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (char c in missing) sb.Append($"U+{(int)c:X4} ");
                        Log($"WARNING (R6): {missing.Count} form(s) MISSING from font '{fa.name}' -> tofu: {sb}");
                        HandleFallback(fa, missing);
                    }
                    else Log($"R6 OK: every emitted form exists in font '{fa.name}'.");
                }

                // Horizontal alignment: Arabic reads RTL, so a default left-aligned box
                // looks wrong. Set the target box's horizontal alignment to Right (vertical
                // left untouched). Persistent + undoable; toggle off to keep your own.
                if (_setRightAlignment && _target != null)
                {
                    Undo.RecordObject(_target, "HeshamRTL: set horizontal alignment Right");
                    SetHorizontalRight(_target);
                    EditorUtility.SetDirty(_target);
                    var sc = _target.gameObject.scene;
                    if (sc.IsValid()) EditorSceneManager.MarkSceneDirty(sc);
                    Log("Set target box horizontal alignment to Right (vertical unchanged). " +
                        "Toggle 'Set target box alignment to Right' off to keep your own alignment.");
                }

                Log("REMINDER: set the runtime display component to NoWrap (see warning at top).");
                AssetDatabase.Refresh();
            }
            catch (Exception e) { Log("EXCEPTION: " + e); }
        }

        // ---- Scene batch: bake EVERY Arabic TMP_Text in the active scene IN PLACE, each
        // measured against its OWN width (no single target box). Each baked box is set to
        // NoWrap (the runtime contract is satisfied right here in-scene). Undoable; already-
        // baked / non-Arabic / empty boxes are skipped. ---------------------------------
        void BakeScene()
        {
            _log = "";
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) { Log("ERROR: exit Play mode before baking the scene."); return; }
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded) { Log("ERROR: no valid, loaded active scene."); return; }

                var boxes = new List<TMP_Text>();
                foreach (var go in scene.GetRootGameObjects())
                    boxes.AddRange(go.GetComponentsInChildren<TMP_Text>(true));   // include inactive
                if (boxes.Count == 0) { Log("No TMP_Text components found in the active scene."); return; }

                Log($"Scene batch: scanning {boxes.Count} TMP_Text component(s) in '{scene.name}'.");
                _anomalies = 0;
                var emitted = new HashSet<char>();
                var touchedFonts = new HashSet<TMP_FontAsset>();
                int baked = 0, ltr = 0, already = 0, empty = 0, autoSizeOn = 0;

                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("HeshamRTL: bake active scene");

                foreach (var box in boxes)
                {
                    string src = box.text;
                    if (string.IsNullOrEmpty(src)) { empty++; continue; }
                    if (HeshamRTLBaker.IsBaked(src)) { already++; continue; }
                    if (!HeshamRTLBaker.HasArabic(src)) { ltr++; continue; }

                    var usedPua = new HashSet<char>();                 // icon glyphs in THIS text
                    foreach (char c in src) if (c >= '\uE000' && c <= '\uF8FF') usedPua.Add(c);

                    string outv;
                    try { outv = BakeValue(box, src, emitted, box.name, usedPua); }
                    catch (Exception ex) { Log($"  ERROR baking '{box.name}': {ex.Message} — skipped."); continue; }

                    Undo.RecordObject(box, "HeshamRTL: bake " + box.name);
                    box.text = outv;
                    SetWrap(box, false);                               // NoWrap runtime contract
                    if (_setRightAlignment) SetHorizontalRight(box);
                    EditorUtility.SetDirty(box);
                    if (box.font != null) touchedFonts.Add(box.font);
                    if (box.enableAutoSizing) autoSizeOn++;            // incompatible with baked wrap (warned below)
                    baked++;
                    Log($"  baked '{box.name}'  (NoWrap{(_setRightAlignment ? ", Right" : "")}{(box.enableAutoSizing ? ", AUTO-SIZE!" : "")}).");
                }

                if (baked > 0 && scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
                Undo.CollapseUndoOperations(undoGroup);

                Log($"Scene DONE. baked={baked}, passed-through LTR={ltr}, already-baked={already}, empty={empty}.");
                if (_anomalies > 0)
                    Log($"!!! {_anomalies} segment(s) tripped the loss/duplication/contiguity guard — INSPECT (see warnings above).");
                if (autoSizeOn > 0)
                    Log($"WARNING (auto-size): {autoSizeOn} baked box(es) have auto-sizing (Best Fit) ENABLED. " +
                        "Pre-baked wrap requires a FIXED font size — auto-sizing rescales the font at runtime so the " +
                        "baked line breaks overflow. Disable auto-sizing (set a fixed size) on those boxes.");

                // R6 tofu check against EACH distinct font that received baked text.
                foreach (var fa in touchedFonts)
                {
                    if (fa == null) continue;
                    var missing = new List<char>();
                    foreach (char c in emitted) if (!fa.HasCharacter(c)) missing.Add(c);
                    if (missing.Count > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (char c in missing) sb.Append($"U+{(int)c:X4} ");
                        Log($"WARNING (R6): {missing.Count} form(s) MISSING from font '{fa.name}' -> tofu: {sb}");
                        HandleFallback(fa, missing);
                    }
                    else Log($"R6 OK: every emitted form exists in font '{fa.name}'.");
                }

                Log("These boxes are now NoWrap in-scene — the runtime contract is satisfied for them.");
                AssetDatabase.Refresh();
            }
            catch (Exception e) { Log("EXCEPTION: " + e); }
        }

        // page-split (tags verbatim) -> bake each page against the given box's metrics
        internal string BakeValue(TMP_Text box, string value, HashSet<char> emitted, string keyForLog, HashSet<char> usedPua)
        {
            // Detect & REPORT runtime placeholders. Their wrap is computed with a FIXED-width
            // reservation (the configurable Fat-8 buffer), so this field's on-screen fit depends on
            // the LENGTH of the value injected at runtime — a long name/item can overflow under
            // NoWrap. Surface it per key so the developer is never silently surprised in-game.
            int ph = NumberedPlaceholder.Matches(value).Count;
            if (ph > 0)
                Log($"  NOTE key[{Trunc(keyForLog)}]: contains {ph} runtime placeholder(s) {{N}} — wrap reserved " +
                    $"{_placeholderPadWidth} glyph(s) each (measurement only; the value keeps {{N}}). Fit depends on " +
                    "the injected value's length; unbounded text (names/items) may overflow — raise the reserve, or " +
                    "size that box for the worst case / shape it at runtime.");

            var sb = new StringBuilder();
            foreach (string part in PageSplit.Split(value))
            {
                if (part.Length == 0) continue;
                if (PageTagFull.IsMatch(part)) sb.Append(part);            // page tag kept as-is
                else sb.Append(BakePage(box, part, emitted, keyForLog, usedPua));
            }
            return sb.ToString();
        }

        // <br>-split (forced lines). Per segment, the PROVEN Sutoor pipeline:
        //   Protect (classify+pair) -> Shape -> MeasureWrap (TMP) -> BalanceSpans
        //   -> per line: VisualOrder + SwapPairs -> Restore.  Rejoin with <br>.
        // Protection happens BEFORE measurement so paired rich-text tags survive
        // (they become 1-char placeholders instead of being eaten by TMP's
        // rich-text parser). Each segment is fully restored before joining, so
        // placeholder codepoints never collide across segments.
        string BakePage(TMP_Text box, string page, HashSet<char> emitted, string keyForLog, HashSet<char> usedPua)
        {
            var outLines = new List<string>();
            foreach (string seg in BrSplit.Split(page))
            {
                if (seg.Length == 0) { outLines.Add(""); continue; }

                Dictionary<char, string> map;
                List<HeshamRTLBaker.SpanPair> pairs;
                string prot = HeshamRTLBaker.Protect(seg, usedPua, out map, out pairs);
                string shaped = HeshamRTLShaper.Shape(prot);

                foreach (char c in shaped)
                    if ((c >= '\uFB50' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF')) emitted.Add(c);

                // Protect turned every {N} into a single PUA placeholder char. Collect the
                // placeholder chars whose ORIGINAL tag is a numbered runtime placeholder, so
                // MeasureWrap can widen ONLY those during the mock measurement ("Fat-8" buffer).
                var fatPlaceholders = new HashSet<char>();
                foreach (var kv in map)
                    if (NumberedPlaceholder.IsMatch(kv.Value)) fatPlaceholders.Add(kv.Key);

                List<string> wrapLines = MeasureWrap(box, shaped, keyForLog, fatPlaceholders);
                if (pairs.Count > 0) wrapLines = HeshamRTLBaker.BalanceSpans(wrapLines, pairs);

                var visLines = new List<string>(wrapLines.Count);
                foreach (string wl in wrapLines)
                {
                    string vis = HeshamRTLBaker.VisualOrder(wl);
                    if (pairs.Count > 0) vis = HeshamRTLBaker.SwapPairs(vis, pairs);
                    visLines.Add(vis);
                }
                outLines.Add(HeshamRTLBaker.Restore(string.Join("<br>", visLines), map));
            }
            return string.Join("<br>", outLines);
        }

        // R6 follow-up: fill tofu using the assigned Arabic fallback font.
        // HasCharacter above checks the BASE font only, so this also flags when a
        // wired fallback already covers the forms (runtime is fine in that case).
        internal void HandleFallback(TMP_FontAsset targetFont, List<char> missing)
        {
            // The field is normally pre-populated on import; if it is somehow empty,
            // resolve (and generate on first use if needed) the bundled asset now so
            // tofu is fixed automatically rather than asking for a manual build.
            if (_fallbackFont == null)
            {
                _fallbackFont = HeshamRTLFontBootstrap.LoadBundledFontAsset(generateIfMissing: true);
                if (_fallbackFont != null)
                    Log($"  Auto-resolved bundled fallback font '{_fallbackFont.name}'.");
            }
            if (_fallbackFont == null)
            {
                Log("  FIX: the bundled 'NotoKufiArabic-Regular.ttf' could not be auto-converted. " +
                    "One-time manual step: Window > TextMeshPro > Font Asset Creator on that ttf " +
                    "(Unicode Range 20-7E,FB50-FDFF,FE70-FEFF), save as 'NotoKufiArabic SDF' next to " +
                    "the ttf, then re-bake — the tool will auto-resolve it afterwards.");
                return;
            }
            int covered = 0;
            foreach (char c in missing) if (_fallbackFont.HasCharacter(c)) covered++;
            if (covered == 0)
            {
                Log($"  NOTE: fallback '{_fallbackFont.name}' does NOT cover the missing forms either — " +
                    "use a font with Arabic presentation forms (the bundled NotoKufiArabic-Regular.ttf does).");
                return;
            }
            bool wired = targetFont.fallbackFontAssetTable != null &&
                         targetFont.fallbackFontAssetTable.Contains(_fallbackFont);
            if (wired)
            {
                Log($"  OK: '{_fallbackFont.name}' is already in '{targetFont.name}' fallback chain and covers " +
                    $"{covered}/{missing.Count} form(s) — runtime resolves them (R6 checks the base font only).");
                return;
            }
            if (_autoWireFallback)
            {
                if (targetFont.fallbackFontAssetTable == null)
                    targetFont.fallbackFontAssetTable = new List<TMP_FontAsset>();
                targetFont.fallbackFontAssetTable.Add(_fallbackFont);
                EditorUtility.SetDirty(targetFont);
                AssetDatabase.SaveAssets();
                Log($"  FIXED: added '{_fallbackFont.name}' to '{targetFont.name}' fallback chain " +
                    $"({covered}/{missing.Count} form(s) now resolve via fallback). " +
                    "Persistent change to that font asset.");
            }
            else
            {
                Log($"  FIX: '{_fallbackFont.name}' covers {covered}/{missing.Count} form(s). Enable " +
                    "'Auto-add fallback…' to wire it, or add it to the target font's fallback list yourself.");
            }
        }

        internal int _anomalies;

        // Put shaped (logical-order) text on the box in naive-LTR with wrap ON, rebuild each
        // line from the actual per-character SOURCE indices (guarded against loss / duplication
        // / reordering), trim boundary spaces, restore box. Any char in fatPlaceholders (a {N}
        // placeholder — already a single PUA char after Protect) is widened to _placeholderPadWidth
        // ('8') glyphs FOR MEASUREMENT ONLY so it reserves runtime width; it stays one atomic source
        // char in the rebuilt line. With no fat placeholders the measurement string equals `shaped`
        // and behaviour is identical to before.
        List<string> MeasureWrap(TMP_Text box, string shaped, string keyForLog, HashSet<char> fatPlaceholders)
        {
            var result = new List<string>();

            // Build the measurement string + a measure-index -> SOURCE-index map.
            bool hasFat = fatPlaceholders != null && fatPlaceholders.Count > 0;
            string measureText;
            int[] m2t;                                  // null => identity (measureText == shaped)
            if (!hasFat) { measureText = shaped; m2t = null; }
            else
            {
                var mb = new StringBuilder(shaped.Length);
                var map = new List<int>(shaped.Length);
                for (int s = 0; s < shaped.Length; s++)
                {
                    if (fatPlaceholders.Contains(shaped[s]))
                        for (int k = 0; k < _placeholderPadWidth; k++) { mb.Append('8'); map.Add(s); }
                    else { mb.Append(shaped[s]); map.Add(s); }
                }
                measureText = mb.ToString();
                m2t = map.ToArray();
            }

            string oText = box.text;
            bool oRtl = box.isRightToLeftText;
            bool oRich = box.richText;
            bool oAuto = box.enableAutoSizing;       // measure at the FIXED size, restore after
            object oWrap = GetWrap(box);
            try
            {
                box.isRightToLeftText = false;       // we do RTL ourselves; keep TMP's bidi off
                box.richText = false;                // the measured string is all placeholders+glyphs;
                                                     // off so any stray '<' is measured, never parsed
                box.enableAutoSizing = false;        // auto-size would scale glyphs -> wrong breaks
                SetWrap(box, true);                  // wrap ON so TMP exposes the break points
                box.text = measureText;
                box.ForceMeshUpdate();

                TMP_TextInfo ti = box.textInfo;
                if (ti == null || ti.lineCount <= 0) { result.Add(shaped.Trim()); return result; }

                var seen   = new int[shaped.Length];     // coverage count per SOURCE index
                var placed = new bool[shaped.Length];    // fat placeholder already assigned to a line
                var splitFlagged = new HashSet<int>();   // fat placeholder reported line-split (once)

                for (int i = 0; i < ti.lineCount; i++)
                {
                    TMP_LineInfo ln = ti.lineInfo[i];
                    if (ln.characterCount <= 0) { result.Add(""); continue; }

                    var idxs = new List<int>(ln.characterCount);
                    var lineSet = new HashSet<int>();
                    for (int j = ln.firstCharacterIndex; j <= ln.lastCharacterIndex; j++)
                    {
                        int mk = ti.characterInfo[j].index;                  // display char -> measure index
                        int ix = (m2t == null) ? mk : (mk >= 0 && mk < m2t.Length ? m2t[mk] : -1);
                        if (ix < 0 || ix >= shaped.Length) continue;

                        bool isFat = hasFat && fatPlaceholders.Contains(shaped[ix]);
                        if (isFat)
                        {
                            if (placed[ix])
                            {
                                // its widened glyphs straddled a break -> keep the placeholder whole
                                // on its FIRST line; warn once that the wrap there is approximate.
                                if (!lineSet.Contains(ix) && splitFlagged.Add(ix))
                                {
                                    _anomalies++;
                                    Log($"  WARNING (Fat-8) key[{Trunc(keyForLog)}]: a {{N}} placeholder's reserved " +
                                        "width wrapped across a line — kept atomic on its first line; wrap approximate.");
                                }
                                continue;
                            }
                            if (lineSet.Contains(ix)) continue;              // its other '8' glyphs on this line
                        }
                        else if (lineSet.Contains(ix)) continue;             // 1:1 normally; guard anyway

                        lineSet.Add(ix);
                        idxs.Add(ix);
                        seen[ix]++;
                        if (isFat) placed[ix] = true;
                    }
                    idxs.Sort();

                    bool contiguous = true;
                    for (int k = 1; k < idxs.Count; k++) if (idxs[k] != idxs[k - 1] + 1) { contiguous = false; break; }
                    if (!contiguous)
                    {
                        _anomalies++;
                        Log($"  WARNING (Bug1) key[{Trunc(keyForLog)}] line {i}: NON-CONTIGUOUS source indices " +
                            "(possible TMP reordering) — verify this line's output.");
                    }

                    var sbLine = new StringBuilder(idxs.Count);
                    foreach (int ix in idxs) sbLine.Append(shaped[ix]);
                    result.Add(sbLine.ToString().Trim());        // trim boundary spaces before reversal
                }

                // loss / duplication self-check across the whole segment
                int dup = 0, missNonSpace = 0;
                for (int ix = 0; ix < shaped.Length; ix++)
                {
                    if (seen[ix] > 1) dup++;
                    else if (seen[ix] == 0 && !char.IsWhiteSpace(shaped[ix])) missNonSpace++;
                }
                if (dup > 0)          { _anomalies++; Log($"  ERROR (Bug1) key[{Trunc(keyForLog)}]: {dup} char(s) in MULTIPLE lines (duplication)."); }
                if (missNonSpace > 0) { _anomalies++; Log($"  ERROR (Bug1) key[{Trunc(keyForLog)}]: {missNonSpace} non-space char(s) DROPPED across lines (loss)."); }
            }
            finally
            {
                box.text = oText;
                box.isRightToLeftText = oRtl;
                box.richText = oRich;
                box.enableAutoSizing = oAuto;
                SetWrap(box, oWrap);
                box.ForceMeshUpdate();
            }
            return result;
        }

        // ----- Bug 2: version-agnostic wrap toggle via reflection ----------------
        static bool _wrapResolved;
        static PropertyInfo _wrapModeProp;   // TMP modern: textWrappingMode (enum)
        static PropertyInfo _wrapBoolProp;   // TMP legacy: enableWordWrapping (bool)

        static void ResolveWrap(TMP_Text t)
        {
            if (_wrapResolved) return;
            var ty = t.GetType();
            _wrapModeProp = ty.GetProperty("textWrappingMode");
            _wrapBoolProp = ty.GetProperty("enableWordWrapping");
            _wrapResolved = true;
        }
        static string WrapApiName(TMP_Text t)
        {
            ResolveWrap(t);
            if (_wrapModeProp != null) return "textWrappingMode (modern TMP)";
            if (_wrapBoolProp != null) return "enableWordWrapping (legacy TMP)";
            return "NONE FOUND (will error)";
        }
        static object GetWrap(TMP_Text t)   // opaque original state to restore later
        {
            ResolveWrap(t);
            if (_wrapModeProp != null) return _wrapModeProp.GetValue(t);
            if (_wrapBoolProp != null) return _wrapBoolProp.GetValue(t);
            return null;
        }
        static void SetWrap(TMP_Text t, bool on)   // enable/disable for measurement
        {
            ResolveWrap(t);
            if (_wrapModeProp != null)
                _wrapModeProp.SetValue(t, Enum.Parse(_wrapModeProp.PropertyType, on ? "Normal" : "NoWrap"));
            else if (_wrapBoolProp != null)
                _wrapBoolProp.SetValue(t, on);
            else
                throw new Exception("No TMP wrapping property (textWrappingMode / enableWordWrapping) found on " + t.GetType().Name);
        }
        static void SetWrap(TMP_Text t, object original)   // restore saved state
        {
            ResolveWrap(t);
            if (original == null) return;
            if (_wrapModeProp != null) _wrapModeProp.SetValue(t, original);
            else if (_wrapBoolProp != null) _wrapBoolProp.SetValue(t, original);
        }
        static bool IsNoWrap(object wrapState)   // is the saved state "NoWrap"?
        {
            if (wrapState == null) return false;
            if (_wrapModeProp != null) return wrapState.Equals(Enum.Parse(_wrapModeProp.PropertyType, "NoWrap"));
            if (_wrapBoolProp != null) return wrapState is bool b && !b;
            return false;
        }

        // Set ONLY horizontal alignment to Right, leaving vertical alignment untouched.
        static void SetHorizontalRight(TMP_Text t)
        {
            // Modern TMP exposes a separate horizontal axis — use it when present so the
            // vertical component is provably unaffected.
            var hProp = typeof(TMP_Text).GetProperty("horizontalAlignment");
            if (hProp != null && hProp.CanWrite)
            {
                hProp.SetValue(t, Enum.Parse(hProp.PropertyType, "Right"));
                return;
            }
            // Fallback (older TMP): TextAlignmentOptions packs horizontal in the low byte
            // and vertical above it. Keep the vertical bits, replace horizontal with Right.
            int vertical = (int)t.alignment & ~0xFF;
            int right    = (int)TextAlignmentOptions.Right & 0xFF;
            t.alignment  = (TextAlignmentOptions)(vertical | right);
        }

        // Read a UTF-8 file as a clean string (BOM stripped so every adapter — including the
        // line-oriented YAML one — sees clean text), reporting whether a BOM was present so the
        // output can reproduce the integrator's choice.
        static string ReadTextStripBom(string path, out bool hadBom)
        {
            byte[] b = File.ReadAllBytes(path);
            hadBom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            var enc = new UTF8Encoding(false);
            return hadBom ? enc.GetString(b, 3, b.Length - 3) : enc.GetString(b);
        }

        static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 24 ? s.Substring(0, 24) : s);
        internal void Log(string s) { _log += s + "\n"; Debug.Log("[HeshamRTL] " + s); }
    }
}
#endif
