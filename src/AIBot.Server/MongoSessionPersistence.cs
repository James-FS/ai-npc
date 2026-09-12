using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;

namespace AIBot.Server
{
    /// <summary>
    /// Session 的 MongoDB 实现：payload 以 JSON↔BsonDocument 桥接保存，顶层冗余查询字段。
    /// 过期清理走 deleteMany（sessions 不建 TTL：待摘要/挂起轮需保留）。
    /// </summary>
    public sealed class MongoSessionPersistence : ISessionPersistence
    {
        private const string CollectionName = "sessions";
        private static readonly AIBot.Core.Logging.ILogSink Log = new AIBot.Core.Logging.ConsoleLogSink();
        private readonly IMongoCollection<BsonDocument> _sessions;

        public MongoSessionPersistence(MongoConnectionFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _sessions = factory.Collection<BsonDocument>(CollectionName);
        }

        public SessionStore.SessionFileDto Load(string gameId, string npcId, string playerId, string sessionId)
        {
            string id = SessionStore.IdentityKey(gameId, npcId, playerId, sessionId);
            BsonDocument document = _sessions.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefault();
            if (document == null) return null;
            return ToDto(document);
        }

        public SessionSaveResult Save(string gameId, SessionStore.SessionFileDto dto)
        {
            if (dto == null) throw new ArgumentNullException(nameof(dto));
            var result = new SessionSaveResult();
            try
            {
                string id = SessionStore.IdentityKey(gameId, dto.npcId, dto.playerId, dto.sessionId);
                string payloadJson = JsonConvert.SerializeObject(dto, Formatting.None);
                DateTime lastActive = dto.lastActiveUtc == default(DateTime) ? DateTime.UtcNow : dto.lastActiveUtc;
                DateTime now = DateTime.UtcNow;
                bool pending = dto.evictedMessages != null && dto.evictedMessages.Count > 0;

                var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
                var update = Builders<BsonDocument>.Update
                    .Set("gameId", gameId)
                    .Set("npcId", dto.npcId)
                    .Set("playerKey", dto.playerId ?? string.Empty)
                    .Set("sessionId", dto.sessionId)
                    .Set("payload", BsonDocument.Parse(payloadJson))
                    .Set("hasPendingMemory", pending)
                    .Set("lastActiveUtc", new BsonDateTime(lastActive))
                    .Set("updatedUtc", new BsonDateTime(now))
                    .SetOnInsert("createdUtc", new BsonDateTime(now));
                _sessions.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true });
                result.Persisted = true;
                return result;
            }
            catch (Exception ex)
            {
                // 与 JSON 实现的“保存失败只记 warning 并返回 Persisted=false”契约保持一致。
                Log.Log(AIBot.Core.Logging.LogLevel.Warning,
                    "会话保存失败(" + dto.sessionId + "): " + ex.Message);
                return result;
            }
        }

        public List<SessionStore.SessionFileDto> List(string gameId, string npcId, string playerId)
        {
            var builder = Builders<BsonDocument>.Filter;
            FilterDefinition<BsonDocument> filter = builder.Eq("gameId", gameId);
            if (npcId != null) filter = filter & builder.Eq("npcId", npcId);
            if (playerId != null) filter = filter & builder.Eq("playerKey", playerId);
            List<BsonDocument> documents = _sessions.Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Descending("lastActiveUtc")).ToList();
            return documents.Select(ToDto).Where(x => x != null).ToList();
        }

        public List<PendingMemorySession> ScanPending()
        {
            var builder = Builders<BsonDocument>.Filter;
            // 必须排除 playerKey="" 的 legacy session（只有非空 playerKey 才可摘要）。
            FilterDefinition<BsonDocument> filter = builder.Eq("hasPendingMemory", true)
                & builder.Ne("playerKey", string.Empty);
            return _sessions.Find(filter).ToList().Select(document => new PendingMemorySession
            {
                GameId = Str(document, "gameId"),
                NpcId = Str(document, "npcId"),
                PlayerId = string.IsNullOrEmpty(Str(document, "playerKey")) ? null : Str(document, "playerKey"),
                SessionId = Str(document, "sessionId")
            }).ToList();
        }

        public bool Delete(string gameId, string npcId, string playerId, string sessionId)
        {
            string id = SessionStore.IdentityKey(gameId, npcId, playerId, sessionId);
            try
            {
                return _sessions.DeleteOne(Builders<BsonDocument>.Filter.Eq("_id", id)).DeletedCount > 0;
            }
            catch (Exception ex)
            {
                // 上层按 IOException 处理删除失败并保留内存状态（与 JSON 实现契约一致）。
                throw new System.IO.IOException("会话删除失败，内存状态已保留，可安全重试", ex);
            }
        }

        public int DeleteExpired(TimeSpan idle, ISet<string> activeKeys, ISet<string> protectedPaths)
        {
            if (idle <= TimeSpan.Zero) return 0;
            DateTime cutoff = DateTime.UtcNow - idle;
            var active = new BsonArray((activeKeys ?? new HashSet<string>())
                .Select(key => (BsonValue)key));

            var filter = new BsonDocument
            {
                { "lastActiveUtc", new BsonDocument("$lt", new BsonDateTime(cutoff)) },
                { "hasPendingMemory", false },
                { "payload.pendingToolRound", BsonNull.Value },
                { "payload.recentRequests", new BsonDocument("$not",
                    new BsonDocument("$elemMatch", new BsonDocument("status", ChatRequestStatuses.Processing))) },
                { "_id", new BsonDocument("$nin", active) }
            };
            DeleteResult result = _sessions.DeleteMany(
                new BsonDocumentFilterDefinition<BsonDocument>(filter));
            return (int)result.DeletedCount;
        }

        private static SessionStore.SessionFileDto ToDto(BsonDocument document)
        {
            if (document == null || !document.Contains("payload") || !document["payload"].IsBsonDocument) return null;
            // 不能用 payload.ToJson()：Shell 模式会把 Int64/DateTime 写成 NumberLong/ISODate（非合法 JSON）。
            SessionStore.SessionFileDto dto = JsonConvert.DeserializeObject<SessionStore.SessionFileDto>(
                BsonJson.ToJsonString(document["payload"]));
            if (dto == null) return null;
            dto.npcId = Str(document, "npcId");
            dto.sessionId = Str(document, "sessionId");
            // 空串 playerKey 还原为 null（legacy），与门面 Key 的去重语义一致。
            string playerKey = Str(document, "playerKey");
            dto.playerId = string.IsNullOrEmpty(playerKey) ? null : playerKey;
            if (dto.lastActiveUtc == default(DateTime) && document.Contains("lastActiveUtc")
                && !document["lastActiveUtc"].IsBsonNull)
                dto.lastActiveUtc = document["lastActiveUtc"].ToUniversalTime();
            return dto;
        }

        private static string Str(BsonDocument document, string name)
        {
            if (!document.Contains(name)) return null;
            BsonValue value = document[name];
            return value.IsBsonNull ? null : value.AsString;
        }
    }
}
