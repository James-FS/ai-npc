using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AIBot.Server
{
    /// <summary>摘要任务的 MongoDB 实现：状态流转均为单文档操作。</summary>
    public sealed class MongoMemorySummaryJobPersistence : IMemorySummaryJobPersistence
    {
        private const string CollectionName = "memory_summary_jobs";
        private readonly IMongoCollection<BsonDocument> _jobs;

        public MongoMemorySummaryJobPersistence(MongoConnectionFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _jobs = factory.Collection<BsonDocument>(CollectionName);
        }

        public void UpsertPending(MemorySummaryJob job, string key)
        {
            DateTime now = DateTime.UtcNow;
            var filter = Builders<BsonDocument>.Filter.Eq("_id", key);
            var update = Builders<BsonDocument>.Update
                .SetOnInsert("gameId", job.GameId)
                .SetOnInsert("npcId", job.NpcId)
                .SetOnInsert("playerId", job.PlayerId)
                .SetOnInsert("sessionId", job.SessionId)
                .SetOnInsert("createdUtc", now)
                .SetOnInsert("attempts", 0)
                .Set("force", job.Force)
                .Set("actor", job.Actor ?? "system")
                .Set("generation", job.Generation)
                .Set("status", "pending")
                .Set("availableUtc", now)
                .Set("updatedUtc", now);
            _jobs.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true });
        }

        public List<MemorySummaryJobRecord> LoadRecoverable()
        {
            FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.In("status",
                new[] { "pending", "processing" });
            return _jobs.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("createdUtc"))
                .ToList().Select(ToRecord).ToList();
        }

        public List<MemorySummaryJobRecord> LoadFailed()
        {
            FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("status", "failed");
            return _jobs.Find(filter).Sort(Builders<BsonDocument>.Sort.Descending("updatedUtc"))
                .ToList().Select(ToRecord).ToList();
        }

        public void MarkProcessing(string key)
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", key);
            var update = Builders<BsonDocument>.Update
                .Set("status", "processing")
                .Set("lastError", BsonNull.Value)
                .Set("updatedUtc", DateTime.UtcNow)
                .Inc("attempts", 1);
            _jobs.UpdateOne(filter, update);
        }

        public void MarkSucceeded(string key)
        {
            _jobs.DeleteOne(Builders<BsonDocument>.Filter.Eq("_id", key));
        }

        public void MarkFailed(string key, string error)
        {
            ExecuteStatus(key, "failed", error);
        }

        public void MarkPending(string key)
        {
            ExecuteStatus(key, "pending", null);
        }

        public void DeleteForPlayer(string gameId, string npcId, string playerId)
        {
            var filter = Builders<BsonDocument>.Filter.Eq("gameId", gameId)
                & Builders<BsonDocument>.Filter.Eq("npcId", npcId)
                & Builders<BsonDocument>.Filter.Eq("playerId", playerId);
            _jobs.DeleteMany(filter);
        }

        private void ExecuteStatus(string key, string status, string error)
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", key);
            var update = Builders<BsonDocument>.Update
                .Set("status", status)
                .Set("lastError", error == null ? BsonNull.Value : (BsonValue)error)
                .Set("updatedUtc", DateTime.UtcNow);
            _jobs.UpdateOne(filter, update);
        }

        private static MemorySummaryJobRecord ToRecord(BsonDocument document)
        {
            return new MemorySummaryJobRecord
            {
                JobKey = Str(document, "_id"),
                GameId = Str(document, "gameId"),
                NpcId = Str(document, "npcId"),
                PlayerId = Str(document, "playerId"),
                SessionId = Str(document, "sessionId"),
                Force = Bool(document, "force"),
                Actor = Str(document, "actor"),
                Generation = Long(document, "generation"),
                Status = Str(document, "status"),
                Attempts = Int(document, "attempts"),
                LastError = document.Contains("lastError") && !document["lastError"].IsBsonNull
                    ? document["lastError"].AsString : null,
                UpdatedUtc = document.Contains("updatedUtc") && !document["updatedUtc"].IsBsonNull
                    ? document["updatedUtc"].ToUniversalTime() : default(DateTime)
            };
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
    }
}
