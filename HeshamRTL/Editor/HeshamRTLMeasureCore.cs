// HeshamRTLMeasureCore.cs — the PURE half of wrap measurement (no UnityEngine).
// Added in v1.4 (Batch 1: F8 + F13). HeshamRTLWindow.MeasureWrap becomes a thin
// TMP adapter around this core, so the rebuild/guard logic is provable outside
// Unity in the external harness (simulated TMP splits, byte-exact assertions).
//
// F13 — harakat are ZERO-WIDTH in the wrap decision by construction:
//   BuildMeasurePlan emits the measurement string WITHOUT harakat (base chars
//   only). Each omitted haraka is recorded; at rebuild every base character
//   pulls its trailing harakat back with it (they are adjacent in the shaped
//   string — Shape() appends marks right after their base cell). A haraka with
//   NO preceding base (a leading orphan) is measured normally so it can never
//   be dropped.
//
// F8 — the loss/duplication guard is FAIL-CLOSED:
//   RebuildLines reports Dup / MissNonSpace; when either is non-zero the caller
//   must NOT write the value (HeshamRTLGuardException is thrown by the Unity
//   adapter). Coverage counts harakat through their base pull, so omitting them
//   from measurement can never produce false loss errors.
//
// Fat-8 ({N} width reserve) is unchanged in behavior: each fat placeholder char
// expands to padWidth digit glyphs in the measurement string, stays ONE atomic
// source char in the rebuilt line, and a reserve that straddles a break is kept
// whole on its first line with a warning (wrap approximate there).
//
// Minor fix folded in (from the issue register's minor list): boundary trimming
// now strips ASCII space/tab ONLY, so an intentional NBSP at a line boundary is
// preserved (prerequisite for X4's NBSP hardening).
using System;
using System.Collections.Generic;
using System.Text;

namespace HeshamRTL
{
    /// <summary>Thrown when the loss/duplication guard trips: the value must NOT be written.</summary>
    public sealed class HeshamRTLGuardException : Exception
    {
        public HeshamRTLGuardException(string message) : base(message) { }
    }

    public static class HeshamRTLMeasureCore
    {
        /// <summary>Everything the TMP adapter needs to measure, and the rebuild needs to map back.</summary>
        public sealed class MeasurePlan
        {
            public string Shaped;            // the shaped source (rebuild alphabet)
            public string MeasureText;       // what is actually put on the TMP box
            public int[] M2T;                // measure index -> source index
            public bool[] SkippedHaraka;     // per SOURCE index: omitted from measurement (pulled at rebuild)
            public int[] OrdOfSource;        // per SOURCE index: ordinal among MEASURED sources (-1 if skipped)
        }

        public sealed class RebuildResult
        {
            public readonly List<string> Lines = new List<string>();
            public int Dup;                  // source chars that landed in MULTIPLE lines
            public int MissNonSpace;         // non-whitespace source chars dropped everywhere
            public int NonContiguous;        // lines whose measured sources were not contiguous
            public int FatSplits;            // fat reserves that straddled a break (kept atomic; approximate)
            public readonly List<string> Warnings = new List<string>();
            public bool GuardTripped { get { return Dup > 0 || MissNonSpace > 0; } }
        }

        /// <summary>
        /// Build the measurement string: fat placeholders widened to padWidth digit
        /// glyphs (Fat-8, unchanged); harakat omitted (F13) unless they are leading
        /// orphans with no base to attach to.
        /// </summary>
        public static MeasurePlan BuildMeasurePlan(string shaped, HashSet<char> fatPlaceholders, int padWidth)
        {
            bool hasFat = fatPlaceholders != null && fatPlaceholders.Count > 0;
            int n = shaped.Length;
            var mb = new StringBuilder(n);
            var map = new List<int>(n);
            var skipped = new bool[n];
            var ord = new int[n];
            int nextOrd = 0;
            bool sawBase = false;            // any measured non-haraka char so far?

            for (int s = 0; s < n; s++)
            {
                char c = shaped[s];
                if (HeshamRTLShaper.IsHaraka(c) && sawBase)
                {
                    skipped[s] = true;       // zero-width in the wrap decision; pulled at rebuild
                    ord[s] = -1;
                    continue;
                }
                if (hasFat && fatPlaceholders.Contains(c))
                {
                    for (int k = 0; k < padWidth; k++) { mb.Append('8'); map.Add(s); }
                }
                else
                {
                    mb.Append(c);
                    map.Add(s);
                }
                ord[s] = nextOrd++;
                if (!HeshamRTLShaper.IsHaraka(c)) sawBase = true;
            }

            return new MeasurePlan
            {
                Shaped = shaped,
                MeasureText = mb.ToString(),
                M2T = map.ToArray(),
                SkippedHaraka = skipped,
                OrdOfSource = ord,
            };
        }

        /// <summary>
        /// Rebuild the wrapped lines from the per-line MEASURE indices reported by the
        /// layout engine. Deterministic and engine-agnostic: the Unity adapter feeds
        /// TMP's textInfo; the harness feeds simulated splits.
        /// </summary>
        public static RebuildResult RebuildLines(MeasurePlan plan, List<int[]> lineMeasureIndices,
                                                 HashSet<char> fatPlaceholders, string keyForLog)
        {
            string shaped = plan.Shaped;
            int n = shaped.Length;
            bool hasFat = fatPlaceholders != null && fatPlaceholders.Count > 0;
            var rr = new RebuildResult();

            var seen = new int[n];                       // coverage count per SOURCE index
            var placed = new bool[n];                    // fat placeholder already assigned to a line
            var splitFlagged = new HashSet<int>();       // fat placeholder reported line-split (once)

            for (int i = 0; i < lineMeasureIndices.Count; i++)
            {
                int[] mks = lineMeasureIndices[i];
                if (mks == null || mks.Length == 0) { rr.Lines.Add(""); continue; }

                var idxs = new List<int>(mks.Length);
                var lineSet = new HashSet<int>();
                foreach (int mk in mks)
                {
                    int ix = (mk >= 0 && mk < plan.M2T.Length) ? plan.M2T[mk] : -1;
                    if (ix < 0 || ix >= n) continue;

                    bool isFat = hasFat && fatPlaceholders.Contains(shaped[ix]);
                    if (isFat)
                    {
                        if (placed[ix])
                        {
                            // its widened glyphs straddled a break -> keep the placeholder whole
                            // on its FIRST line; warn once that the wrap there is approximate.
                            if (!lineSet.Contains(ix) && splitFlagged.Add(ix))
                            {
                                rr.FatSplits++;
                                rr.Warnings.Add("WARNING (Fat-8) key[" + keyForLog + "]: a {N} placeholder's reserved " +
                                    "width wrapped across a line — kept atomic on its first line; wrap approximate.");
                            }
                            continue;
                        }
                        if (lineSet.Contains(ix)) continue;      // its other '8' glyphs on this line
                    }
                    else if (lineSet.Contains(ix)) continue;     // 1:1 normally; guard anyway

                    lineSet.Add(ix);
                    idxs.Add(ix);
                    seen[ix]++;
                    if (isFat) placed[ix] = true;
                }
                idxs.Sort();

                // contiguity is checked over MEASURED ordinals, so omitted harakat
                // between two bases never read as a gap.
                bool contiguous = true;
                for (int k = 1; k < idxs.Count; k++)
                    if (plan.OrdOfSource[idxs[k]] != plan.OrdOfSource[idxs[k - 1]] + 1) { contiguous = false; break; }
                if (!contiguous)
                {
                    rr.NonContiguous++;
                    rr.Warnings.Add("WARNING (Bug1) key[" + keyForLog + "] line " + i + ": NON-CONTIGUOUS source indices " +
                        "(possible engine reordering) — verify this line's output.");
                }

                // build the line: every base pulls its trailing (omitted) harakat with it
                var sbLine = new StringBuilder(idxs.Count);
                foreach (int ix in idxs)
                {
                    sbLine.Append(shaped[ix]);
                    int k = ix + 1;
                    while (k < n && plan.SkippedHaraka[k])
                    {
                        sbLine.Append(shaped[k]);
                        seen[k]++;
                        k++;
                    }
                }
                rr.Lines.Add(Trim(sbLine.ToString()));           // ASCII space/tab only (NBSP preserved)
            }

            // loss / duplication self-check across the whole segment
            for (int ix = 0; ix < n; ix++)
            {
                if (seen[ix] > 1) rr.Dup++;
                else if (seen[ix] == 0 && !char.IsWhiteSpace(shaped[ix])) rr.MissNonSpace++;
            }
            if (rr.Dup > 0)
                rr.Warnings.Add("ERROR (Bug1) key[" + keyForLog + "]: " + rr.Dup + " char(s) in MULTIPLE lines (duplication).");
            if (rr.MissNonSpace > 0)
                rr.Warnings.Add("ERROR (Bug1) key[" + keyForLog + "]: " + rr.MissNonSpace + " non-space char(s) DROPPED across lines (loss).");
            return rr;
        }

        /// <summary>
        /// F5 — split SHAPED text on forced line separators: the PUA point atoms that
        /// Protect assigned to br-class tags (&lt;br&gt;, &lt;br/&gt;, \n escapes), plus real
        /// newlines (\n, \r\n; a lone \r is text — parity with the old BrSplit regex).
        /// Empty segments are preserved (leading/trailing/consecutive separators).
        /// </summary>
        public static List<string> SplitOnSeparators(string shaped, HashSet<char> sepChars)
        {
            var segs = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < shaped.Length; i++)
            {
                char c = shaped[i];
                if (sepChars != null && sepChars.Contains(c)) { segs.Add(sb.ToString()); sb.Length = 0; continue; }
                if (c == '\n') { segs.Add(sb.ToString()); sb.Length = 0; continue; }
                if (c == '\r' && i + 1 < shaped.Length && shaped[i + 1] == '\n')
                { segs.Add(sb.ToString()); sb.Length = 0; i++; continue; }
                sb.Append(c);
            }
            segs.Add(sb.ToString());
            return segs;
        }

        // boundary trim: ASCII space/tab ONLY — a deliberate NBSP must survive (X4 prerequisite)
        static string Trim(string s)
        {
            int a = 0, b = s.Length;
            while (a < b && (s[a] == ' ' || s[a] == '\t')) a++;
            while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t')) b--;
            return s.Substring(a, b - a);
        }
    }
}
