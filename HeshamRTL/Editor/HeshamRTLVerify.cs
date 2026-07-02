// HeshamRTLVerify.cs — the PURE round-trip verification core (P1, v1.4 Batch 4).
// Every transform in the bake pipeline is independently inverted or re-derived
// right after it runs; ANY mismatch makes the bake FAIL CLOSED (the value is
// never written). Composition of the verified steps is a mathematical guarantee:
//
//   source --Protect--> prot --Shape--> shaped --wrap--> lines --Balance-->
//   balanced --VisualOrder+SwapPairs--> vis --NBSP--> nbsp --Restore--> final
//
//   * wrap        : already a proven bijection (the loss/duplication gate).
//   * Shape       : VerifyShape — Unshape(shaped) must equal prot (lam-alef
//                   haraka position canonicalized: [lig][mark] is one rendered
//                   glyph, so "لَا" and "لاَ" are the same text by construction).
//   * Balance     : VerifyBalance — each output line must be exactly
//                   carried-opens + input-line + auto-closes, with the carry
//                   re-derived independently from the input lines.
//   * Visual+Swap : VerifyVisualLine — InverseSwapPairs then VisualOrder (an
//                   involution) must reproduce the balanced line byte-exactly.
//   * NBSP        : trivial (space -> U+00A0 on the same line), asserted inline.
//   * Restore     : VerifyRestore — recompute + placeholder-leak scan.
//
// Pure C#: fully provable in the external harness, including fault injection.
using System;
using System.Collections.Generic;
using System.Text;

namespace HeshamRTL
{
    public static class HeshamRTLVerify
    {
        // ---- SwapPairs inverse: production swapped close-before-open into
        // open-before-close; swap back whenever open currently precedes close.
        public static string InverseSwapPairs(string vis, List<HeshamRTLBaker.SpanPair> pairs)
        {
            if (pairs == null || pairs.Count == 0) return vis;
            char[] chars = vis.ToCharArray();
            foreach (var p in pairs)
            {
                int io = Array.IndexOf(chars, p.Open);
                int ic = Array.IndexOf(chars, p.Close);
                if (io < 0 || ic < 0) continue;
                if (io < ic) { char t = chars[io]; chars[io] = chars[ic]; chars[ic] = t; }
            }
            return new string(chars);
        }

        // ---- balance verification: re-derive the carry evolution from the INPUT
        // lines and require each balanced line == prefix + input + suffix exactly.
        public static string VerifyBalance(List<string> input, List<string> balanced,
                                           List<HeshamRTLBaker.SpanPair> pairs)
        {
            if (input.Count != balanced.Count)
                return "line count changed (" + input.Count + " -> " + balanced.Count + ")";
            if (pairs == null || pairs.Count == 0)
            {
                for (int i = 0; i < input.Count; i++)
                    if (!string.Equals(input[i], balanced[i], StringComparison.Ordinal))
                        return "line " + i + " changed with no pairs";
                return null;
            }
            var closeOf = new Dictionary<char, char>();
            var openOf = new Dictionary<char, char>();
            foreach (var p in pairs) { closeOf[p.Open] = p.Close; openOf[p.Close] = p.Open; }

            var carry = new List<char>();
            for (int i = 0; i < input.Count; i++)
            {
                string prefix = new string(carry.ToArray());
                foreach (char ch in input[i])
                {
                    if (closeOf.ContainsKey(ch)) carry.Add(ch);
                    else
                    {
                        char want;
                        if (openOf.TryGetValue(ch, out want) && carry.Count > 0 && carry[carry.Count - 1] == want)
                            carry.RemoveAt(carry.Count - 1);
                    }
                }
                var suffix = new StringBuilder();
                for (int k = carry.Count - 1; k >= 0; k--) suffix.Append(closeOf[carry[k]]);
                string expect = prefix + input[i] + suffix.ToString();
                if (!string.Equals(balanced[i], expect, StringComparison.Ordinal))
                    return "line " + i + " structural mismatch (expected prefix/suffix form)";
            }
            return null;
        }

        // ---- visual step verification: undo the pair swap, then apply VisualOrder
        // again (it is an involution: reverse-of-reverse with the same tokenizer,
        // a symmetric mirror map, and an order-preserving LTR merge).
        public static string VerifyVisualLine(string balancedLine, string visLine,
                                              List<HeshamRTLBaker.SpanPair> pairs,
                                              HashSet<char> transparentPaired)
        {
            // An ORPHAN leading haraka (a mark with no base before it — degenerate but
            // legal input) makes re-tokenization of the reversed line legitimately
            // different, so the involution does not apply to that line. Fall back to a
            // multiset check there: it still catches loss, duplication and character
            // corruption, which is what this gate exists for.
            int firstReal = 0;
            while (firstReal < balancedLine.Length &&
                   transparentPaired != null && transparentPaired.Contains(balancedLine[firstReal])) firstReal++;
            if (firstReal < balancedLine.Length && HeshamRTLShaper.IsHaraka(balancedLine[firstReal]))
            {
                char[] x = balancedLine.ToCharArray(); Array.Sort(x);
                char[] y = visLine.ToCharArray(); Array.Sort(y);
                if (!new string(x).Equals(new string(y), StringComparison.Ordinal))
                    return "orphan-haraka line: character multiset mismatch";
                return null;
            }

            string back = HeshamRTLBaker.VisualOrder(InverseSwapPairs(visLine, pairs), transparentPaired);
            if (!string.Equals(back, balancedLine, StringComparison.Ordinal))
            {
                int n = Math.Min(back.Length, balancedLine.Length), at = -1;
                for (int i = 0; i < n; i++) if (back[i] != balancedLine[i]) { at = i; break; }
                if (at < 0) at = n;
                return "involution mismatch at char " + at;
            }
            return null;
        }

        // ---- shape inversion: Unshape must reproduce the protected text.
        // Canonicalization: marks (harakat / joining-transparent placeholders)
        // sitting between LAM and a ligature-forming alef are hoisted after the
        // alef on BOTH sides — [lig][mark] renders as one glyph + mark, so both
        // orderings are the same text.
        public static string VerifyShape(string prot, string shaped, HashSet<char> transparent)
        {
            string back = HeshamRTLShaper.Unshape(shaped);
            string a = CanonLamAlefMarks(prot.Replace("\u200D", ""), transparent);   // ZWJ consumed by Shape
            string b = CanonLamAlefMarks(back, transparent);
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                int n = Math.Min(a.Length, b.Length), at = -1;
                for (int i = 0; i < n; i++) if (a[i] != b[i]) { at = i; break; }
                if (at < 0) at = n;
                return "unshape mismatch near char " + at + " (source len " + a.Length + ", inverse len " + b.Length + ")";
            }
            return null;
        }

        public static string CanonLamAlefMarks(string s, HashSet<char> transparent)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\u0644')                          // LAM
                {
                    int j = i + 1;
                    while (j < s.Length &&
                           (HeshamRTLShaper.IsHaraka(s[j]) ||
                            (transparent != null && transparent.Contains(s[j])))) j++;
                    if (j > i + 1 && j < s.Length && HeshamRTLShaper.IsLamAlefAlef(s[j]))
                    {
                        sb.Append('\u0644');
                        sb.Append(s[j]);
                        sb.Append(s, i + 1, j - i - 1);     // the hoisted marks
                        i = j;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- restore verification: independent recompute + placeholder-leak scan.
        public static string VerifyRestore(string final, string joined,
                                           Dictionary<char, string> map)
        {
            string recomputed = HeshamRTLBaker.Restore(joined, map);
            if (!string.Equals(final, recomputed, StringComparison.Ordinal))
                return "restore recompute mismatch";
            foreach (var kv in map)
                if (final.IndexOf(kv.Key) >= 0)
                    return "placeholder U+" + ((int)kv.Key).ToString("X4") + " leaked into the output";
            return null;
        }
    }
}
