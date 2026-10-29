using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hibira
{
    /// <summary>
    /// A small JSON reader and writer, so the package needs no other packages.
    /// </summary>
    /// <remarks>
    /// <see cref="Parse"/> returns <c>Dictionary&lt;string, object&gt;</c> for objects,
    /// <c>List&lt;object&gt;</c> for arrays, <c>long</c> for whole numbers that fit,
    /// <c>double</c> for other numbers, and <c>string</c>, <c>bool</c> or <c>null</c>.
    /// To read a payload into your own <c>[Serializable]</c> class instead, use
    /// <see cref="HibiraEvent.DataAs{T}"/>.
    /// </remarks>
    public static class Json
    {
        /// <summary>Parse JSON text. Throws <see cref="FormatException"/> on bad input.</summary>
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var reader = new Reader(text);
            reader.SkipWhitespace();
            var value = reader.ReadValue(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd) throw reader.Error("unexpected text after the value");
            return value;
        }

        /// <summary>Parse JSON text, or return false when it is not JSON.</summary>
        public static bool TryParse(string text, out object value)
        {
            value = null;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                value = Parse(text);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// Write a value as JSON. Handles null, strings, numbers, bools,
        /// dictionaries and lists (nested freely). Any other object is written
        /// with Unity's <c>JsonUtility</c>, so <c>[Serializable]</c> classes work.
        /// </summary>
        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        const int MaxDepth = 64;

        static void Write(StringBuilder sb, object value, int depth)
        {
            if (depth > MaxDepth) throw new ArgumentException("Json: value is nested too deeply");
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case char c:
                    WriteString(sb, c.ToString());
                    return;
                case Enum e:
                    WriteString(sb, e.ToString());
                    return;
                case int _:
                case long _:
                case short _:
                case byte _:
                case sbyte _:
                case uint _:
                case ulong _:
                case ushort _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case float f:
                    WriteNumber(sb, f);
                    return;
                case double d:
                    WriteNumber(sb, d);
                    return;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case IDictionary dict:
                    sb.Append('{');
                    var first = true;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        Write(sb, entry.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
            }
#if UNITY_5_3_OR_NEWER
            sb.Append(UnityEngine.JsonUtility.ToJson(value));
#else
            // Outside Unity (the package's own tests): public fields, as
            // JsonUtility would write them.
            sb.Append('{');
            var firstField = true;
            foreach (var field in value.GetType().GetFields())
            {
                if (field.IsStatic) continue;
                if (!firstField) sb.Append(',');
                firstField = false;
                WriteString(sb, field.Name);
                sb.Append(':');
                Write(sb, field.GetValue(value), depth + 1);
            }
            sb.Append('}');
#endif
        }

        static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                sb.Append("null");
                return;
            }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Reader
        {
            readonly string _s;
            int _i;

            public Reader(string s) => _s = s;

            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string message) =>
                new FormatException($"Json: {message} at position {_i}");

            public void SkipWhitespace()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
            }

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Error("value is nested too deeply");
                if (AtEnd) throw Error("unexpected end");
                var c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                }
                if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                throw Error($"unexpected '{c}'");
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error($"expected {word}");
                _i += word.Length;
            }

            Dictionary<string, object> ReadObject(int depth)
            {
                var obj = new Dictionary<string, object>();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == '}')
                {
                    _i++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != '"') throw Error("expected a key");
                    var key = ReadString();
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != ':') throw Error("expected ':'");
                    _i++;
                    SkipWhitespace();
                    obj[key] = ReadValue(depth + 1);
                    SkipWhitespace();
                    if (AtEnd) throw Error("unexpected end");
                    if (_s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (_s[_i] == '}')
                    {
                        _i++;
                        return obj;
                    }
                    throw Error("expected ',' or '}'");
                }
            }

            List<object> ReadArray(int depth)
            {
                var list = new List<object>();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == ']')
                {
                    _i++;
                    return list;
                }
                while (true)
                {
                    SkipWhitespace();
                    list.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd) throw Error("unexpected end");
                    if (_s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (_s[_i] == ']')
                    {
                        _i++;
                        return list;
                    }
                    throw Error("expected ',' or ']'");
                }
            }

            string ReadString()
            {
                _i++; // opening quote
                StringBuilder sb = null;
                var start = _i;
                while (true)
                {
                    if (AtEnd) throw Error("unterminated string");
                    var c = _s[_i];
                    if (c == '"')
                    {
                        string result;
                        if (sb == null)
                        {
                            result = _s.Substring(start, _i - start);
                        }
                        else
                        {
                            sb.Append(_s, start, _i - start);
                            result = sb.ToString();
                        }
                        _i++;
                        return result;
                    }
                    if (c < 0x20) throw Error("control character in string");
                    if (c != '\\')
                    {
                        _i++;
                        continue;
                    }
                    sb ??= new StringBuilder();
                    sb.Append(_s, start, _i - start);
                    _i++;
                    if (AtEnd) throw Error("unterminated string");
                    var e = _s[_i++];
                    switch (e)
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
                            if (_i + 4 > _s.Length) throw Error("bad \\u escape");
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw Error("bad \\u escape");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw Error($"bad escape \\{e}");
                    }
                    start = _i;
                }
            }

            object ReadNumber()
            {
                var start = _i;
                if (_s[_i] == '-') _i++;
                var digits = _i;
                while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                if (_i == digits) throw Error("bad number");
                var whole = true;
                if (!AtEnd && _s[_i] == '.')
                {
                    whole = false;
                    _i++;
                    var frac = _i;
                    while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                    if (_i == frac) throw Error("bad number");
                }
                if (!AtEnd && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    whole = false;
                    _i++;
                    if (!AtEnd && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    var exp = _i;
                    while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                    if (_i == exp) throw Error("bad number");
                }
                var text = _s.Substring(start, _i - start);
                if (whole && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
                return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        // ---- helpers for reading parsed values --------------------------------

        internal static Dictionary<string, object> AsObject(object value) =>
            value as Dictionary<string, object> ?? new Dictionary<string, object>();

        internal static object Get(Dictionary<string, object> obj, string key) =>
            obj != null && obj.TryGetValue(key, out var v) ? v : null;

        internal static bool IsNumber(object value) =>
            value is long || value is double || value is int || value is float;

        internal static double ToDouble(object value, double fallback = 0) =>
            IsNumber(value) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : fallback;

        /// <summary>Strings as they are; anything else as JSON (or "" for null).</summary>
        internal static string ToText(object value) =>
            value == null ? "" : value as string ?? Serialize(value);
    }
}
