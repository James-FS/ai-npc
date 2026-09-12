using System;

namespace AIBot.Server
{
    /// <summary>摘要任务持久化记录（Mongo 与队列恢复共用）。</summary>
    public sealed class MemorySummaryJobRecord
    {
        public string JobKey { get; set; }
        public string GameId { get; set; }
        public string NpcId { get; set; }
        public string PlayerId { get; set; }
        public string SessionId { get; set; }
        public bool Force { get; set; }
        public string Actor { get; set; }
        public long Generation { get; set; }
        public string Status { get; set; }
        public int Attempts { get; set; }
        public string LastError { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
