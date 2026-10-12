// A small YAML reader for the subset that DAMF files use: block mappings ("key: value"),
// block sequences ("- item"), flow sequences ("[1, 2, 3]") and plain scalars.
// Anchors, multi-line strings, quoting rules and the rest of YAML are not supported - the
// files truehdd and Dolby tools write don't use them.
//
// Result: nested Dictionary<string, object> / List<object> / string, like JSONSerialization
// gives you [String: Any] in Swift.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AtmosEngine
{
    public static class MiniYaml
    {
        struct Line
        {
            public int Indent;
            public string Text;
            public int Number;
        }

        public static object Parse(string text)
        {
            var lines = new List<Line>();
            int number = 0;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                number++;
                string t = StripComment(raw).TrimEnd();
                if (t.Trim().Length == 0) continue;
                int indent = 0;
                while (indent < t.Length && t[indent] == ' ') indent++;
                if (indent < t.Length && t[indent] == '\t')
                    throw new InvalidDataException("tab indentation at line " + number);
                lines.Add(new Line { Indent = indent, Text = t.Substring(indent), Number = number });
            }
            if (lines.Count == 0) return null;
            int pos = 0;
            var result = ParseBlock(lines, ref pos, lines[0].Indent);
            if (pos != lines.Count)
                throw new InvalidDataException("unexpected indentation at line " + lines[pos].Number);
            return result;
        }

        static object ParseBlock(List<Line> lines, ref int pos, int indent)
        {
            return lines[pos].Text.StartsWith("- ") || lines[pos].Text == "-"
                ? ParseSequence(lines, ref pos, indent)
                : (object)ParseMapping(lines, ref pos, indent);
        }

        static List<object> ParseSequence(List<Line> lines, ref int pos, int indent)
        {
            var list = new List<object>();
            while (pos < lines.Count && lines[pos].Indent == indent && (lines[pos].Text.StartsWith("- ") || lines[pos].Text == "-"))
            {
                var line = lines[pos];
                string rest = line.Text.Length > 1 ? line.Text.Substring(2).TrimStart() : "";
                int restIndent = indent + (line.Text.Length - line.Text.Substring(1).TrimStart().Length);
                if (rest.Length == 0)
                {
                    pos++;
                    list.Add(pos < lines.Count && lines[pos].Indent > indent ? ParseBlock(lines, ref pos, lines[pos].Indent) : null);
                }
                else if (IsMappingEntry(rest))
                {
                    // "- key: value" starts a mapping whose further keys sit at restIndent.
                    lines[pos] = new Line { Indent = restIndent, Text = rest, Number = line.Number };
                    list.Add(ParseMapping(lines, ref pos, restIndent));
                }
                else
                {
                    list.Add(ParseScalarOrFlow(rest));
                    pos++;
                }
            }
            return list;
        }

        static Dictionary<string, object> ParseMapping(List<Line> lines, ref int pos, int indent)
        {
            var map = new Dictionary<string, object>();
            while (pos < lines.Count && lines[pos].Indent == indent && !lines[pos].Text.StartsWith("- "))
            {
                var line = lines[pos];
                int colon = FindMappingColon(line.Text);
                if (colon < 0) throw new InvalidDataException("expected 'key: value' at line " + line.Number);
                string key = line.Text.Substring(0, colon).Trim();
                string value = line.Text.Substring(colon + 1).Trim();
                pos++;
                if (value.Length > 0)
                {
                    map[key] = ParseScalarOrFlow(value);
                }
                else if (pos < lines.Count && (lines[pos].Indent > indent ||
                         (lines[pos].Indent == indent && lines[pos].Text.StartsWith("- "))))
                {
                    map[key] = ParseBlock(lines, ref pos, lines[pos].Indent);
                }
                else
                {
                    map[key] = null;
                }
            }
            return map;
        }

        static bool IsMappingEntry(string text) => FindMappingColon(text) > 0;

        static int FindMappingColon(string text)
        {
            if (text.StartsWith("[")) return -1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == ':' && (i + 1 == text.Length || text[i + 1] == ' ')) return i;
            return -1;
        }

        static object ParseScalarOrFlow(string value)
        {
            if (value.StartsWith("["))
            {
                if (!value.EndsWith("]")) throw new InvalidDataException("unterminated flow sequence: " + value);
                var list = new List<object>();
                string inner = value.Substring(1, value.Length - 2).Trim();
                if (inner.Length > 0)
                    foreach (var part in inner.Split(','))
                        list.Add(Unquote(part.Trim()));
                return list;
            }
            return Unquote(value);
        }

        static string Unquote(string s)
        {
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        static string StripComment(string s)
        {
            int i = s.IndexOf(" #", StringComparison.Ordinal);
            if (s.StartsWith("#")) return "";
            return i >= 0 ? s.Substring(0, i) : s;
        }

        // ---- typed accessors ----------------------------------------------------------------

        public static double ToDouble(object o)
        {
            var s = (o as string ?? throw new InvalidDataException("expected a number, got " + Describe(o))).Trim();
            switch (s)
            {
                case "-inf": case "-.inf": case "-Infinity": return double.NegativeInfinity;
                case "inf": case ".inf": case "+inf": case "Infinity": return double.PositiveInfinity;
            }
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public static long ToLong(object o) =>
            long.Parse((o as string ?? throw new InvalidDataException("expected an integer, got " + Describe(o))).Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture);

        public static bool ToBool(object o)
        {
            var s = (o as string ?? "").Trim().ToLowerInvariant();
            if (s == "true") return true;
            if (s == "false") return false;
            throw new InvalidDataException("expected true/false, got " + Describe(o));
        }

        static string Describe(object o) => o == null ? "null" : o.GetType().Name + " '" + o + "'";
    }
}
