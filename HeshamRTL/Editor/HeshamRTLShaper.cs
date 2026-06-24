// HeshamRTLShaper.cs  — AUTO-GENERATED tables (from arabic-reshaper 3.0.0),
// hand-written algorithm (faithful port of reshaper.reshape() under the baker
// config: keep harakat, keep tatweel, isolated_form = ISOLATED, ligatures =
// lam-alef only). Validated byte-identical to the Python reference on 8000+
// strings. Pure C# — no UnityEngine dependency.
using System.Collections.Generic;
using System.Text;

namespace HeshamRTL
{
    public static class HeshamRTLShaper
    {
        const int ISO = 0, INI = 1, MED = 2, FIN = 3, NS = -1;
        const char LAM = '\u0644';

        // base letter -> { isolated, initial, medial, final }; '\0' = form unavailable
        static readonly Dictionary<char, char[]> FORMS = new Dictionary<char, char[]>
        {
            { '\u0621', new[]{ '\uFE80', '\0', '\0', '\0' } },
            { '\u0622', new[]{ '\uFE81', '\0', '\0', '\uFE82' } },
            { '\u0623', new[]{ '\uFE83', '\0', '\0', '\uFE84' } },
            { '\u0624', new[]{ '\uFE85', '\0', '\0', '\uFE86' } },
            { '\u0625', new[]{ '\uFE87', '\0', '\0', '\uFE88' } },
            { '\u0626', new[]{ '\uFE89', '\uFE8B', '\uFE8C', '\uFE8A' } },
            { '\u0627', new[]{ '\uFE8D', '\0', '\0', '\uFE8E' } },
            { '\u0628', new[]{ '\uFE8F', '\uFE91', '\uFE92', '\uFE90' } },
            { '\u0629', new[]{ '\uFE93', '\0', '\0', '\uFE94' } },
            { '\u062A', new[]{ '\uFE95', '\uFE97', '\uFE98', '\uFE96' } },
            { '\u062B', new[]{ '\uFE99', '\uFE9B', '\uFE9C', '\uFE9A' } },
            { '\u062C', new[]{ '\uFE9D', '\uFE9F', '\uFEA0', '\uFE9E' } },
            { '\u062D', new[]{ '\uFEA1', '\uFEA3', '\uFEA4', '\uFEA2' } },
            { '\u062E', new[]{ '\uFEA5', '\uFEA7', '\uFEA8', '\uFEA6' } },
            { '\u062F', new[]{ '\uFEA9', '\0', '\0', '\uFEAA' } },
            { '\u0630', new[]{ '\uFEAB', '\0', '\0', '\uFEAC' } },
            { '\u0631', new[]{ '\uFEAD', '\0', '\0', '\uFEAE' } },
            { '\u0632', new[]{ '\uFEAF', '\0', '\0', '\uFEB0' } },
            { '\u0633', new[]{ '\uFEB1', '\uFEB3', '\uFEB4', '\uFEB2' } },
            { '\u0634', new[]{ '\uFEB5', '\uFEB7', '\uFEB8', '\uFEB6' } },
            { '\u0635', new[]{ '\uFEB9', '\uFEBB', '\uFEBC', '\uFEBA' } },
            { '\u0636', new[]{ '\uFEBD', '\uFEBF', '\uFEC0', '\uFEBE' } },
            { '\u0637', new[]{ '\uFEC1', '\uFEC3', '\uFEC4', '\uFEC2' } },
            { '\u0638', new[]{ '\uFEC5', '\uFEC7', '\uFEC8', '\uFEC6' } },
            { '\u0639', new[]{ '\uFEC9', '\uFECB', '\uFECC', '\uFECA' } },
            { '\u063A', new[]{ '\uFECD', '\uFECF', '\uFED0', '\uFECE' } },
            { '\u0640', new[]{ '\u0640', '\u0640', '\u0640', '\u0640' } },
            { '\u0641', new[]{ '\uFED1', '\uFED3', '\uFED4', '\uFED2' } },
            { '\u0642', new[]{ '\uFED5', '\uFED7', '\uFED8', '\uFED6' } },
            { '\u0643', new[]{ '\uFED9', '\uFEDB', '\uFEDC', '\uFEDA' } },
            { '\u0644', new[]{ '\uFEDD', '\uFEDF', '\uFEE0', '\uFEDE' } },
            { '\u0645', new[]{ '\uFEE1', '\uFEE3', '\uFEE4', '\uFEE2' } },
            { '\u0646', new[]{ '\uFEE5', '\uFEE7', '\uFEE8', '\uFEE6' } },
            { '\u0647', new[]{ '\uFEE9', '\uFEEB', '\uFEEC', '\uFEEA' } },
            { '\u0648', new[]{ '\uFEED', '\0', '\0', '\uFEEE' } },
            { '\u0649', new[]{ '\uFEEF', '\uFBE8', '\uFBE9', '\uFEF0' } },
            { '\u064A', new[]{ '\uFEF1', '\uFEF3', '\uFEF4', '\uFEF2' } },
            { '\u0671', new[]{ '\uFB50', '\0', '\0', '\uFB51' } },
            { '\u0677', new[]{ '\uFBDD', '\0', '\0', '\0' } },
            { '\u0679', new[]{ '\uFB66', '\uFB68', '\uFB69', '\uFB67' } },
            { '\u067A', new[]{ '\uFB5E', '\uFB60', '\uFB61', '\uFB5F' } },
            { '\u067B', new[]{ '\uFB52', '\uFB54', '\uFB55', '\uFB53' } },
            { '\u067E', new[]{ '\uFB56', '\uFB58', '\uFB59', '\uFB57' } },
            { '\u067F', new[]{ '\uFB62', '\uFB64', '\uFB65', '\uFB63' } },
            { '\u0680', new[]{ '\uFB5A', '\uFB5C', '\uFB5D', '\uFB5B' } },
            { '\u0683', new[]{ '\uFB76', '\uFB78', '\uFB79', '\uFB77' } },
            { '\u0684', new[]{ '\uFB72', '\uFB74', '\uFB75', '\uFB73' } },
            { '\u0686', new[]{ '\uFB7A', '\uFB7C', '\uFB7D', '\uFB7B' } },
            { '\u0687', new[]{ '\uFB7E', '\uFB80', '\uFB81', '\uFB7F' } },
            { '\u0688', new[]{ '\uFB88', '\0', '\0', '\uFB89' } },
            { '\u068C', new[]{ '\uFB84', '\0', '\0', '\uFB85' } },
            { '\u068D', new[]{ '\uFB82', '\0', '\0', '\uFB83' } },
            { '\u068E', new[]{ '\uFB86', '\0', '\0', '\uFB87' } },
            { '\u0691', new[]{ '\uFB8C', '\0', '\0', '\uFB8D' } },
            { '\u0698', new[]{ '\uFB8A', '\0', '\0', '\uFB8B' } },
            { '\u06A4', new[]{ '\uFB6A', '\uFB6C', '\uFB6D', '\uFB6B' } },
            { '\u06A6', new[]{ '\uFB6E', '\uFB70', '\uFB71', '\uFB6F' } },
            { '\u06A9', new[]{ '\uFB8E', '\uFB90', '\uFB91', '\uFB8F' } },
            { '\u06AD', new[]{ '\uFBD3', '\uFBD5', '\uFBD6', '\uFBD4' } },
            { '\u06AF', new[]{ '\uFB92', '\uFB94', '\uFB95', '\uFB93' } },
            { '\u06B1', new[]{ '\uFB9A', '\uFB9C', '\uFB9D', '\uFB9B' } },
            { '\u06B3', new[]{ '\uFB96', '\uFB98', '\uFB99', '\uFB97' } },
            { '\u06BA', new[]{ '\uFB9E', '\0', '\0', '\uFB9F' } },
            { '\u06BB', new[]{ '\uFBA0', '\uFBA2', '\uFBA3', '\uFBA1' } },
            { '\u06BE', new[]{ '\uFBAA', '\uFBAC', '\uFBAD', '\uFBAB' } },
            { '\u06C0', new[]{ '\uFBA4', '\0', '\0', '\uFBA5' } },
            { '\u06C1', new[]{ '\uFBA6', '\uFBA8', '\uFBA9', '\uFBA7' } },
            { '\u06C5', new[]{ '\uFBE0', '\0', '\0', '\uFBE1' } },
            { '\u06C6', new[]{ '\uFBD9', '\0', '\0', '\uFBDA' } },
            { '\u06C7', new[]{ '\uFBD7', '\0', '\0', '\uFBD8' } },
            { '\u06C8', new[]{ '\uFBDB', '\0', '\0', '\uFBDC' } },
            { '\u06C9', new[]{ '\uFBE2', '\0', '\0', '\uFBE3' } },
            { '\u06CB', new[]{ '\uFBDE', '\0', '\0', '\uFBDF' } },
            { '\u06CC', new[]{ '\uFBFC', '\uFBFE', '\uFBFF', '\uFBFD' } },
            { '\u06D0', new[]{ '\uFBE4', '\uFBE6', '\uFBE7', '\uFBE5' } },
            { '\u06D2', new[]{ '\uFBAE', '\0', '\0', '\uFBAF' } },
            { '\u06D3', new[]{ '\uFBB0', '\0', '\0', '\uFBB1' } },
            { '\u200D', new[]{ '\u200D', '\u200D', '\u200D', '\u200D' } },
        };

        // alef-variant -> { ligature isolated, ligature final } (preceded by LAM)
        static readonly Dictionary<char, char[]> LAMALEF = new Dictionary<char, char[]>
        {
            { '\u0627', new[]{ '\uFEFB', '\uFEFC' } },
            { '\u0623', new[]{ '\uFEF7', '\uFEF8' } },
            { '\u0625', new[]{ '\uFEF9', '\uFEFA' } },
            { '\u0622', new[]{ '\uFEF5', '\uFEF6' } },
        };

        // harakat (transparent combining marks) ranges [lo,hi] inclusive
        static readonly int[][] HARAKAT = new int[][] { new[]{0x0610,0x061A}, new[]{0x064B,0x065F}, new[]{0x0670,0x0670}, new[]{0x06D6,0x06DC}, new[]{0x06DF,0x06E8}, new[]{0x06EA,0x06ED}, new[]{0x08D4,0x08FF} };

        public static bool IsHaraka(char c)
        {
            int x = c;
            foreach (var r in HARAKAT) if (x >= r[0] && x <= r[1]) return true;
            return false;
        }
        static bool CB(char c)  => FORMS.TryGetValue(c, out var f) && (f[FIN] != '\0' || f[MED] != '\0');
        static bool CA(char c)  => FORMS.TryGetValue(c, out var f) && (f[INI] != '\0' || f[MED] != '\0');
        static bool CBA(char c) => FORMS.TryGetValue(c, out var f) && f[MED] != '\0';

        struct Cell { public char Letter; public int Form; public Cell(char l, int f){ Letter=l; Form=f; } }

        public static string Shape(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var output = new List<Cell>();
            var harakat = new Dictionary<int, List<char>>();  // output-index -> marks

            foreach (char ch in text)
            {
                if (IsHaraka(ch))
                {
                    int p = output.Count - 1;
                    if (!harakat.TryGetValue(p, out var lst)) { lst = new List<char>(); harakat[p] = lst; }
                    lst.Add(ch);
                }
                else if (!FORMS.ContainsKey(ch)) output.Add(new Cell(ch, NS));
                else if (output.Count == 0) output.Add(new Cell(ch, ISO));
                else
                {
                    var prev = output[output.Count - 1];
                    if (prev.Form == NS) output.Add(new Cell(ch, ISO));
                    else if (!CB(ch)) output.Add(new Cell(ch, ISO));
                    else if (!CA(prev.Letter)) output.Add(new Cell(ch, ISO));
                    else if (prev.Form == FIN && !CBA(prev.Letter)) output.Add(new Cell(ch, ISO));
                    else if (prev.Form == ISO)
                    {
                        output[output.Count - 1] = new Cell(prev.Letter, INI);
                        output.Add(new Cell(ch, FIN));
                    }
                    else
                    {
                        output[output.Count - 1] = new Cell(prev.Letter, MED);
                        output.Add(new Cell(ch, FIN));
                    }
                }
            }

            // lam-alef ligature pass (indices align with output; both exclude harakat)
            int i = 0;
            while (i < output.Count - 1)
            {
                var a = output[i]; var b = output[i + 1];
                if (a.Letter == LAM && LAMALEF.TryGetValue(b.Letter, out var lf))
                {
                    int aF = a.Form, bF = b.Form, lig;
                    if (aF == ISO || aF == INI) lig = (bF == ISO || bF == FIN) ? ISO : INI;
                    else                        lig = (bF == ISO || bF == FIN) ? FIN : MED;
                    char chosen = lig == ISO ? lf[0] : (lig == FIN ? lf[1] : '\0');
                    if (chosen != '\0')
                    {
                        output[i] = new Cell(chosen, NS);
                        output[i + 1] = new Cell('\0', NS);   // empty sentinel
                        i += 2; continue;
                    }
                }
                i++;
            }

            var sb = new StringBuilder();
            if (harakat.TryGetValue(-1, out var lead)) foreach (var h in lead) sb.Append(h);
            for (int k = 0; k < output.Count; k++)
            {
                var cell = output[k];
                if (cell.Letter != '\0')
                    sb.Append(cell.Form == NS ? cell.Letter : FORMS[cell.Letter][cell.Form]);
                if (harakat.TryGetValue(k, out var hs)) foreach (var h in hs) sb.Append(h);
            }
            return sb.ToString();
        }
    }
}
