#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EasyMultiNet.Protocol
{
    // ─────────────────────────────────────────────────────────────────────────
    // A minimal JSON reader/writer, hand-written on purpose.
    //
    // The SDK has to stay a plain source drop: clone the repo, copy the .cs files into
    // your Unity/Godot project, done. Any JSON package (System.Text.Json, Newtonsoft)
    // would put a NuGet/UPM step and an IL2CPP stripping config between the developer
    // and "it just works", so the protocol carries its own codec instead.
    //
    // Scope is exactly the relay's control messages: flat objects whose values are
    // strings, ints, bools, string arrays, or arrays of flat objects. Nothing else is
    // supported because nothing else is sent.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Builds one JSON object. Members whose value is null are skipped, matching "absence = default".</summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(128);
        private bool _first = true;

        public JsonWriter Begin()
        {
            _sb.Append('{');
            _first = true;
            return this;
        }

        public string End()
        {
            _sb.Append('}');
            return _sb.ToString();
        }

        public JsonWriter Str(string name, string? value)
        {
            if (value == null) return this;
            Key(name);
            WriteEscaped(_sb, value);
            return this;
        }

        public JsonWriter Num(string name, int? value)
        {
            if (value == null) return this;
            Key(name);
            _sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Bool(string name, bool? value)
        {
            if (value == null) return this;
            Key(name);
            _sb.Append(value.Value ? "true" : "false");
            return this;
        }

        public JsonWriter StrArray(string name, string[]? values)
        {
            if (values == null) return this;
            Key(name);
            _sb.Append('[');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) _sb.Append(',');
                WriteEscaped(_sb, values[i] ?? "");
            }

            _sb.Append(']');
            return this;
        }

        /// <summary>Write an array of already-serialized objects (each element is a complete JSON object).</summary>
        public JsonWriter ObjArray<T>(string name, T[]? items, Func<T, string> writeItem)
        {
            if (items == null) return this;
            Key(name);
            _sb.Append('[');
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) _sb.Append(',');
                _sb.Append(writeItem(items[i]));
            }

            _sb.Append(']');
            return this;
        }

        private void Key(string name)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            WriteEscaped(_sb, name);
            _sb.Append(':');
        }

        private static void WriteEscaped(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Only C0 controls must be escaped. Everything else — including
                        // CJK and emoji — is emitted as-is and carried by UTF-8.
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }
    }

    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>One parsed JSON value, plus typed accessors for reading a flat message object.</summary>
    internal sealed class JsonValue
    {
        /// <summary>Nesting cap. The protocol never nests past object → array → object.</summary>
        private const int MaxDepth = 8;

        private static readonly JsonValue NullValue = new JsonValue { Kind = JsonKind.Null };

        public JsonKind Kind;
        public string Text = "";
        public double Number;
        public bool Boolean;
        public Dictionary<string, JsonValue>? Members;
        public List<JsonValue>? Items;

        public bool IsObject => Kind == JsonKind.Object;

        /// <summary>Parse a complete JSON document. Returns false for anything malformed; never throws.</summary>
        public static bool TryParse(string json, out JsonValue value)
        {
            value = NullValue;
            if (json == null) return false;

            int i = 0;
            if (!TryParseValue(json, ref i, 0, out JsonValue parsed)) return false;

            SkipWhitespace(json, ref i);
            if (i != json.Length) return false; // trailing garbage

            value = parsed;
            return true;
        }

        // ── Typed accessors (missing / wrong-typed members fall back to defaults) ──

        public string Str(string name) => Member(name).Kind == JsonKind.String ? Member(name).Text : "";

        public string? OptStr(string name)
        {
            JsonValue v = Member(name);
            return v.Kind == JsonKind.String ? v.Text : null;
        }

        public int? OptInt(string name)
        {
            JsonValue v = Member(name);
            return v.Kind == JsonKind.Number ? (int)v.Number : (int?)null;
        }

        public bool? OptBool(string name)
        {
            JsonValue v = Member(name);
            return v.Kind == JsonKind.Bool ? v.Boolean : (bool?)null;
        }

        public int Int(string name) => OptInt(name) ?? 0;

        public bool Bool(string name) => OptBool(name) ?? false;

        public string[] StrArray(string name)
        {
            JsonValue v = Member(name);
            if (v.Kind != JsonKind.Array || v.Items == null) return Array.Empty<string>();

            var result = new string[v.Items.Count];
            for (int i = 0; i < v.Items.Count; i++)
            {
                result[i] = v.Items[i].Kind == JsonKind.String ? v.Items[i].Text : "";
            }

            return result;
        }

        public T[] ObjArray<T>(string name, Func<JsonValue, T> readItem)
        {
            JsonValue v = Member(name);
            if (v.Kind != JsonKind.Array || v.Items == null) return Array.Empty<T>();

            var result = new List<T>(v.Items.Count);
            foreach (JsonValue item in v.Items)
            {
                if (item.IsObject) result.Add(readItem(item));
            }

            return result.ToArray();
        }

        private JsonValue Member(string name)
        {
            if (Members != null && Members.TryGetValue(name, out JsonValue? v)) return v;
            return NullValue;
        }

        // ── Parser ────────────────────────────────────────────────────────────

        private static bool TryParseValue(string s, ref int i, int depth, out JsonValue value)
        {
            value = NullValue;
            if (depth > MaxDepth) return false;
            SkipWhitespace(s, ref i);
            if (i >= s.Length) return false;

            switch (s[i])
            {
                case '{': return TryParseObject(s, ref i, depth, out value);
                case '[': return TryParseArray(s, ref i, depth, out value);
                case '"':
                    if (!TryParseString(s, ref i, out string text)) return false;
                    value = new JsonValue { Kind = JsonKind.String, Text = text };
                    return true;
                case 't':
                    if (!Literal(s, ref i, "true")) return false;
                    value = new JsonValue { Kind = JsonKind.Bool, Boolean = true };
                    return true;
                case 'f':
                    if (!Literal(s, ref i, "false")) return false;
                    value = new JsonValue { Kind = JsonKind.Bool, Boolean = false };
                    return true;
                case 'n':
                    if (!Literal(s, ref i, "null")) return false;
                    value = NullValue;
                    return true;
                default:
                    return TryParseNumber(s, ref i, out value);
            }
        }

        private static bool TryParseObject(string s, ref int i, int depth, out JsonValue value)
        {
            value = NullValue;
            var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                value = new JsonValue { Kind = JsonKind.Object, Members = members };
                return true;
            }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') return false;
                if (!TryParseString(s, ref i, out string name)) return false;

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') return false;
                i++;

                if (!TryParseValue(s, ref i, depth + 1, out JsonValue member)) return false;
                members[name] = member;

                SkipWhitespace(s, ref i);
                if (i >= s.Length) return false;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}')
                {
                    i++;
                    value = new JsonValue { Kind = JsonKind.Object, Members = members };
                    return true;
                }

                return false;
            }
        }

        private static bool TryParseArray(string s, ref int i, int depth, out JsonValue value)
        {
            value = NullValue;
            var items = new List<JsonValue>();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                value = new JsonValue { Kind = JsonKind.Array, Items = items };
                return true;
            }

            while (true)
            {
                if (!TryParseValue(s, ref i, depth + 1, out JsonValue item)) return false;
                items.Add(item);

                SkipWhitespace(s, ref i);
                if (i >= s.Length) return false;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']')
                {
                    i++;
                    value = new JsonValue { Kind = JsonKind.Array, Items = items };
                    return true;
                }

                return false;
            }
        }

        private static bool TryParseString(string s, ref int i, out string result)
        {
            result = "";
            var sb = new StringBuilder();
            i++; // opening quote

            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                {
                    result = sb.ToString();
                    return true;
                }

                if (c != '\\')
                {
                    if (c < 0x20) return false; // raw control char is invalid in a JSON string
                    sb.Append(c);
                    continue;
                }

                if (i >= s.Length) return false;
                char esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) return false;
                        if (!ushort.TryParse(
                                s.Substring(i, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out ushort code))
                        {
                            return false;
                        }

                        // Surrogate pairs arrive as two \u escapes; appending both halves
                        // in order reconstructs the astral char (emoji etc.) correctly.
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: return false;
                }
            }

            return false; // unterminated
        }

        private static bool TryParseNumber(string s, ref int i, out JsonValue value)
        {
            value = NullValue;
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E'
                                    || s[i] == '-' || s[i] == '+'))
            {
                i++;
            }

            if (i == start) return false;
            if (!double.TryParse(
                    s.Substring(start, i - start),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number))
            {
                return false;
            }

            value = new JsonValue { Kind = JsonKind.Number, Number = number };
            return true;
        }

        private static bool Literal(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length) return false;
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'))
            {
                i++;
            }
        }
    }
}
