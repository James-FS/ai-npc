using System.Collections.Generic;
using AIBot.Core.Llm;

namespace AIBot.Core.Memory
{
    /// <summary>
    /// 短期对话窗口：保留最近 N 条消息，被挤出的进入淘汰队列；
    /// 淘汰达到阈值后由 AgentLoop 触发 MemorySummarizer 压缩为长期记忆。
    /// </summary>
    public sealed class ShortTermMemory
    {
        private readonly List<LlmMessage> _messages = new List<LlmMessage>();
        private readonly Queue<LlmMessage> _evicted = new Queue<LlmMessage>();
        private readonly int _max;

        public ShortTermMemory(int maxTurns) { _max = maxTurns < 2 ? 2 : maxTurns; }

        public IReadOnlyList<LlmMessage> Messages { get { return _messages; } }

        /// <summary>待摘要的淘汰消息数。</summary>
        public int EvictedCount { get { return _evicted.Count; } }

        public void Add(LlmMessage message)
        {
            _messages.Add(message);
            while (_messages.Count > _max)
            {
                _evicted.Enqueue(_messages[0]);
                _messages.RemoveAt(0);
            }
        }

        /// <summary>取出全部淘汰消息（交给摘要器）。</summary>
        public List<LlmMessage> TakeEvicted()
        {
            var list = new List<LlmMessage>(_evicted);
            _evicted.Clear();
            return list;
        }

        public void Clear()
        {
            _messages.Clear();
            _evicted.Clear();
        }
    }
}
