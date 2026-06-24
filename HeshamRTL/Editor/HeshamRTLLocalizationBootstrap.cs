// HeshamRTLLocalizationBootstrap.cs — Editor-only. ALWAYS compiles: it has NO feature #if and
// NO compile-time dependency on the Unity Localization package. Its single job is to keep the
// HESHAMRTL_LOCALIZATION scripting define in sync with whether com.unity.localization is
// installed, so the optional integration in HeshamRTLLocalization.cs (which IS guarded by
// `#if HESHAMRTL_LOCALIZATION`) turns itself on when the package is present and is stripped —
// with ZERO compile errors — when it is not. This mirrors how HeshamRTLFontBootstrap auto-wires
// the bundled font: the integrator never flips a switch.
//
// Two safeguards keep this from ever breaking a project's compilation:
//   1) RECONCILE on every domain reload (InitializeOnLoad): probe for the Localization types and
//      add/remove the define to match. It writes only on a REAL change, so it self-stabilises
//      and never loops (the define edit recompiles; the next run finds it already correct).
//   2) PRE-CLEAR on package removal (PackageManager.registeringPackages): the instant the
//      package is about to be REMOVED, clear the define BEFORE the recompile that would
//      otherwise try to build the #if'd feature code against the now-missing assemblies.
//
// Manual recovery (only if the package was deleted straight off disk so neither safeguard ran):
// remove HESHAMRTL_LOCALIZATION from Project Settings > Player > Scripting Define Symbols. Unity
// lets you edit that even while scripts fail to compile.
#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace HeshamRTL
{
    [InitializeOnLoad]
    static class HeshamRTLLocalizationBootstrap
    {
        const string Define      = "HESHAMRTL_LOCALIZATION";
        const string PackageName = "com.unity.localization";

        static HeshamRTLLocalizationBootstrap()
        {
            EditorApplication.delayCall += Reconcile;          // run once PlayerSettings is ready
            Events.registeringPackages -= OnRegistering;
            Events.registeringPackages += OnRegistering;
        }

        // Fires BEFORE the package set is swapped and scripts recompile. If localization is on its
        // way out, drop the define now so the upcoming compile never sees the #if'd feature code.
        static void OnRegistering(PackageRegistrationEventArgs args)
        {
            try
            {
                if (args != null && args.removed != null && args.removed.Any(p => p != null && p.name == PackageName))
                    SetDefine(false);
            }
            catch { /* never block a package operation */ }
        }

        static void Reconcile()
        {
            try { SetDefine(IsLocalizationPresent()); }
            catch { /* a bootstrap must never throw */ }
        }

        // "Present" == BOTH the runtime component type AND the editor settings type are loadable,
        // i.e. both Unity.Localization and Unity.Localization.Editor are referenceable from the
        // predefined Editor assembly (which is what the integration compiles into). We probe by
        // type name across loaded assemblies, so this file needs no reference to the package.
        static bool IsLocalizationPresent()
        {
            return ResolveType("UnityEngine.Localization.Components.LocalizeStringEvent") != null
                && ResolveType("UnityEditor.Localization.LocalizationEditorSettings") != null;
        }

        static Type ResolveType(string fullName)
        {
            var t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(fullName); if (t != null) return t; }
                catch { /* reflection-only / dynamic assemblies */ }
            }
            return null;
        }

        // Add/remove the define for the currently selected build target group. Editor assemblies
        // recompile when the active build target changes (which triggers a domain reload, so
        // Reconcile re-runs for the newly selected group), so targeting the selected group is
        // correct and platform switches are handled automatically.
        static void SetDefine(bool wanted)
        {
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown) return;

            string current = GetDefines(group);
            var parts = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            bool has = parts.Contains(Define);
            if (wanted == has) return;                         // already correct -> no write, no recompile loop

            if (wanted) parts.Add(Define);
            else        parts.RemoveAll(p => p == Define);
            SetDefines(group, string.Join(";", parts));
        }

        // The BuildTargetGroup-based define API exists on every Unity version (it is merely marked
        // obsolete on the newest in favour of NamedBuildTarget). Calling it through reflection
        // keeps this file warning-free AND compiling on both old and new editors.
        static MethodInfo _get, _set;
        static void ResolveDefineApi()
        {
            if (_get != null && _set != null) return;
            var ps = typeof(PlayerSettings);
            _get = ps.GetMethod("GetScriptingDefineSymbolsForGroup", new[] { typeof(BuildTargetGroup) });
            _set = ps.GetMethod("SetScriptingDefineSymbolsForGroup", new[] { typeof(BuildTargetGroup), typeof(string) });
        }
        static string GetDefines(BuildTargetGroup g)
        {
            ResolveDefineApi();
            return (_get != null ? _get.Invoke(null, new object[] { g }) as string : "") ?? "";
        }
        static void SetDefines(BuildTargetGroup g, string defines)
        {
            ResolveDefineApi();
            _set?.Invoke(null, new object[] { g, defines });
        }
    }
}
#endif
