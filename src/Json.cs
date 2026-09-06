using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentReaper
{
    /// <summary>順序を保つ JSON オブジェクト。外部ライブラリを足さないための最小実装。</summary>
    internal sealed class JObj
    {
        public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();

        public JObj Set(string key, object value)
        {
            Items.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }
    }

    internal sealed class JArr
    {
        public readonly List<object> Items = new List<object>();

        public JArr Add(object value)
        {
            Items.Add(value);
            return this;
        }

        public int Count { get { return Items.Count; } }
    }

    /// <summary>
    /// JSON の書き出しと、同じ内容の人間向けテキスト化。
    /// 2 つの表現が食い違わないよう、テキストは JSON と同じ木から作る。
    /// </summary>
    internal static class Json
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder(16 * 1024);
            WriteValue(sb, value, 0);
            sb.AppendLine();
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }

            var obj = v as JObj;
            if (obj != null) { WriteObject(sb, obj, depth); return; }

            var arr = v as JArr;
            if (arr != null) { WriteArray(sb, arr, depth); return; }

            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }

            if (v is double || v is float)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }

            if (v is int || v is long || v is short || v is byte)
            {
                sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture));
                return;
            }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void WriteObject(StringBuilder sb, JObj obj, int depth)
        {
            if (obj.Items.Count == 0) { sb.Append("{}"); return; }

            sb.Append("{");
            for (int i = 0; i < obj.Items.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.AppendLine();
                Indent(sb, depth + 1);
                WriteString(sb, obj.Items[i].Key);
                sb.Append(": ");
                WriteValue(sb, obj.Items[i].Value, depth + 1);
            }
            sb.AppendLine();
            Indent(sb, depth);
            sb.Append("}");
        }

        private static void WriteArray(StringBuilder sb, JArr arr, int depth)
        {
            if (arr.Items.Count == 0) { sb.Append("[]"); return; }

            sb.Append("[");
            for (int i = 0; i < arr.Items.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.AppendLine();
                Indent(sb, depth + 1);
                WriteValue(sb, arr.Items[i], depth + 1);
            }
            sb.AppendLine();
            Indent(sb, depth);
            sb.Append("]");
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }

            sb.Append('"');
            foreach (char c in s)
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
                        if (c < 0x20 || c == 0x7f)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- 人間向けテキスト ----------------

        /// <summary>同じ木からテキストを作る。JSON とテキストの内容が食い違うことがない。</summary>
        public static string ToText(object value)
        {
            var sb = new StringBuilder(16 * 1024);
            TextValue(sb, null, value, 0);
            return sb.ToString();
        }

        private static void TextValue(StringBuilder sb, string label, object v, int depth)
        {
            var obj = v as JObj;
            if (obj != null)
            {
                if (label != null) { Indent(sb, depth); sb.AppendLine(label); }
                foreach (var kv in obj.Items) TextValue(sb, kv.Key, kv.Value, label == null ? depth : depth + 1);
                return;
            }

            var arr = v as JArr;
            if (arr != null)
            {
                Indent(sb, depth);
                if (arr.Items.Count == 0) { sb.AppendLine(label + ": (なし)"); return; }
                sb.AppendLine(label + ":");
                foreach (object item in arr.Items)
                {
                    if (item is JObj || item is JArr)
                    {
                        Indent(sb, depth + 1);
                        sb.AppendLine("-");
                        TextValue(sb, null, item, depth + 2);
                    }
                    else
                    {
                        Indent(sb, depth + 1);
                        sb.Append("- ").AppendLine(Scalar(item));
                    }
                }
                return;
            }

            Indent(sb, depth);
            sb.Append(label).Append(": ").AppendLine(Scalar(v));
        }

        private static string Scalar(object v)
        {
            if (v == null) return "(なし)";
            if (v is bool) return ((bool)v) ? "はい" : "いいえ";
            if (v is double || v is float)
                return Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture), 2)
                    .ToString("0.##", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
