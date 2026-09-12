using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Memory;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AIBot.Server
{
    /// <summary>
    /// MongoDB 长期记忆仓储：摘要与事实内嵌同一文档，写入用 memoryVersion 做一次原子 CAS。
    /// 事实顺序为写入顺序（与 JSON 版一致）。
    /// </summary>
    public sealed class MongoMemoryRepository : IMemoryRepository
    {
        private const string CollectionName = "player_memories";
        private readonly IMongoCollection<MemoryDocument> _memories;

        public MongoMemoryRepository(MongoConnectionFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _memories = factory.Collection<MemoryDocument>(CollectionName);
        }

        public async Task<PlayerLongTermMemory> LoadPlayerMemoryAsync(string gameId, string npcId,
            string playerId, CancellationToken ct)
        {
            ValidateKey(gameId, npcId, playerId);
            MemoryDocument document = await LoadDocumentAsync(Key(gameId, npcId, playerId), ct);
            return document == null ? NewMemory(gameId, npcId, playerId) : ToMemory(document);
        }

        public async Task<PlayerLongTermMemory> SavePlayerMemoryAsync(PlayerLongTermMemory memory,
            int expectedVersion, CancellationToken ct)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            ValidateKey(memory.gameId, memory.npcId, memory.playerId);

            string id = Key(memory.gameId, memory.npcId, memory.playerId);
            DateTime now = DateTime.UtcNow;
            DateTime updatedUtc = MaxLatest(now, memory.lastSummarizedUtc, memory.facts);

            FilterDefinition<MemoryDocument> filter = Builders<MemoryDocument>.Filter.And(
                Builders<MemoryDocument>.Filter.Eq(d => d.Id, id),
                Builders<MemoryDocument>.Filter.Eq(d => d.MemoryVersion, expectedVersion));

            UpdateDefinition<MemoryDocument> update = Builders<MemoryDocument>.Update
                .Set(d => d.SchemaVersion, 2)
                .Set(d => d.MemoryVersion, expectedVersion + 1)
                .Set(d => d.Summary, memory.summary)
                .Set(d => d.LastSummarizedUtc, memory.lastSummarizedUtc)
                .Set(d => d.Facts, ToFactDocuments(memory.facts, now))
                .Set(d => d.UpdatedUtc, updatedUtc)
                .SetOnInsert(d => d.GameId, memory.gameId)
                .SetOnInsert(d => d.NpcId, memory.npcId)
                .SetOnInsert(d => d.PlayerId, memory.playerId)
                .SetOnInsert(d => d.CreatedUtc, now);

            try
            {
                UpdateResult result = await _memories.UpdateOneAsync(filter, update,
                    new UpdateOptions { IsUpsert = expectedVersion == 0 }, ct);
                if (result.MatchedCount == 0 && result.UpsertedId == null)
                    throw new MemoryVersionConflictException(expectedVersion, await CurrentVersionAsync(id, ct));
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // expectedVersion==0 且文档已存在时 upsert 会撞 _id：等价于版本冲突。
                throw new MemoryVersionConflictException(expectedVersion, await CurrentVersionAsync(id, ct));
            }

            MemoryDocument saved = await LoadDocumentAsync(id, ct);
            if (saved == null)
            {
                // CAS 提交到回读之间文档被并发删除：调用方持有的 expectedVersion 已失效，按冲突处理
                // 而不是返回 null 让上层 NRE（actual=0 表示“已不存在”）。
                throw new MemoryVersionConflictException(expectedVersion, 0);
            }
            return ToMemory(saved);
        }

        public async Task<MemoryListPage> ListPlayerMemoriesAsync(string gameId, string npcId,
            string playerId, int limit, int offset, CancellationToken ct)
        {
            if (!DataStore.IsValidId(gameId)) throw new ArgumentException("invalid game id");
            if (npcId != null && !DataStore.IsValidId(npcId)) throw new ArgumentException("invalid npc id");
            if (playerId != null && !DataStore.IsValidPlayerId(playerId)) throw new ArgumentException("invalid player id");

            int safeLimit = Math.Max(1, Math.Min(200, limit));
            int safeOffset = Math.Max(0, offset);

            FilterDefinition<MemoryDocument> filter = Builders<MemoryDocument>.Filter.Eq(d => d.GameId, gameId);
            FilterDefinitionBuilder<MemoryDocument> where = Builders<MemoryDocument>.Filter;
            if (npcId != null) filter = filter & where.Eq(d => d.NpcId, npcId);
            if (playerId != null) filter = filter & where.Eq(d => d.PlayerId, playerId);

            long total = await _memories.CountDocumentsAsync(filter, cancellationToken: ct);
            List<MemoryDocument> documents = await _memories.Find(filter)
                .Sort(Builders<MemoryDocument>.Sort
                    .Descending(d => d.UpdatedUtc)
                    .Ascending(d => d.NpcId)
                    .Ascending(d => d.PlayerId))
                .Skip(safeOffset)
                .Limit(safeLimit)
                .ToListAsync(ct);

            return new MemoryListPage
            {
                total = (int)total,
                limit = safeLimit,
                offset = safeOffset,
                items = documents.Select(ToListItem).ToList()
            };
        }

        public async Task DeletePlayerMemoryAsync(string gameId, string npcId, string playerId,
            int? expectedVersion, CancellationToken ct)
        {
            ValidateKey(gameId, npcId, playerId);
            string id = Key(gameId, npcId, playerId);
            if (expectedVersion.HasValue)
            {
                MemoryDocument current = await LoadDocumentAsync(id, ct);
                if (current == null) return;                       // 不存在 → 幂等
                if (current.MemoryVersion != expectedVersion.Value)
                    throw new MemoryVersionConflictException(expectedVersion.Value, current.MemoryVersion);
            }
            await _memories.DeleteOneAsync(d => d.Id == id, ct);
        }

        private async Task<MemoryDocument> LoadDocumentAsync(string id, CancellationToken ct)
        {
            return await _memories.Find(d => d.Id == id).FirstOrDefaultAsync(ct);
        }

        private async Task<int> CurrentVersionAsync(string id, CancellationToken ct)
        {
            MemoryDocument document = await _memories.Find(d => d.Id == id)
                .Project<MemoryDocument>(Builders<MemoryDocument>.Projection
                    .Include(d => d.Id).Include(d => d.MemoryVersion))
                .FirstOrDefaultAsync(ct);
            return document?.MemoryVersion ?? 0;
        }

        private static void ValidateKey(string gameId, string npcId, string playerId)
        {
            if (!DataStore.IsValidId(gameId) || !DataStore.IsValidId(npcId) || !DataStore.IsValidPlayerId(playerId))
                throw new ArgumentException("invalid memory key");
        }

        private static string Key(string gameId, string npcId, string playerId)
        {
            return gameId + "|" + npcId + "|" + playerId;
        }

        private static DateTime MaxLatest(DateTime now, DateTime? lastSummarizedUtc,
            IEnumerable<MemoryFact> facts)
        {
            DateTime max = now;
            if (lastSummarizedUtc.HasValue && lastSummarizedUtc.Value > max) max = lastSummarizedUtc.Value;
            foreach (MemoryFact fact in facts ?? Enumerable.Empty<MemoryFact>())
            {
                if (fact != null && fact.updatedUtc != default(DateTime) && fact.updatedUtc > max)
                    max = fact.updatedUtc;
            }
            return max;
        }

        private static List<MemoryFactDocument> ToFactDocuments(IEnumerable<MemoryFact> facts, DateTime now)
        {
            var result = new List<MemoryFactDocument>();
            foreach (MemoryFact fact in facts ?? Enumerable.Empty<MemoryFact>())
            {
                if (fact == null || string.IsNullOrWhiteSpace(fact.id)) continue;
                result.Add(new MemoryFactDocument
                {
                    Id = fact.id,
                    Category = fact.category,
                    Key = fact.key,
                    Value = fact.value,
                    Confidence = fact.confidence,
                    Source = fact.source,
                    SourceSessionId = fact.sourceSessionId,
                    CreatedUtc = fact.createdUtc == default(DateTime) ? now : fact.createdUtc,
                    UpdatedUtc = fact.updatedUtc == default(DateTime) ? now : fact.updatedUtc,
                    Pinned = fact.pinned,
                    ExpiresUtc = fact.expiresUtc
                });
            }
            return result;
        }

        private static PlayerLongTermMemory ToMemory(MemoryDocument document)
        {
            if (document == null) return null;
            return new PlayerLongTermMemory
            {
                schemaVersion = document.SchemaVersion,
                memoryVersion = document.MemoryVersion,
                gameId = document.GameId,
                npcId = document.NpcId,
                playerId = document.PlayerId,
                summary = document.Summary,
                lastSummarizedUtc = document.LastSummarizedUtc,
                facts = (document.Facts ?? new List<MemoryFactDocument>()).Select(f => new MemoryFact
                {
                    id = f.Id,
                    category = f.Category,
                    key = f.Key,
                    value = f.Value,
                    confidence = f.Confidence,
                    source = f.Source,
                    sourceSessionId = f.SourceSessionId,
                    createdUtc = f.CreatedUtc,
                    updatedUtc = f.UpdatedUtc,
                    pinned = f.Pinned,
                    expiresUtc = f.ExpiresUtc
                }).ToList()
            };
        }

        private static MemoryListItem ToListItem(MemoryDocument document)
        {
            return new MemoryListItem
            {
                gameId = document.GameId,
                npcId = document.NpcId,
                playerId = document.PlayerId,
                memoryVersion = document.MemoryVersion,
                factCount = document.Facts?.Count ?? 0,
                hasSummary = !string.IsNullOrWhiteSpace(document.Summary),
                lastSummarizedUtc = document.LastSummarizedUtc,
                updatedUtc = document.UpdatedUtc
            };
        }

        private static PlayerLongTermMemory NewMemory(string gameId, string npcId, string playerId)
        {
            return new PlayerLongTermMemory
            {
                gameId = gameId,
                npcId = npcId,
                playerId = playerId,
                memoryVersion = 0
            };
        }

        internal sealed class MemoryDocument
        {
            [BsonId] public string Id { get; set; }
            [BsonElement("gameId")] public string GameId { get; set; }
            [BsonElement("npcId")] public string NpcId { get; set; }
            [BsonElement("playerId")] public string PlayerId { get; set; }
            [BsonElement("schemaVersion")] public int SchemaVersion { get; set; }
            [BsonElement("memoryVersion")] public int MemoryVersion { get; set; }
            [BsonElement("summary")] public string Summary { get; set; }
            [BsonElement("lastSummarizedUtc")]
            [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
            public DateTime? LastSummarizedUtc { get; set; }
            [BsonElement("facts")] public List<MemoryFactDocument> Facts { get; set; } = new List<MemoryFactDocument>();
            [BsonElement("createdUtc")]
            [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
            public DateTime CreatedUtc { get; set; }
            [BsonElement("updatedUtc")]
            [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
            public DateTime UpdatedUtc { get; set; }
        }

        internal sealed class MemoryFactDocument
        {
            [BsonElement("id")] public string Id { get; set; }
            [BsonElement("category")] public string Category { get; set; }
            [BsonElement("key")] public string Key { get; set; }
            [BsonElement("value")] public string Value { get; set; }
            [BsonElement("confidence")] public float Confidence { get; set; }
            [BsonElement("source")] public string Source { get; set; }
            [BsonElement("sourceSessionId")] public string SourceSessionId { get; set; }
            [BsonElement("createdUtc")]
            [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
            public DateTime CreatedUtc { get; set; }
            [BsonElement("updatedUtc")]
            [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
            public DateTime UpdatedUtc { get; set; }
            [BsonElement("pinned")] public bool Pinned { get; set; }
            [BsonElement("expiresUtc")]
            [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
            public DateTime? ExpiresUtc { get; set; }
        }
    }
}
