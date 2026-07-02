// HeshamRTLAdapters.cs — the decoupled I/O layer (Adapter pattern). Edit-time only.
//
//   [Input Parser] -> [HeshamRTLBaker (pure logic, untouched)] -> [Output Serializer]
//
// Each adapter parses ONE file into an ordered list of LocValue "slots", the
// orchestrator (HeshamRTLWindow) bakes each slot's text via the SAME pipeline as
// before, then the adapter re-serialises using each slot's Baked value.
//
//  * YamlAdapter — line-oriented, PRESERVES comments / blank lines / formatting
//                  (writes back prefix + baked + suffix per `KEY: "value"` line).
//  * CsvAdapter  — comma / semicolon / tab (auto-detected), RFC-4180 quoting,
//                  BOM-tolerant; bakes any CELL that contains Arabic.
//  * JsonAdapter — self-contained recursive-descent parser/serialiser (no external
//                  dependency); nested objects/arrays supported; bakes any string
//                  VALUE that contains Arabic (keys are never touched).
//
// Pure C# (no UnityEngine) on purpose — keeps the baking brain engine-agnostic.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HeshamRTL
{
    /// <summary>One localizable value. The orchestrator fills <see cref="Baked"/>.</summary>
    public sealed class LocValue
    {
        public readonly string Key;     // for logging only
        public readonly string Source;  // original value text (baker input)
        public string Baked;            // baker output (defaults to Source = passthrough)
        public LocValue(string key, string source) { Key = key; Source = source; Baked = source; }
    }

    /// <summary>One adapter instance handles one file: Parse, then Serialize.</summary>
    public abstract class LocAdapter
    {
        public abstract string Id { get; }
        public readonly List<string> ParseWarnings = new List<string>();
        public abstract List<LocValue> Parse(string raw);
        public abstract string Serialize();
    }

    public static class AdapterRegistry
    {
        public static LocAdapter Pick(string extNoDot)
        {
            switch ((extNoDot ?? "").ToLowerInvariant())
            {
                case "json": return new JsonAdapter();
                case "csv":  return new CsvAdapter();
                case "yml":
                case "yaml":
                case "txt":
                default:     return new YamlAdapter();   // line-oriented (back-compat default)
            }
        }
    }

    // ===================== YAML (line-oriented, format-preserving) ==============
    public sealed class YamlAdapter : LocAdapter
    {
        public override string Id { get { return "YAML"; } }

        //  KEY: "value"  [# comment]  -> g1 = prefix incl. opening quote,
        //  g2 = value (escape-aware, non-greedy: stops at the TRUE closing quote,
        //  even when a trailing comment contains a quote), g3 = closing quote +
        //  whitespace + optional #comment — written back VERBATIM (F9).
        static readonly Regex LineRe =
            new Regex("^(\\s*[^:#\\s][^:]*:\\s*\")((?:\\\\.|[^\"\\\\])*)(\"\\s*(?:#.*)?)$", RegexOptions.Compiled);
        // a KEY: "…  that opens a quote but has no closing quote on the same line
        static readonly Regex OpensQuoteNoClose =
            new Regex("^\\s*[^:#\\s][^:]*:\\s*\"(?:\\\\.|[^\"\\\\])*$", RegexOptions.Compiled);
        // any KEY: line (for the F9 diagnostic on unquoted Arabic values)
        static readonly Regex KeyLineRe =
            new Regex("^\\s*[^:#\\s][^:]*:", RegexOptions.Compiled);

        struct Rec { public bool IsValue; public string Raw; public string Prefix; public string Suffix; public LocValue Val; }
        readonly List<Rec> _recs = new List<Rec>();
        string _newline = "\r\n";      // F9: dominant newline of the SOURCE file, re-emitted as-is

        public override List<LocValue> Parse(string raw)
        {
            _recs.Clear();
            ParseWarnings.Clear();
            var values = new List<LocValue>();
            _newline = raw.Contains("\r\n") ? "\r\n" : (raw.IndexOf('\n') >= 0 ? "\n" : "\r\n");
            string[] lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match m = LineRe.Match(line);
                if (m.Success)
                {
                    var v = new LocValue(m.Groups[1].Value, m.Groups[2].Value);
                    var r = new Rec();
                    r.IsValue = true; r.Prefix = m.Groups[1].Value; r.Suffix = m.Groups[3].Value; r.Val = v; r.Raw = line;
                    _recs.Add(r);
                    values.Add(v);
                }
                else
                {
                    if (OpensQuoteNoClose.IsMatch(line))
                        ParseWarnings.Add("line " + (i + 1) + ": opens a quoted value with no closing quote on " +
                                          "the same line — NOT baked (this tool expects one physical line per key).");
                    else if (KeyLineRe.IsMatch(line) && HeshamRTLBaker.HasArabic(line))
                        ParseWarnings.Add("line " + (i + 1) + ": Arabic value not in quoted single-line form " +
                                          "(KEY: \"value\") — skipped (F9 diagnostic).");
                    var r = new Rec();
                    r.IsValue = false; r.Raw = line;
                    _recs.Add(r);
                }
            }
            return values;
        }

        public override string Serialize()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _recs.Count; i++)
            {
                Rec r = _recs[i];
                if (r.IsValue) sb.Append(r.Prefix).Append(r.Val.Baked).Append(r.Suffix);
                else sb.Append(r.Raw);
                if (i < _recs.Count - 1) sb.Append(_newline);   // F9: keep the source file's newline
            }
            return sb.ToString();
        }
    }

    // ===================== CSV (delimiter auto-detect, RFC-4180) ================
    public sealed class CsvAdapter : LocAdapter
    {
        public override string Id { get { return "CSV"; } }

        char _delim = ',';
        string _newline = "\r\n";
        bool _hadFinalNewline;
        struct Field { public string Text; public bool Quoted; public LocValue Val; }   // Val != null => bakeable
        readonly List<List<Field>> _grid = new List<List<Field>>();

        public override List<LocValue> Parse(string raw)
        {
            _grid.Clear();
            ParseWarnings.Clear();
            var values = new List<LocValue>();
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw.Substring(1);   // strip BOM
            _newline = raw.Contains("\r\n") ? "\r\n" : (raw.IndexOf('\n') >= 0 ? "\n" : "\r\n");
            _hadFinalNewline = raw.Length > 0 && raw[raw.Length - 1] == '\n';    // N1
            _delim = DetectDelimiter(raw);

            int n = raw.Length, pos = 0;
            var row = new List<Field>();
            var cur = new StringBuilder();
            bool inQuotes = false, quotedField = false;
            while (pos < n)
            {
                char c = raw[pos];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (pos + 1 < n && raw[pos + 1] == '"') { cur.Append('"'); pos += 2; continue; }
                        inQuotes = false; pos++; continue;
                    }
                    cur.Append(c); pos++; continue;
                }
                if (c == '"') { inQuotes = true; quotedField = true; pos++; continue; }
                if (c == _delim) { row.Add(MakeField(cur.ToString(), quotedField, values)); cur.Length = 0; quotedField = false; pos++; continue; }
                if (c == '\r') { pos++; continue; }                  // normalise CRLF
                if (c == '\n')
                {
                    row.Add(MakeField(cur.ToString(), quotedField, values)); cur.Length = 0; quotedField = false;
                    _grid.Add(row); row = new List<Field>(); pos++; continue;
                }
                cur.Append(c); pos++;
            }
            if (cur.Length > 0 || quotedField || row.Count > 0)
            {
                row.Add(MakeField(cur.ToString(), quotedField, values));
                _grid.Add(row);
            }
            return values;
        }

        Field MakeField(string text, bool quoted, List<LocValue> values)
        {
            var f = new Field();
            f.Text = text; f.Quoted = quoted; f.Val = null;
            if (HeshamRTLBaker.HasArabic(text) || HeshamRTLBaker.IsBaked(text))
            {
                f.Val = new LocValue("cell", text);
                values.Add(f.Val);
            }
            return f;
        }

        static char DetectDelimiter(string raw)
        {
            int n = raw.Length; bool inQ = false; int comma = 0, semi = 0, tab = 0;
            for (int i = 0; i < n; i++)
            {
                char c = raw[i];
                if (c == '"') { inQ = !inQ; continue; }
                if (inQ) continue;
                if (c == '\n') break;                                 // first physical line only
                if (c == ',') comma++; else if (c == ';') semi++; else if (c == '\t') tab++;
            }
            if (semi > comma && semi >= tab) return ';';
            if (tab > comma && tab > semi) return '\t';
            return ',';
        }

        public override string Serialize()
        {
            var sb = new StringBuilder();
            for (int r = 0; r < _grid.Count; r++)
            {
                List<Field> row = _grid[r];
                for (int f = 0; f < row.Count; f++)
                {
                    if (f > 0) sb.Append(_delim);
                    string text = row[f].Val != null ? row[f].Val.Baked : row[f].Text;
                    sb.Append(QuoteIfNeeded(text, row[f].Quoted));
                }
                if (r < _grid.Count - 1) sb.Append(_newline);
            }
            if (_hadFinalNewline) sb.Append(_newline);              // N1: keep the file's final newline
            return sb.ToString();
        }

        string QuoteIfNeeded(string s, bool wasQuoted)
        {
            bool need = wasQuoted || s.IndexOf(_delim) >= 0 || s.IndexOf('"') >= 0 ||
                        s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
            if (!need) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }

    // ===================== JSON (self-contained, nested-aware) ==================
    public sealed class JsonAdapter : LocAdapter
    {
        public override string Id { get { return "JSON"; } }

        abstract class JNode { public abstract void Write(StringBuilder sb, int level); }

        sealed class JObj : JNode
        {
            public readonly List<KeyValuePair<string, JNode>> Items = new List<KeyValuePair<string, JNode>>();
            public override void Write(StringBuilder sb, int level)
            {
                if (Items.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                for (int i = 0; i < Items.Count; i++)
                {
                    Indent(sb, level + 1);
                    sb.Append('"').Append(Esc(Items[i].Key)).Append("\": ");
                    Items[i].Value.Write(sb, level + 1);
                    if (i < Items.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, level); sb.Append('}');
            }
        }

        sealed class JArr : JNode
        {
            public readonly List<JNode> Items = new List<JNode>();
            public override void Write(StringBuilder sb, int level)
            {
                if (Items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < Items.Count; i++)
                {
                    Indent(sb, level + 1);
                    Items[i].Write(sb, level + 1);
                    if (i < Items.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, level); sb.Append(']');
            }
        }

        sealed class JStr : JNode
        {
            public LocValue Val;     // when Bakeable
            public string Raw;       // when not
            public bool Bakeable;
            public override void Write(StringBuilder sb, int level)
            {
                string s = Bakeable ? Val.Baked : Raw;
                sb.Append('"').Append(Esc(s)).Append('"');
            }
        }

        sealed class JRaw : JNode   // number / true / false / null — kept verbatim
        {
            public string Token;
            public override void Write(StringBuilder sb, int level) { sb.Append(Token); }
        }

        JNode _root;
        string _s;
        int _i;

        public override List<LocValue> Parse(string raw)
        {
            ParseWarnings.Clear();
            var values = new List<LocValue>();
            _s = raw; _i = 0;
            if (_i < _s.Length && _s[_i] == '\uFEFF') _i++;   // BOM
            SkipWs();
            if (_i >= _s.Length) { _root = null; return values; }
            _root = ParseValue(values);
            return values;
        }

        public override string Serialize()
        {
            if (_root == null) return "";
            var sb = new StringBuilder();
            _root.Write(sb, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        JNode ParseValue(List<LocValue> values)
        {
            SkipWs();
            if (_i >= _s.Length) throw new Exception("unexpected end of input");
            char c = _s[_i];
            if (c == '{') return ParseObject(values);
            if (c == '[') return ParseArray(values);
            if (c == '"')
            {
                string str = ParseString();
                var node = new JStr();
                if (HeshamRTLBaker.HasArabic(str) || HeshamRTLBaker.IsBaked(str))
                {
                    node.Bakeable = true;
                    node.Val = new LocValue("json", str);
                    values.Add(node.Val);
                }
                else { node.Bakeable = false; node.Raw = str; }
                return node;
            }
            return ParseRaw();
        }

        JObj ParseObject(List<LocValue> values)
        {
            _i++; // '{'
            var o = new JObj();
            SkipWs();
            if (_i < _s.Length && _s[_i] == '}') { _i++; return o; }
            while (true)
            {
                SkipWs();
                if (_i >= _s.Length || _s[_i] != '"') throw new Exception("expected object key (string) at " + _i);
                string key = ParseString();
                SkipWs();
                if (_i >= _s.Length || _s[_i] != ':') throw new Exception("expected ':' after key at " + _i);
                _i++;
                JNode val = ParseValue(values);
                o.Items.Add(new KeyValuePair<string, JNode>(key, val));
                SkipWs();
                if (_i >= _s.Length) throw new Exception("unterminated object");
                char d = _s[_i++];
                if (d == '}') break;
                if (d != ',') throw new Exception("expected ',' or '}' at " + (_i - 1));
            }
            return o;
        }

        JArr ParseArray(List<LocValue> values)
        {
            _i++; // '['
            var a = new JArr();
            SkipWs();
            if (_i < _s.Length && _s[_i] == ']') { _i++; return a; }
            while (true)
            {
                JNode val = ParseValue(values);
                a.Items.Add(val);
                SkipWs();
                if (_i >= _s.Length) throw new Exception("unterminated array");
                char d = _s[_i++];
                if (d == ']') break;
                if (d != ',') throw new Exception("expected ',' or ']' at " + (_i - 1));
            }
            return a;
        }

        string ParseString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (_i >= _s.Length) break;
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 <= _s.Length)
                            {
                                int cp = int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                _i += 4; sb.Append((char)cp);
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new Exception("unterminated string");
        }

        JRaw ParseRaw()
        {
            int start = _i;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c)) break;
                _i++;
            }
            string tok = _s.Substring(start, _i - start);
            if (tok.Length == 0) throw new Exception("invalid token at " + start);
            var r = new JRaw(); r.Token = tok; return r;
        }

        void SkipWs() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        static void Indent(StringBuilder sb, int level) { for (int i = 0; i < level; i++) sb.Append("  "); }

        static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
