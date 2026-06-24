// HeshamRTLBaker.cs — the "brain". Pure C# (no UnityEngine). Edit-time only.
//
// The span (paired-tag) handling here is a direct port of the PROVEN, shipped
// mechanics in Sutoor (سطور) arabic_baker.py:
//     _protect      -> Protect      (classify point/open/close, pair by nesting)
//     _balance_spans -> BalanceSpans (a span straddling a wrap is closed at the
//                                     line end and reopened at the next start)
//     _swap_pairs    -> SwapPairs    (positional reversal flips open/close order
//                                     inside a pair; swap them back)
//     visual_order   -> VisualOrder  (reverse grapheme clusters; keep LTR
//                                     islands; mirror brackets)
// The ONE difference from Sutoor: Sutoor computes wrapping itself (advance-sum,
// tags = zero width). This tool asks TMP for the REAL break positions, so tag
// placeholders stay one visible char during measurement. Under the runtime
// NoWrap contract any width approximation is purely cosmetic (it only moves a
// <br>; nothing re-wraps), so this is safe.
//
// Per-segment pipeline (the orchestrator in HeshamRTLWindow drives it):
//   Protect(logical) -> Shape(protected) -> MeasureWrap (TMP) -> BalanceSpans
//   -> per line: VisualOrder + SwapPairs -> Restore.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HeshamRTL
{
    public static class HeshamRTLBaker
    {
        const int PUA = 0xE000;

        // Inline tags / runtime variables / escapes -> protected atomic units.
        static readonly Regex TagRe   = new Regex(@"<[^>]+>|\{[^{}]*\}|\\.", RegexOptions.Compiled);
        // Leading name of a tag: <b> -> b ,  </color> -> color
        static readonly Regex NameRe  = new Regex(@"^</?([A-Za-z]+)", RegexOptions.Compiled);
        // Latin / number islands kept in internal (LTR) order: config.json, v1.2, 50%
        static readonly Regex IslandRe = new Regex(@"[A-Za-z0-9]+(?:[._:/%\-]+[A-Za-z0-9]+)*%?", RegexOptions.Compiled);
        static readonly HashSet<char> NoPua = new HashSet<char>();

        // TMP rich-text tags that come in OPEN/CLOSE pairs (stateful spans).
        // Anything else (<sprite=…>, {0}, \" , an unmatched half) is a point tag.
        static readonly HashSet<string> PairedNames = new HashSet<string>(StringComparer.Ordinal)
        { "b", "i", "u", "s", "color", "size", "mark", "sub", "sup", "link", "font", "gradient", "cspace", "lowercase", "uppercase", "smallcaps", "allcaps", "alpha" };

        static readonly Dictionary<char, char> Mirror = new Dictionary<char, char>
        {
            {'(',')'}, {')','('}, {'[',']'}, {']','['}, {'{','}'}, {'}','{'},
            {'<','>'}, {'>','<'}, {'\u00AB','\u00BB'}, {'\u00BB','\u00AB'},
            {'\u2039','\u203A'}, {'\u203A','\u2039'},
        };

        // open placeholder + its matching close placeholder (one stateful span)
        public struct SpanPair
        {
            public char Open;
            public char Close;
            public SpanPair(char open, char close) { Open = open; Close = close; }
        }

        // ---- guards -----------------------------------------------------------
        public static bool IsBaked(string s)
        {
            foreach (char c in s)
                if ((c >= '\uFB50' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF')) return true;
            return false;
        }

        public static bool HasArabic(string s)
        {
            foreach (char c in s)
                if ((c >= '\u0600' && c <= '\u06FF') || (c >= '\u0750' && c <= '\u077F')) return true;
            return false;
        }

        // ---- tag protection + span classification (port of Sutoor._protect) ---
        // Replaces every tag with a single PUA placeholder NOT used by the file
        // (so game-icon glyphs in U+E000–F8FF are never collided with). Tags are
        // classified point / open / close; open & close are paired by nesting
        // order (nearest open, exactly as Sutoor). Stray halves -> point.
        public static string Protect(string text, HashSet<char> usedPua,
                                     out Dictionary<char, string> mapping,
                                     out List<SpanPair> pairs)
        {
            var map   = new Dictionary<char, string>();
            var roles = new Dictionary<char, int>();   // 0 point, 1 open, 2 close
            int next  = PUA;

            string result = TagRe.Replace(text, match =>
            {
                while (next <= 0xF8FF && usedPua.Contains((char)next)) next++;   // skip game PUA
                if (next > 0xF8FF)
                    throw new Exception("No free PUA placeholder slot (input uses too much of U+E000–F8FF).");
                char ph = (char)next++;
                string raw = match.Value;

                int role = 0;                                    // point by default
                Match nm = NameRe.Match(raw);
                if (nm.Success)
                {
                    string name = nm.Groups[1].Value.ToLowerInvariant();
                    if (PairedNames.Contains(name))
                        role = raw.StartsWith("</", StringComparison.Ordinal) ? 2 : 1;
                }
                map[ph] = raw;
                roles[ph] = role;
                return ph.ToString();
            });

            // pair open/close by nesting order of appearance (nearest open)
            var pairList = new List<SpanPair>();
            var stack = new List<char>();
            foreach (char ch in result)
            {
                int r;
                if (!roles.TryGetValue(ch, out r)) continue;
                if (r == 1) stack.Add(ch);
                else if (r == 2)
                {
                    if (stack.Count > 0)
                    {
                        char open = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        pairList.Add(new SpanPair(open, ch));
                    }
                    else roles[ch] = 0;          // stray close -> point
                }
            }
            // stray opens -> point (nothing else needed; they just stay atomic)
            mapping = map;
            pairs = pairList;
            return result;
        }

        public static string Restore(string text, Dictionary<char, string> mapping)
        {
            foreach (var kv in mapping)
                text = text.Replace(kv.Key.ToString(), kv.Value);
            return text;
        }

        // ---- span balancing across wrap lines (port of Sutoor._balance_spans)-
        // Each wrap line is made locally balanced: reopen at the start every span
        // still open from previous lines, and close at the end every span left
        // open, so the engine's state machine never styles across wrong ranges.
        public static List<string> BalanceSpans(List<string> lines, List<SpanPair> pairs)
        {
            var closeOf = new Dictionary<char, char>();
            var openOf  = new Dictionary<char, char>();
            var rolesOpen  = new HashSet<char>();
            var rolesClose = new HashSet<char>();
            foreach (var p in pairs)
            {
                closeOf[p.Open] = p.Close;
                openOf[p.Close] = p.Open;
                rolesOpen.Add(p.Open);
                rolesClose.Add(p.Close);
            }

            var carry = new List<char>();              // stack of open placeholders
            var outLines = new List<string>(lines.Count);
            foreach (string line in lines)
            {
                string prefix = new string(carry.ToArray());      // reopen carried
                foreach (char ch in line)
                {
                    if (rolesOpen.Contains(ch)) carry.Add(ch);
                    else if (rolesClose.Contains(ch) && carry.Count > 0)
                    {
                        char want;
                        if (openOf.TryGetValue(ch, out want) && carry[carry.Count - 1] == want)
                            carry.RemoveAt(carry.Count - 1);
                    }
                }
                var sb = new StringBuilder();
                for (int i = carry.Count - 1; i >= 0; i--) sb.Append(closeOf[carry[i]]);
                outLines.Add(prefix + line + sb.ToString());
            }
            return outLines;
        }

        // ---- visual order (per-line reversal) --------------------------------
        //  - PUA placeholders: atomic   - Latin/number islands: atomic
        //  - everything else: one grapheme cluster (base + trailing harakat)
        //  reverse token order; mirror single-char bracket clusters.
        public static string VisualOrder(string line)
        {
            var toks = new List<KeyValuePair<string, bool>>(); // (value, isCluster)
            int pos = 0, n = line.Length;
            while (pos < n)
            {
                char c = line[pos];
                if (c >= '\uE000' && c <= '\uF8FF')                 // PUA atom
                {
                    toks.Add(new KeyValuePair<string, bool>(c.ToString(), false));
                    pos++;
                    continue;
                }
                Match im = IslandRe.Match(line, pos);               // island atom
                if (im.Success && im.Index == pos)
                {
                    toks.Add(new KeyValuePair<string, bool>(im.Value, false));
                    pos += im.Length;
                    continue;
                }
                int start = pos++;                                  // grapheme cluster
                while (pos < n && HeshamRTLShaper.IsHaraka(line[pos])) pos++;
                toks.Add(new KeyValuePair<string, bool>(line.Substring(start, pos - start), true));
            }

            var outSb = new StringBuilder();
            for (int i = toks.Count - 1; i >= 0; i--)
            {
                string val = toks[i].Key;
                bool isCluster = toks[i].Value;
                char mm;
                if (isCluster && val.Length == 1 && Mirror.TryGetValue(val[0], out mm))
                    outSb.Append(mm);
                else
                    outSb.Append(val);
            }
            return outSb.ToString();
        }

        // ---- swap open/close inside each pair (port of Sutoor._swap_pairs) ----
        // After positional reversal a pair's close lands before its open; swap
        // them back. Restores correct draw-order for single, nested, sibling.
        public static string SwapPairs(string vis, List<SpanPair> pairs)
        {
            char[] chars = vis.ToCharArray();
            foreach (var p in pairs)
            {
                int io = Array.IndexOf(chars, p.Open);
                int ic = Array.IndexOf(chars, p.Close);
                if (io < 0 || ic < 0) continue;       // pair not on this line
                if (ic < io) { char t = chars[io]; chars[io] = chars[ic]; chars[ic] = t; }
            }
            return new string(chars);
        }

        // ---- single-line convenience (no wrapping) ----------------------------
        // Same pipeline as the wrap path but for one line: Protect -> Shape ->
        // VisualOrder -> SwapPairs -> Restore. (BalanceSpans is the identity on a
        // single line.) Use for lines that fit, or to validate against Python.
        public static string BakeLine(string logical) => BakeLine(logical, NoPua);

        public static string BakeLine(string logical, HashSet<char> usedPua)
        {
            if (IsBaked(logical)) return logical;     // already baked
            if (!HasArabic(logical)) return logical;  // pure LTR -> leave as-is
            Dictionary<char, string> map;
            List<SpanPair> pairs;
            string prot = Protect(logical, usedPua, out map, out pairs);
            string shaped = HeshamRTLShaper.Shape(prot);
            string vis = VisualOrder(shaped);
            if (pairs.Count > 0) vis = SwapPairs(vis, pairs);
            return Restore(vis, map);
        }
    }
}
