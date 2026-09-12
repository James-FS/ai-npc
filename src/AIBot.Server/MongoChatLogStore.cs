using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AIBot.Server
{
    /// <summary>对话日志的 MongoDB 实现。TTL 由 MongoInitializer 建立在 ts 上。</summary>
    public sealed class MongoChatLogStore : IChatLogStore
    {
        private const string CollectionName = "chat_logs";
        private readonly IMongoCollection<BsonDocument> _logs;

        public MongoChatLogStore(MongoConnectionFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _logs = factory.Collection<BsonDocument>(CollectionName);
        }

        public void Write(string gameId, ChatLogService.ChatLogEntry entry)
        {
            DateTime ts = ParseUtc(entry.ts);
            var document = new BsonDocument
            {
                ["gameId"] = gameId ?? string.Empty,
                ["ts"] = new BsonDateTime(ts),
                ["npcId"] = entry.npcId ?? string.Empty,
                ["playerId"] = entry.playerId ?? string.Empty,
                ["sessionId"] = entry.sessionId ?? string.Empty,
                ["legacyMemoryScope"] = entry.legacyMemoryScope,
                ["userMessage"] = entry.userMessage ?? string.Empty,
                ["say"] = entry.say ?? string.Empty,
                ["emotion"] = entry.emotion ?? string.Empty,
                ["action"] = entry.action ?? string.Empty,
                ["fallback"] = entry.fallback,
                ["promptTokens"] = entry.promptTokens,
                ["completionTokens"] = entry.completionTokens,
                ["elapsedMs"] = entry.elapsedMs,
                ["tools"] = new BsonArray((entry.tools ?? new List<string>()).Select(t => (BsonValue)(t ?? string.Empty))),
                ["injection"] = entry.injection
            };
            _logs.InsertOne(document);
        }

        public JObject Query(string gameId, string date, string npcId, int limit, int offset)
        {
            DateTime day;
            if (string.IsNullOrEmpty(date) || !DateTime.TryParse(date,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out day)) day = DateTime.UtcNow;
            day = day.ToUniversalTime();
            DateTime StartUtc = day.Date;
            DateTime endUtc = day.Date.AddDays(1);

            FilterDefinitionBuilder<BsonDocument> where = Builders<BsonDocument>.Filter;
            FilterDefinition<BsonDocument> filter = where.Eq("gameId", gameId)
                & where.Gte("ts", new BsonDateTime(StartUtc))
                & where.Lt("ts", new BsonDateTime(endUtc));
            if (!string.IsNullOrEmpty(npcId)) filter = filter & where.Eq("npcId", npcId);

            int safeLimit = Math.Max(1, Math.Min(200, limit));
            int safeOffset = Math.Max(0, offset);
            long total = _logs.CountDocuments(filter);
            List<BsonDocument> documents = _logs.Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Descending("ts"))
                .Skip(safeOffset).Limit(safeLimit).ToList();

            return new JObject
            {
                ["total"] = total,
                ["items"] = new JArray(documents.Select(ToJson)),
                ["date"] = day.ToString("yyyy-MM-dd")
            };
        }

        private static JObject ToJson(BsonDocument document)
        {
            BsonArray tools = document.Contains("tools") && document["tools"].IsBsonArray
                ? document["tools"].AsBsonArray : new BsonArray();
            var toolArray = new JArray();
            foreach (BsonValue tool in tools) toolArray.Add(tool.IsBsonNull ? null : new JValue(tool.AsString));

            return new JObject
            {
                ["ts"] = ToIso(document, "ts"),
                ["npcId"] = Str(document, "npcId"),
                ["playerId"] = Str(document, "playerId"),
                ["sessionId"] = Str(document, "sessionId"),
                ["legacyMemoryScope"] = Bool(document, "legacyMemoryScope"),
                ["userMessage"] = Str(document, "userMessage"),
                ["say"] = Str(document, "say"),
                ["emotion"] = Str(document, "emotion"),
                ["action"] = Str(document, "action"),
                ["fallback"] = Bool(document, "fallback"),
                ["promptTokens"] = Int(document, "promptTokens"),
                ["completionTokens"] = Int(document, "completionTokens"),
                ["elapsedMs"] = Long(document, "elapsedMs"),
                ["tools"] = toolArray,
                ["injection"] = Bool(document, "injection")
            };
        }

        private static string ToIso(BsonDocument document, string name)
        {
            if (!document.Contains(name)) return null;
            BsonValue value = document[name];
            return value.IsBsonNull ? null : value.ToUniversalTime().ToString("o");
        }

        private static string Str(BsonDocument document, string name)
        {
            if (!document.Contains(name)) return null;
            BsonValue value = document[name];
            return value.IsBsonNull ? null : value.AsString;
        }

        private static bool Bool(BsonDocument document, string name)
        {
            return document.Contains(name) && !document[name].IsBsonNull && document[name].ToBoolean();
        }

        private static int Int(BsonDocument document, string name)
        {
            return document.Contains(name) && !document[name].IsBsonNull ? document[name].ToInt32() : 0;
        }

        private static long Long(BsonDocument document, string name)
        {
            return document.Contains(name) && !document[name].IsBsonNull ? document[name].ToInt64() : 0;
        }

        private static DateTime ParseUtc(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime() : DateTime.UtcNow;
        }
    }
}
