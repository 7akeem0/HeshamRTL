// HeshamRTLFontBootstrap.cs — Editor-only. Auto-generates the bundled Arabic
// TMP Font Asset from NotoKufiArabic-Regular.ttf on first import, pins it to a
// stable GUID, and acts as the single source of truth the window uses to resolve
// the "Fallback Arabic font" field automatically.
//
// WHY GENERATE INSTEAD OF SHIPPING A PRE-BAKED .asset?
//   A TMP Font Asset's SDF atlas is produced by Unity's native font engine
//   (FreeType + Unity's SDF generator). It cannot be authored correctly outside
//   Unity, and a hand-written .asset would embed a TMP_FontAsset script GUID that
//   may not match the integrator's installed TMP version. Generating once, in the
//   integrator's own editor, yields a CORRECT atlas with the RIGHT script GUID and
//   zero manual steps. After first import the .asset is a normal committed project
//   asset with the stable GUID pinned below.
//
// FOOTPRINT: this file lives under Editor/, so the generation logic never ships in
// a player build. The GENERATED .asset lives in the non-Editor Fonts/ folder, so
// Unity includes it in a build ONLY if the integrator references it from a shipped
// component — an unreferenced asset is stripped. (An Editor-folder asset, by
// contrast, could not be legally referenced by a shipped scene at all.)
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace HeshamRTL
{
    [InitializeOnLoad]
    public static class HeshamRTLFontBootstrap
    {
        // ---- Stable identity (single source of truth) --------------------------
        // GUID of the bundled .ttf (pinned by the shipped NotoKufiArabic-Regular.ttf.meta).
        public const string TtfGuid   = "ab12cd34ef56ab78cd90ef12ab34cd56";
        // Stable GUID the generated font asset is pinned to (so references survive).
        public const string AssetGuid = "fe09dc87ba65fe43dc21ba09fe87dc65";

        const string TtfFileName   = "NotoKufiArabic-Regular.ttf";
        const string AssetFileName = "NotoKufiArabic SDF.asset";

        // ---- Generation parameters (mirror "Font Asset Creator" exactly) -------
        //   Character Set = Unicode Range (Hex):  20-7E, FB50-FDFF, FE70-FEFF
        //   Render mode SDFAA, 90pt sampling, 9px padding, 1024x1024, multi-atlas on.
        const int  SamplingPointSize = 90;
        const int  AtlasPadding      = 9;
        const int  AtlasWidth        = 1024;
        const int  AtlasHeight       = 1024;
        static readonly GlyphRenderMode RenderMode = GlyphRenderMode.SDFAA;

        static readonly (uint lo, uint hi)[] Ranges =
        {
            (0x0020, 0x007E),   // Basic Latin (ASCII printable)
            (0xFB50, 0xFDFF),   // Arabic Presentation Forms-A
            (0xFE70, 0xFEFF),   // Arabic Presentation Forms-B
        };

        const string SessionDoneKey = "HeshamRTLBaker.FontBootstrap.Done";
        const string SessionWaitLoggedKey = "HeshamRTLBaker.FontBootstrap.WaitLogged";

        // -----------------------------------------------------------------------
        static HeshamRTLFontBootstrap()
        {
            // Run after the import/compile that triggered this reload. On a brand-new
            // project TMP's essential resources may not be imported yet, so generation
            // DEFERS and re-arms (below) instead of failing — a first-time integrator
            // never has to touch the Regenerate menu.
            EditorApplication.delayCall += TryGenerateDeferred;

            // Catch the moment "TMP Essential Resources" finish importing. Importing an
            // asset-only package does not guarantee a domain reload, so delayCall alone
            // wouldn't re-fire — this hook covers that case.
            AssetDatabase.importPackageCompleted -= OnPackageImported;
            AssetDatabase.importPackageCompleted += OnPackageImported;
        }

        // Re-check on the next editor tick after any package import settles.
        static void OnPackageImported(string packageName) => EditorApplication.delayCall += TryGenerateDeferred;

        static void TryGenerateDeferred()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (BuildPipeline.isBuildingPlayer) return;
            if (SessionState.GetBool(SessionDoneKey, false)) return;
            EnsureGenerated();   // marks SessionDoneKey itself once it has settled
        }

        // TMP "Essential Resources" (TMP Settings + the default font/material/shaders
        // CreateFontAsset relies on) must be imported first. Their TMP Settings asset is
        // the canonical marker, and this AssetDatabase check is side-effect-free.
        static bool IsTmpReady()
        {
            try { return AssetDatabase.FindAssets("t:TMP_Settings").Length > 0; }
            catch { return false; }
        }

        /// <summary>Resolve the bundled font asset; optionally generate it if absent.</summary>
        public static TMP_FontAsset LoadBundledFontAsset(bool generateIfMissing)
        {
            TMP_FontAsset fa = LoadExisting();
            if (fa == null && generateIfMissing) { EnsureGenerated(); fa = LoadExisting(); }
            return fa;
        }

        static TMP_FontAsset LoadExisting()
        {
            // 1) by stable GUID (preferred)
            string path = AssetDatabase.GUIDToAssetPath(AssetGuid);
            if (!string.IsNullOrEmpty(path))
            {
                var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (fa != null) return fa;
            }
            // 2) by path beside the bundled ttf (GUID-independent fallback)
            string ttf = ResolveTtfPath();
            if (!string.IsNullOrEmpty(ttf))
            {
                string asset = Path.Combine(Path.GetDirectoryName(ttf), AssetFileName).Replace('\\', '/');
                var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(asset);
                if (fa != null) return fa;
            }
            // 3) last resort: search by name + type
            foreach (string g in AssetDatabase.FindAssets("NotoKufiArabic SDF t:TMP_FontAsset"))
            {
                var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(g));
                if (fa != null) return fa;
            }
            return null;
        }

        [MenuItem("Tools/HeshamRTL/Regenerate Bundled Font Asset", priority = 20)]
        public static void RegenerateMenu()
        {
            string ttf = ResolveTtfPath();
            if (!string.IsNullOrEmpty(ttf))
            {
                string asset = ResolveFontAssetOutputPath(ttf);
                if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(asset) != null)
                    AssetDatabase.DeleteAsset(asset);
            }
            EnsureGenerated(force: true);
        }

        /// <summary>Create the bundled font asset if it does not already exist.</summary>
        public static void EnsureGenerated(bool force = false)
        {
            if (!force && LoadExisting() != null) { SessionState.SetBool(SessionDoneKey, true); return; }

            // Ordering guard: if TMP essentials aren't imported yet, DON'T error. Defer
            // quietly and leave SessionDoneKey unset so we retry on the next domain
            // reload / package import (e.g. when the integrator imports TMP Essentials).
            if (!IsTmpReady())
            {
                if (force || !SessionState.GetBool(SessionWaitLoggedKey, false))
                    Debug.Log("[HeshamRTL] TextMeshPro essential resources aren't imported yet — the " +
                        "bundled Arabic font asset will be generated automatically once they are " +
                        "(Window > TextMeshPro > Import TMP Essential Resources). No action needed.");
                SessionState.SetBool(SessionWaitLoggedKey, true);
                return;
            }

            string ttfPath = ResolveTtfPath();
            if (string.IsNullOrEmpty(ttfPath))
            {
                Debug.LogWarning("[HeshamRTL] Bundled '" + TtfFileName + "' not found. " +
                    "Keep it in the package (Fonts/ folder). The 'Fallback Arabic font' field can " +
                    "still be assigned manually.");
                SessionState.SetBool(SessionDoneKey, true);
                return;
            }

            var srcFont = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
            if (srcFont == null)
            {
                Debug.LogWarning("[HeshamRTL] '" + ttfPath + "' did not import as a Font; cannot " +
                    "build the TMP Font Asset automatically. Assign 'Fallback Arabic font' manually.");
                SessionState.SetBool(SessionDoneKey, true);
                return;
            }

            string assetPath = ResolveFontAssetOutputPath(ttfPath);

            try
            {
                // Unity's own engine builds the atlas — guarantees a correct SDF + the
                // right TMP_FontAsset script GUID for THIS project's TMP version.
                var fontAsset = TMP_FontAsset.CreateFontAsset(
                    srcFont, SamplingPointSize, AtlasPadding, RenderMode,
                    AtlasWidth, AtlasHeight,
                    AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);

                if (fontAsset == null)
                {
                    Debug.LogWarning("[HeshamRTL] TMP_FontAsset.CreateFontAsset returned null " +
                        "(unexpected). Assign 'Fallback Arabic font' manually via Font Asset Creator.");
                    SessionState.SetBool(SessionDoneKey, true);
                    return;
                }

                fontAsset.name = Path.GetFileNameWithoutExtension(AssetFileName);
                AssetDatabase.CreateAsset(fontAsset, assetPath);

                // Persist atlas texture(s) + material as sub-assets of the .asset.
                if (fontAsset.material != null)
                {
                    fontAsset.material.name = fontAsset.name + " Material";
                    if (!AssetDatabase.Contains(fontAsset.material))
                        AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
                }
                AddAtlasTextures(fontAsset);

                // Pre-render exactly the requested ranges so the glyphs the baker emits
                // exist immediately (no first-frame hitch, no tofu). Dynamic mode still
                // renders any other requested glyph on demand as a safety net.
                uint[] unicodes = BuildUnicodeList();
                fontAsset.TryAddCharacters(unicodes, out uint[] missing);
                AddAtlasTextures(fontAsset);   // re-add any atlas page created while warming

                // Ship the warmed atlas as-is (don't clear baked glyph data on build).
                // Set reflectively: this property is absent on older TMP, and a direct
                // reference would break compilation of the whole Editor assembly there.
                var clearProp = typeof(TMP_FontAsset).GetProperty("clearDynamicDataOnBuild");
                if (clearProp != null && clearProp.CanWrite) clearProp.SetValue(fontAsset, false);

                EditorUtility.SetDirty(fontAsset);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

                int requested = 0; foreach (var r in Ranges) requested += (int)(r.hi - r.lo + 1);
                int missingCount = missing != null ? missing.Length : 0;
                Debug.Log($"[HeshamRTL] Generated bundled font asset '{fontAsset.name}' at {assetPath} " +
                          $"({requested - missingCount}/{requested} requested code points present; the rest are " +
                          "unassigned/unsupported and harmless). Zero manual setup needed.");

                PinGuid(assetPath, AssetGuid);
                SessionState.SetBool(SessionDoneKey, true);
            }
            catch (MissingMethodException)
            {
                Debug.LogWarning("[HeshamRTL] This TMP version lacks TMP_FontAsset.CreateFontAsset. " +
                    "One-time fallback: Window > TextMeshPro > Font Asset Creator on " + TtfFileName +
                    " (Unicode Range 20-7E,FB50-FDFF,FE70-FEFF), save next to the ttf as '" + AssetFileName +
                    "'. The window then auto-resolves it.");
                SessionState.SetBool(SessionDoneKey, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HeshamRTL] Auto font-asset generation failed (" + e.Message +
                    "). The 'Fallback Arabic font' field can be assigned manually; baking is unaffected.");
                SessionState.SetBool(SessionDoneKey, true);
            }
        }

        static void AddAtlasTextures(TMP_FontAsset fontAsset)
        {
            var atlases = fontAsset.atlasTextures;
            if (atlases == null) return;
            for (int i = 0; i < atlases.Length; i++)
            {
                var tex = atlases[i];
                if (tex == null) continue;
                if (string.IsNullOrEmpty(tex.name)) tex.name = fontAsset.name + " Atlas " + i;
                if (!AssetDatabase.Contains(tex))
                    AssetDatabase.AddObjectToAsset(tex, fontAsset);
            }
        }

        static uint[] BuildUnicodeList()
        {
            var list = new List<uint>(1600);
            foreach (var r in Ranges)
                for (uint c = r.lo; c <= r.hi; c++) list.Add(c);
            return list.ToArray();
        }

        // Best-effort GUID pin so integrator references stay stable. Runs at most once
        // (only right after creation, when the freshly-minted GUID differs). Path-based
        // resolution above still works even if this no-ops.
        static void PinGuid(string assetPath, string desiredGuid)
        {
            try
            {
                if (AssetDatabase.AssetPathToGUID(assetPath) == desiredGuid) return;

                string metaPath = assetPath + ".meta";
                AssetDatabase.SaveAssets();
                if (!File.Exists(metaPath)) return;

                string txt = File.ReadAllText(metaPath);
                string patched = Regex.Replace(txt, @"(?m)^guid:\s*[0-9a-fA-F]+\s*$", "guid: " + desiredGuid);
                if (patched == txt) return;

                File.WriteAllText(metaPath, patched);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HeshamRTL] Could not pin the font asset GUID (path resolution " +
                    "still works, so auto-wiring is unaffected): " + e.Message);
            }
        }

        static string ResolveTtfPath()
        {
            string path = AssetDatabase.GUIDToAssetPath(TtfGuid);
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;

            foreach (string g in AssetDatabase.FindAssets("NotoKufiArabic-Regular t:Font"))
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(p) && Path.GetFileName(p).Equals(TtfFileName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        // ---- Install-location safety (manual Assets/ copy vs immutable UPM package) -------------
        // When the tool is added via "Add package from git URL" (or a registry), Unity stores it in
        // an immutable, READ-ONLY location (Library/PackageCache/...). The code still runs, but it
        // cannot write the generated font .asset next to its own .ttf. In that case we write the asset
        // to a fixed, writable project folder instead. Asset references are by GUID and resolve across
        // the Assets/ <-> Packages/ boundary, so the read-only .ttf only needs to be READABLE; the
        // existing GUID pin and the FindAssets-by-name lookup keep working at the new location.

        // True only when the tool lives in an immutable, read-only package location. Embedded and
        // local "file:" packages live under Packages/ too but ARE writable, so they are treated like
        // a normal Assets/ install. (A plain "starts with Packages/" check is NOT enough for that.)
        static bool IsImmutableInstall(string anyToolAssetPath)
        {
            if (string.IsNullOrEmpty(anyToolAssetPath)) return false;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(anyToolAssetPath);
            if (info == null) return false;                                          // under Assets/ -> writable
            return info.source != UnityEditor.PackageManager.PackageSource.Embedded   // embedded -> writable
                && info.source != UnityEditor.PackageManager.PackageSource.Local;     // local "file:" -> writable
            // Git / Registry / BuiltIn -> immutable -> redirect to a writable location.
        }

        // Where the generated font asset should be written. Manual/embedded/local install: next to the
        // bundled .ttf (UNCHANGED). Immutable package install: a fixed, writable, NON-Editor project
        // folder, so the asset can still ship if the integrator references it from a built scene.
        static string ResolveFontAssetOutputPath(string ttfPath)
        {
            if (IsImmutableInstall(ttfPath))
                return EnsureProjectFolder("Assets/HeshamRTL/Generated") + "/" + AssetFileName;
            return Path.Combine(Path.GetDirectoryName(ttfPath), AssetFileName).Replace('\\', '/');
        }

        // Create a nested project folder through the AssetDatabase (so CreateAsset accepts it as a
        // parent), building each missing segment. "Assets" is always valid, so the recursion ends.
        static string EnsureProjectFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return folder;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf   = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureProjectFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
            return folder;
        }
    }
}
#endif
