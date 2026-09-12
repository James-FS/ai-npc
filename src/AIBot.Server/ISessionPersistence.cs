using System;
using System.Collections.Generic;

namespace AIBot.Server
{
    /// <summary>
    /// Session 持久化接缝。Load/List 只返回 DTO；JSON 专有的 legacy 路径回退、文件时间兜底
    /// 与 v1 归档都在实现内消化。DeleteExpired 由门面传入活跃键与 legacy 保护路径。
    /// </summary>
    public interface ISessionPersistence
    {
        SessionStore.SessionFileDto Load(string gameId, string npcId, string playerId, string sessionId);
        SessionSaveResult Save(string gameId, SessionStore.SessionFileDto dto);
        List<SessionStore.SessionFileDto> List(string gameId, string npcId, string playerId);
        List<PendingMemorySession> ScanPending();
        bool Delete(string gameId, string npcId, string playerId, string sessionId);
        int DeleteExpired(TimeSpan idle, ISet<string> activeKeys, ISet<string> protectedPaths);
    }

    /// <summary>会话保存结果：写失败返回 Persisted=false（门面不吞异常）；JSON 归档成功置 LegacyArchived。</summary>
    public sealed class SessionSaveResult
    {
        public bool Persisted;
        public bool LegacyArchived;
    }
}
