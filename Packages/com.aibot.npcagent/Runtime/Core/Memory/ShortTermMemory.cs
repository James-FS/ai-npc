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
        private readonly object _sync = new object();
        private int _max;

        public ShortTermMemory(int maxTurns) { _max = maxTurns < 2 ? 2 : maxTurns; }

        public IReadOnlyList<LlmMessage> Messages
        {
            get { lock (_sync) return new List<LlmMessage>(_messages); }
        }

        public int Capacity { get { lock (_sync) return _max; } }

        /// <summary>待摘要的淘汰消息数。</summary>
        public int EvictedCount { get { lock (_sync) return _evicted.Count; } }

        public void Add(LlmMessage message)
        {
            lock (_sync)
            {
                _messages.Add(message);
                TrimToCapacity();
            }
        }

        /// <summary>运行期策略变更时调整窗口；缩小时超出的旧消息进入待摘要队列。</summary>
        public void Resize(int maxTurns)
        {
            lock (_sync)
            {
                _max = maxTurns < 2 ? 2 : maxTurns;
                TrimToCapacity();
            }
        }

        /// <summary>取出全部淘汰消息（交给摘要器）。</summary>
        public List<LlmMessage> TakeEvicted()
        {
            lock (_sync)
            {
                var list = new List<LlmMessage>(_evicted);
                _evicted.Clear();
                return list;
            }
        }

        /// <summary>获取待摘要消息快照，用于会话持久化；不会清空队列。</summary>
        public List<LlmMessage> SnapshotEvicted()
        {
            lock (_sync) return new List<LlmMessage>(_evicted);
        }

        /// <summary>恢复待摘要消息。摘要失败时也用它把刚取出的批次放回，避免静默丢记忆。</summary>
        public void RestoreEvicted(IEnumerable<LlmMessage> messages)
        {
            if (messages == null) return;
            lock (_sync)
            {
                foreach (LlmMessage message in messages)
                {
                    if (message != null) _evicted.Enqueue(message);
                }
            }
        }

        /// <summary>后台摘要成功后确认消费队首批次；新产生的淘汰消息仍保留在队尾。</summary>
        public void RemoveEvictedPrefix(int count)
        {
            lock (_sync)
            {
                while (count > 0 && _evicted.Count > 0)
                {
                    _evicted.Dequeue();
                    count--;
                }
            }
        }

        /// <summary>仅保留最近的待摘要消息；用于自动摘要关闭时限制持久化队列大小。</summary>
        public void TrimEvictedToLast(int maxCount)
        {
            lock (_sync)
            {
                int keep = maxCount < 0 ? 0 : maxCount;
                while (_evicted.Count > keep) _evicted.Dequeue();
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _messages.Clear();
                _evicted.Clear();
            }
        }

        private void TrimToCapacity()
        {
            while (_messages.Count > _max)
            {
                _evicted.Enqueue(_messages[0]);
                _messages.RemoveAt(0);
            }
        }
    }
}
