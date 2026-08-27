using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Newtonsoft.Json;

namespace AIBot.Server
{
    /// <summary>Session 的 MySQL 持久化实现；消息窗口、待摘要批次和模拟状态以 JSON 文档保存。</summary>
    public sealed class MySqlSessionPersistence
    {
        private readonly MySqlConnectionFactory _factory;

        public MySqlSessionPersistence(MySqlConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public SessionStore.SessionFileDto Load(string gameId, string npcId, string playerId, string sessionId)
        {
            using (IDbConnection connection = _factory.OpenConnection())
            {
                string payload = connection.QuerySingleOrDefault<string>(@"
SELECT payload_json FROM sessions
WHERE game_id=@GameId AND npc_id=@NpcId AND player_key=@PlayerKey AND session_id=@SessionId",
                    new { GameId = gameId, NpcId = npcId, PlayerKey = playerId ?? string.Empty, SessionId = sessionId });
                return string.IsNullOrWhiteSpace(payload)
                    ? null : JsonConvert.DeserializeObject<SessionStore.SessionFileDto>(payload);
            }
        }

        public void Save(string gameId, SessionStore.SessionFileDto dto)
        {
            if (dto == null) throw new ArgumentNullException(nameof(dto));
            string payload = JsonConvert.SerializeObject(dto, Formatting.None);
            bool pending = dto.evictedMessages != null && dto.evictedMessages.Count > 0;
            using (IDbConnection connection = _factory.OpenConnection())
            {
                connection.Execute(@"
INSERT INTO sessions
 (game_id,npc_id,player_key,session_id,payload_json,has_pending_memory,last_active_utc,created_utc,updated_utc)
VALUES (@GameId,@NpcId,@PlayerKey,@SessionId,@Payload,@Pending,@LastActiveUtc,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE payload_json=VALUES(payload_json), has_pending_memory=VALUES(has_pending_memory),
 last_active_utc=VALUES(last_active_utc), updated_utc=UTC_TIMESTAMP(6)",
                    new
                    {
                        GameId = gameId, NpcId = dto.npcId, PlayerKey = dto.playerId ?? string.Empty,
                        SessionId = dto.sessionId, Payload = payload, Pending = pending,
                        LastActiveUtc = dto.lastActiveUtc == default(DateTime) ? DateTime.UtcNow : dto.lastActiveUtc
                    });
            }
        }

        public List<SessionStore.SessionFileDto> List(string gameId, string npcId, string playerId)
        {
            using (IDbConnection connection = _factory.OpenConnection())
            {
                IEnumerable<string> payloads = connection.Query<string>(@"
SELECT payload_json FROM sessions
WHERE game_id=@GameId AND (@NpcId IS NULL OR npc_id=@NpcId)
 AND (@PlayerKey IS NULL OR player_key=@PlayerKey)
ORDER BY last_active_utc DESC",
                    new { GameId = gameId, NpcId = npcId, PlayerKey = playerId });
                return payloads.Select(Deserialize).Where(x => x != null).ToList();
            }
        }

        public List<PendingMemorySession> ScanPending()
        {
            using (IDbConnection connection = _factory.OpenConnection())
            {
                return connection.Query<PendingRow>(@"
SELECT game_id AS GameId, npc_id AS NpcId, NULLIF(player_key,'') AS PlayerId, session_id AS SessionId
FROM sessions WHERE has_pending_memory=1 AND player_key<>''")
                    .Select(x => new PendingMemorySession
                    {
                        GameId = x.GameId, NpcId = x.NpcId, PlayerId = x.PlayerId, SessionId = x.SessionId
                    }).ToList();
            }
        }

        public bool Delete(string gameId, string npcId, string playerId, string sessionId)
        {
            using (IDbConnection connection = _factory.OpenConnection())
            {
                return connection.Execute(@"
DELETE FROM sessions WHERE game_id=@GameId AND npc_id=@NpcId
 AND player_key=@PlayerKey AND session_id=@SessionId",
                    new { GameId = gameId, NpcId = npcId, PlayerKey = playerId ?? string.Empty, SessionId = sessionId }) > 0;
            }
        }

        private static SessionStore.SessionFileDto Deserialize(string payload)
        {
            try { return JsonConvert.DeserializeObject<SessionStore.SessionFileDto>(payload); }
            catch (JsonException) { return null; }
        }

        private sealed class PendingRow
        {
            public string GameId { get; set; }
            public string NpcId { get; set; }
            public string PlayerId { get; set; }
            public string SessionId { get; set; }
        }
    }
}
