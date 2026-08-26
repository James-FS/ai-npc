using System;
using System.Collections.Generic;

namespace AIBot.Core.Memory
{
    /// <summary>长期记忆的结构化事实契约；阶段 A 只定义契约，事实合并与新存储在阶段 B 接入。</summary>
    public sealed class MemoryFact
    {
        public string id;
        public string category;
        public string key;
        public string value;
        public float confidence;
        public string source;
        public string sourceSessionId;
        public DateTime createdUtc;
        public DateTime updatedUtc;
        public bool pinned;
        public DateTime? expiresUtc;
    }

    public sealed class PlayerLongTermMemory
    {
        public int schemaVersion = 2;
        public int memoryVersion;
        public string gameId;
        public string npcId;
        public string playerId;
        public string summary;
        public List<MemoryFact> facts = new List<MemoryFact>();
        public DateTime? lastSummarizedUtc;
    }
}
