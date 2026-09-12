using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>记忆审计的 MongoDB 实现。Write 幂等（吞掉重复键）；TTL 由 MongoInitializer 建立在 ts 上。</summary>
    public sealed class MongoMemoryAuditStore : IMemoryAuditStore
    {
        private const string CollectionName = "memory_audits";
        private readonly IMongoCollection<BsonDocument> _audits;

        public MongoMemoryAuditStore(MongoConnectionFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _audits = factory.Collection<BsonDocument>(CollectionName);
        }

        public void Write(MemoryAuditEntry entry)
        {
            var document = new BsonDocument
            {
                ["_id"] = entry.id,
                ["ts"] = new BsonDateTime(ParseUtc(entry.ts)),
                ["gameId"] = entry.gameId ?? string.Empty,
                ["npcId"] = Nullable(entry.npcId),
                ["playerId"] = Nullable(entry.playerId),
                ["actor"] = Nullable(entry.actor),
                ["action"] = Nullable(entry.action),
                // before/after 是 JToken，可能是 null/scalar/object，必须用 BsonValue 承载。
                ["before"] = ToBsonValue(entry.before),
                ["after"] = ToBsonValue(entry.after),
                ["metadata"] = entry.metadata == null ? BsonNull.Value : BsonDocument.Parse(entry.metadata.ToString(Formatting.None))
            };
            try
            {
                _audits.InsertOne(document);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // 幂等：同一次 RecordRequired 的重试保持 id 不变，重复写视为成功。
            }
        }

        public JObject Query(string gameId, string npcId, string playerId, string action,
            string date, int limit, int offset)
        {
            DateTime day;
            if (string.IsNullOrWhiteSpace(date) || !DateTime.TryParse(date, out day)) day = DateTime.UtcNow;
            // 与 JSON 版一致：date 表示本地日历日，转成 UTC 区间后再与 UTC 存储的 ts 比较。
            DateTime startUtc = day.Date.ToUniversalTime();
            DateTime endUtc = day.Date.AddDays(1).ToUniversalTime();

            FilterDefinitionBuilder<BsonDocument> where = Builders<BsonDocument>.Filter;
            FilterDefinition<BsonDocument> filter = where.Eq("gameId", gameId)
                & where.Gte("ts", new BsonDateTime(startUtc))
                & where.Lt("ts", new BsonDateTime(endUtc));
            if (npcId != null) filter = filter & where.Eq("npcId", npcId);
            if (playerId != null) filter = filter & where.Eq("playerId", playerId);
            if (action != null) filter = filter & where.Eq("action", action);

            int safeLimit = Math.Max(1, Math.Min(200, limit));
            int safeOffset = Math.Max(0, offset);
            long total = _audits.CountDocuments(filter);
            List<BsonDocument> documents = _audits.Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Descending("ts"))
                .Skip(safeOffset).Limit(safeLimit).ToList();

            return new JObject
            {
                ["date"] = day.ToString("yyyy-MM-dd"),
                ["total"] = total,
                ["limit"] = safeLimit,
                ["offset"] = safeOffset,
                ["items"] = new JArray(documents.Select(ToJson))
            };
        }

        public int DeleteExpired(DateTime cutoffUtc)
        {
            // 过期删除交给 TTL 索引；返回 0 以保持接口契约。
            return 0;
        }

        private static JObject ToJson(BsonDocument document)
        {
            return new JObject
            {
                ["id"] = Str(document, "_id"),
                ["ts"] = document.Contains("ts") && !document["ts"].IsBsonNull
                    ? document["ts"].ToUniversalTime().ToString("o") : null,
                ["gameId"] = Str(document, "gameId"),
                ["npcId"] = document.Contains("npcId") && !document["npcId"].IsBsonNull
                    ? document["npcId"].AsString : null,
                ["playerId"] = document.Contains("playerId") && !document["playerId"].IsBsonNull
                    ? document["playerId"].AsString : null,
                ["actor"] = Str(document, "actor"),
                ["action"] = Str(document, "action"),
                ["before"] = document.Contains("before") ? BsonJson.ToJToken(document["before"]) : JValue.CreateNull(),
                ["after"] = document.Contains("after") ? BsonJson.ToJToken(document["after"]) : JValue.CreateNull(),
                ["metadata"] = document.Contains("metadata") && document["metadata"].IsBsonDocument
                    ? (JObject)BsonJson.ToJToken(document["metadata"]) : null
            };
        }

        private static string Str(BsonDocument document, string name)
        {
            if (!document.Contains(name)) return null;
            BsonValue value = document[name];
            return value.IsBsonNull ? null : value.AsString;
        }

        private static BsonValue ToBsonValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return BsonNull.Value;
            return BsonValue.Create(BsonDocument.Parse("{\"v\":" + token.ToString(Formatting.None) + "}")["v"]);
        }

        /// <summary>BsonDocument 索引器不接受 null 字符串，需显式转 BsonNull。</summary>
        private static BsonValue Nullable(string value)
        {
            return string.IsNullOrEmpty(value) ? BsonNull.Value : new BsonString(value);
        }

        private static DateTime ParseUtc(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime() : DateTime.UtcNow;
        }
    }
}
