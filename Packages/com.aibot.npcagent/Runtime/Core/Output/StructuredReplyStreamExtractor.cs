using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AIBot.Core.Output
{
    /// <summary>
    /// 从模型流式输出的结构化 JSON 中增量提取 say 字段。
    /// 下游 UI 只看到 NPC 台词，不会短暂显示 {"say":...} 原始 JSON。
    /// </summary>
    public sealed class StructuredReplyStreamExtractor
    {
        private readonly StringBuilder _raw = new StringBuilder();
        private string _emitted = string.Empty;

        public string Push(string chunk)
        {
            if (!string.IsNullOrEmpty(chunk)) _raw.Append(chunk);
            string current = ExtractPrefix(_raw.ToString());
            if (current.Length <= _emitted.Length || !current.StartsWith(_emitted, StringComparison.Ordinal))
                return string.Empty;
            string delta = current.Substring(_emitted.Length);
            _emitted = current;
            return delta;
        }

        private static string ExtractPrefix(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            Match match = Regex.Match(raw, "\\\"say\\\"\\s*:\\s*\\\"");
            if (!match.Success) return string.Empty;

            var decoded = new StringBuilder();
            for (int i = match.Index + match.Length; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"') break;
                if (c != '\\')
                {
                    decoded.Append(c);
                    continue;
                }

                if (++i >= raw.Length) break; // 不完整转义，等待下一片
                char escaped = raw[i];
                switch (escaped)
                {
                    case '"': decoded.Append('"'); break;
                    case '\\': decoded.Append('\\'); break;
                    case '/': decoded.Append('/'); break;
                    case 'b': decoded.Append('\b'); break;
                    case 'f': decoded.Append('\f'); break;
                    case 'n': decoded.Append('\n'); break;
                    case 'r': decoded.Append('\r'); break;
                    case 't': decoded.Append('\t'); break;
                    case 'u':
                        if (i + 4 >= raw.Length) return decoded.ToString();
                        string hex = raw.Substring(i + 1, 4);
                        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out int code))
                            return decoded.ToString();
                        decoded.Append((char)code);
                        i += 4;
                        break;
                    default:
                        return decoded.ToString();
                }
            }
            return decoded.ToString();
        }
    }
}
