using System;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>
    /// 记忆审计持久化接缝。Write 幂等；失败时抛异常，由 MemoryAuditService 重试并包成
    /// MemoryAuditWriteException。Mongo 的 DeleteExpired 返回 0（TTL 接管）。
    /// </summary>
    public interface IMemoryAuditStore
    {
        void Write(MemoryAuditEntry entry);
        JObject Query(string gameId, string npcId, string playerId, string action, string date, int limit, int offset);
        int DeleteExpired(DateTime cutoffUtc);
    }
}
