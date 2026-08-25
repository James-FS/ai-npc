using System;

namespace AIBot.Core.Llm
{
    /// <summary>
    /// SSE 行缓冲解析器（传输层无关，Unity/Server 共用）。
    /// 输入任意切割的文本片段（半行、多行、多事件粘包），输出完整的 data 载荷行。
    /// </summary>
    public sealed class SseLineParser
    {
        private readonly Action<string> _onData;
        private string _buffer = string.Empty;

        public SseLineParser(Action<string> onData)
        {
            _onData = onData ?? throw new ArgumentNullException(nameof(onData));
        }

        /// <summary>喂入任意大小的文本片段（可能含半行）。</summary>
        public void Feed(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            _buffer += chunk;
            int newline;
            while ((newline = _buffer.IndexOf('\n')) >= 0)
            {
                string line = _buffer.Substring(0, newline).TrimEnd('\r');
                _buffer = _buffer.Substring(newline + 1);
                HandleLine(line);
            }
        }

        /// <summary>流结束时调用，处理无换行结尾的最后一行。</summary>
        public void Flush()
        {
            if (_buffer.Length > 0)
            {
                HandleLine(_buffer.TrimEnd('\r'));
                _buffer = string.Empty;
            }
        }

        private void HandleLine(string line)
        {
            if (line.Length == 0) return;               // 事件分隔空行
            if (line[0] == ':') return;                 // SSE 注释/心跳
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string payload = line.Substring(5).TrimStart(' ').TrimEnd();
                if (payload.Length > 0) _onData(payload);
            }
        }
    }
}
