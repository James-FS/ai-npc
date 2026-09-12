using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AIBot.Server
{
    /// <summary>Mongo 集合与索引的幂等初始化（取代原 MySQL 建表迁移）。</summary>
    public static class MongoInitializer
    {
        public const string MetaCollection = "_meta";
        private const string MetaId = "mongo-initial";
        private const string TtlIndexName = "ttl_ts";

        /// <summary>就绪探针校验的必需集合清单（与 ReadinessService/StartupDiagnostics 共用）。</summary>
        public static readonly string[] RequiredCollections =
        {
            "player_memories", "memory_audits", "chat_logs", "sessions", "memory_summary_jobs", MetaCollection
        };

        public static async Task ApplyAsync(MongoConnectionFactory factory, IConfiguration config,
            CancellationToken ct)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            IMongoDatabase db = factory.Database;

            int chatTtlSeconds = Math.Max(1, config.GetValue<int?>("Logging:ChatRetentionDays") ?? 30) * 86400;
            int auditTtlSeconds = Math.Max(1, config.GetValue<int?>("Logging:AuditRetentionDays") ?? 365) * 86400;

            var existing = new HashSet<string>(await ListCollectionsAsync(db, ct), StringComparer.Ordinal);
            foreach (string name in RequiredCollections)
            {
                if (!existing.Contains(name))
                    await db.CreateCollectionAsync(name, cancellationToken: ct);
            }

            // player_memories
            await CreateIndexAsync(db, "player_memories", "{ gameId: 1, updatedUtc: -1 }", ct);
            await CreateIndexAsync(db, "player_memories", "{ gameId: 1, npcId: 1, playerId: 1 }", ct);
            // sessions（不建 TTL：待摘要/挂起轮需保留，走 ISessionPersistence.DeleteExpired）
            await CreateIndexAsync(db, "sessions", "{ gameId: 1, npcId: 1, playerKey: 1, lastActiveUtc: -1 }", ct);
            await CreateIndexAsync(db, "sessions", "{ hasPendingMemory: 1, playerKey: 1 }", ct);
            // DeleteExpired 按 lastActiveUtc < cutoff 范围删除（不带 gameId），单列索引避免每日清理全表扫描。
            await CreateIndexAsync(db, "sessions", "{ lastActiveUtc: 1 }", ct);
            // chat_logs
            await CreateIndexAsync(db, "chat_logs", "{ gameId: 1, ts: -1 }", ct);
            await CreateIndexAsync(db, "chat_logs", "{ gameId: 1, npcId: 1, ts: -1 }", ct);
            await CreateIndexAsync(db, "chat_logs", "{ gameId: 1, playerId: 1, ts: -1 }", ct);
            // memory_audits
            await CreateIndexAsync(db, "memory_audits", "{ gameId: 1, ts: -1 }", ct);
            await CreateIndexAsync(db, "memory_audits", "{ gameId: 1, npcId: 1, playerId: 1, action: 1, ts: -1 }", ct);
            // memory_summary_jobs
            await CreateIndexAsync(db, "memory_summary_jobs", "{ status: 1, updatedUtc: -1 }", ct);
            await CreateIndexAsync(db, "memory_summary_jobs", "{ gameId: 1, npcId: 1, playerId: 1, sessionId: 1 }", ct);

            // TTL（expireAfterSeconds 不能原地改，不一致时重建）
            await EnsureTtlIndexAsync(db, "chat_logs", chatTtlSeconds, ct);
            await EnsureTtlIndexAsync(db, "memory_audits", auditTtlSeconds, ct);

            // _meta：记录初始化时间与 TTL 秒数（重复执行不覆盖 appliedUtc）
            DateTime now = DateTime.UtcNow;
            IMongoCollection<BsonDocument> meta = db.GetCollection<BsonDocument>(MetaCollection);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", MetaId);
            UpdateDefinition<BsonDocument> update = Builders<BsonDocument>.Update
                .SetOnInsert("name", MetaId)
                .SetOnInsert("appliedUtc", now)
                .Set("indexes.chatLogsTtlSeconds", chatTtlSeconds)
                .Set("indexes.auditsTtlSeconds", auditTtlSeconds)
                .Set("updatedUtc", now);
            await meta.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);

            // 自检：初始化后必需集合必须齐全，否则尽早失败而非留到就绪探针
            var after = new HashSet<string>(await ListCollectionsAsync(db, ct), StringComparer.Ordinal);
            List<string> missing = RequiredCollections.Where(name => !after.Contains(name)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("Mongo 初始化后仍缺少集合: " + string.Join(",", missing));
        }

        private static async Task<List<string>> ListCollectionsAsync(IMongoDatabase db, CancellationToken ct)
        {
            using (IAsyncCursor<string> cursor = await db.ListCollectionNamesAsync(cancellationToken: ct))
            {
                return await cursor.ToListAsync(ct);
            }
        }

        private static async Task CreateIndexAsync(IMongoDatabase db, string collection, string keysJson,
            CancellationToken ct)
        {
            var model = new CreateIndexModel<BsonDocument>(BsonDocument.Parse(keysJson));
            await db.GetCollection<BsonDocument>(collection)
                .Indexes.CreateOneAsync(model, cancellationToken: ct);
        }

        private static async Task EnsureTtlIndexAsync(IMongoDatabase db, string collection, int seconds,
            CancellationToken ct)
        {
            IMongoCollection<BsonDocument> col = db.GetCollection<BsonDocument>(collection);
            bool needCreate = true;
            using (IAsyncCursor<BsonDocument> cursor = await col.Indexes.ListAsync(ct))
            {
                List<BsonDocument> indexes = await cursor.ToListAsync(ct);
                foreach (BsonDocument index in indexes)
                {
                    if (!index.TryGetValue("name", out BsonValue name) || name.AsString != TtlIndexName) continue;
                    int existingSeconds = index.Contains("expireAfterSeconds")
                        ? index["expireAfterSeconds"].ToInt32() : -1;
                    if (existingSeconds == seconds) needCreate = false;
                    else await col.Indexes.DropOneAsync(TtlIndexName, ct);
                    break;
                }
            }
            if (!needCreate) return;

            var options = new CreateIndexOptions
            {
                Name = TtlIndexName,
                ExpireAfter = TimeSpan.FromSeconds(seconds)
            };
            var model = new CreateIndexModel<BsonDocument>(BsonDocument.Parse("{ ts: 1 }"), options);
            await col.Indexes.CreateOneAsync(model, cancellationToken: ct);
        }
    }
}
