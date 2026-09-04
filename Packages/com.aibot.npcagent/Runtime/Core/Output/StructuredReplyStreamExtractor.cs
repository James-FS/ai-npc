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
        private readonly StringBuilder _decoded = new StringBuilder();
        private int _sayValueStart = -1;
        private int _scanIndex;
        private bool _escape;
        private int _unicodeDigits;
        private int _unicodeValue;
        private bool _closed;
        private int _emitted;

        public string Push(string chunk)
        {
            if (!string.IsNullOrEmpty(chunk)) _raw.Append(chunk);
            if (_sayValueStart < 0)
            {
                Match match = Regex.Match(_raw.ToString(), "\\\"say\\\"\\s*:\\s*\\\"");
                if (!match.Success) return string.Empty;
                _sayValueStart = match.Index + match.Length;
                _scanIndex = _sayValueStart;
            }

            while (_scanIndex < _raw.Length && !_closed)
            {
                char c = _raw[_scanIndex++];
                if (_unicodeDigits > 0)
                {
                    int digit = Hex(c);
                    if (digit < 0)
                    {
                        _unicodeDigits = 0;
                        _escape = false;
                        continue;
                    }
                    _unicodeValue = (_unicodeValue << 4) | digit;
                    if (--_unicodeDigits == 0) _decoded.Append((char)_unicodeValue);
                    continue;
                }
                if (_escape)
                {
                    _escape = false;
                    switch (c)
                    {
                        case '"': _decoded.Append('"'); break;
                        case '\\': _decoded.Append('\\'); break;
                        case '/': _decoded.Append('/'); break;
                        case 'b': _decoded.Append('\b'); break;
                        case 'f': _decoded.Append('\f'); break;
                        case 'n': _decoded.Append('\n'); break;
                        case 'r': _decoded.Append('\r'); break;
                        case 't': _decoded.Append('\t'); break;
                        case 'u': _unicodeDigits = 4; _unicodeValue = 0; break;
                        default: break;
                    }
                    continue;
                }
                if (c == '\\') { _escape = true; continue; }
                if (c == '"') { _closed = true; break; }
                _decoded.Append(c);
            }

            if (_decoded.Length <= _emitted) return string.Empty;
            string delta = _decoded.ToString(_emitted, _decoded.Length - _emitted);
            _emitted = _decoded.Length;
            return delta;
        }

        private static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
