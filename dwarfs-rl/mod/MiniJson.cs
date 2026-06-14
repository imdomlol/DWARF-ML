using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DwarfsMod
{
    // just enough json for the bridge. commands coming in are small flat objects
    // like {"command":"STEP","action":2} and outgoing messages get built by hand
    // with a StringBuilder so theres no point dragging in a real json library
    public static class MiniJson
    {
        // parses one flat json object into a dictionary. values come out as
        // string, double, bool or null. commands dont need nested objects or
        // arrays so those just get skipped over if they ever show up
        public static Dictionary<string, object> Parse(string text)
        {
            var result = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(text)) return result;
            int i = 0;
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '{') return result;
            i++;
            while (true)
            {
                SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == '}') break;
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '"') break;
                string key = ReadString(text, ref i);
                SkipWs(text, ref i);
                if (i >= text.Length || text[i] != ':') break;
                i++;
                SkipWs(text, ref i);
                object value = ReadValue(text, ref i);
                result[key] = value;
            }
            return result;
        }

        static object ReadValue(string s, ref int i)
        {
            char c = s[i];
            if (c == '"') return ReadString(s, ref i);
            if (c == '{' || c == '[') { SkipBlock(s, ref i); return null; }
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }

            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                i++;
            double d;
            double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            return d;
        }

        static string ReadString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char e = s[i];
                    if (e == 'n') sb.Append('\n');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 'u' && i + 4 < s.Length)
                    {
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                        i += 4;
                    }
                    else sb.Append(e);
                }
                else sb.Append(s[i]);
                i++;
            }
            i++; // closing quote
            return sb.ToString();
        }

        static void SkipBlock(string s, ref int i)
        {
            char open = s[i], close = open == '{' ? '}' : ']';
            int depth = 0;
            bool inStr = false;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inStr = false;
                }
                else if (c == '"') inStr = true;
                else if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) { i++; return; }
                }
            }
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        // ---- writing helpers ----

        public static string GetString(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is string) return (string)v;
            return fallback;
        }

        public static int GetInt(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is double) return (int)(double)v;
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is bool) return (bool)v;
            return fallback;
        }

        public static float GetFloat(Dictionary<string, object> d, string key, float fallback)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is double) return (float)(double)v;
            return fallback;
        }

        public static void AppendIntArray(StringBuilder sb, int[] values)
        {
            sb.Append('[');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i]);
            }
            sb.Append(']');
        }
    }
}
