using System.Collections.Generic;

namespace AIBot.Server
{
    /// <summary>摘要任务的持久化接缝：仅 Mongo 模式有实现，Json 模式为 null（沿用现状）。</summary>
    public interface IMemorySummaryJobPersistence
    {
        void UpsertPending(MemorySummaryJob job, string key);
        List<MemorySummaryJobRecord> LoadRecoverable();
        List<MemorySummaryJobRecord> LoadFailed();
        void MarkProcessing(string key);
        void MarkSucceeded(string key);
        void MarkFailed(string key, string error);
        void MarkPending(string key);
        void DeleteForPlayer(string gameId, string npcId, string playerId);
    }
}
