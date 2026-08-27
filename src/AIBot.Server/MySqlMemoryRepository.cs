using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Memory;
using Dapper;

namespace AIBot.Server
{
    /// <summary>
    /// MySQL/Dapper 长期记忆仓储。摘要与事实分表保存，写入使用事务和 memoryVersion 乐观锁。
    /// </summary>
    public sealed class MySqlMemoryRepository : IMemoryRepository
    {
        private readonly MySqlConnectionFactory _factory;

        public MySqlMemoryRepository(MySqlConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<PlayerLongTermMemory> LoadPlayerMemoryAsync(string gameId, string npcId,
            string playerId, CancellationToken ct)
        {
            ValidateKey(gameId, npcId, playerId);
            using (IDbConnection connection = _factory.OpenConnection())
            {
                MemoryRow row = await connection.QuerySingleOrDefaultAsync<MemoryRow>(
                    new CommandDefinition(@"
SELECT game_id AS GameId, npc_id AS NpcId, player_id AS PlayerId,
       schema_version AS SchemaVersion, memory_version AS MemoryVersion,
       summary AS Summary, last_summarized_utc AS LastSummarizedUtc
FROM player_memories
WHERE game_id=@GameId AND npc_id=@NpcId AND player_id=@PlayerId",
                    new { GameId = gameId, NpcId = npcId, PlayerId = playerId }, cancellationToken: ct));
                if (row == null) return NewMemory(gameId, npcId, playerId);
                List<FactRow> facts = (await connection.QueryAsync<FactRow>(
                    new CommandDefinition(@"
SELECT id AS Id, category AS Category, fact_key AS FactKey, fact_value AS FactValue,
       confidence AS Confidence, source AS Source, source_session_id AS SourceSessionId,
       created_utc AS CreatedUtc, updated_utc AS UpdatedUtc, pinned AS Pinned, expires_utc AS ExpiresUtc
FROM memory_facts WHERE game_id=@GameId AND npc_id=@NpcId AND player_id=@PlayerId
ORDER BY updated_utc DESC", new { GameId = gameId, NpcId = npcId, PlayerId = playerId }, cancellationToken: ct))).ToList();
                return ToMemory(row, facts);
            }
        }

        public async Task<PlayerLongTermMemory> SavePlayerMemoryAsync(PlayerLongTermMemory memory,
            int expectedVersion, CancellationToken ct)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            ValidateKey(memory.gameId, memory.npcId, memory.playerId);
            using (IDbConnection connection = _factory.OpenConnection())
            using (IDbTransaction transaction = connection.BeginTransaction())
            {
                MemoryRow current = await connection.QuerySingleOrDefaultAsync<MemoryRow>(
                    new CommandDefinition(@"
SELECT game_id AS GameId, npc_id AS NpcId, player_id AS PlayerId,
       schema_version AS SchemaVersion, memory_version AS MemoryVersion,
       summary AS Summary, last_summarized_utc AS LastSummarizedUtc
FROM player_memories WHERE game_id=@GameId AND npc_id=@NpcId AND player_id=@PlayerId FOR UPDATE",
                    new { GameId = memory.gameId, NpcId = memory.npcId, PlayerId = memory.playerId }, transaction, cancellationToken: ct));
                int actualVersion = current?.MemoryVersion ?? 0;
                if (actualVersion != expectedVersion)
                    throw new MemoryVersionConflictException(expectedVersion, actualVersion);

                int nextVersion = actualVersion + 1;
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO player_memories
  (game_id,npc_id,player_id,schema_version,memory_version,summary,last_summarized_utc,created_utc,updated_utc)
VALUES (@GameId,@NpcId,@PlayerId,@SchemaVersion,@MemoryVersion,@Summary,@LastSummarizedUtc,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE
  schema_version=VALUES(schema_version), memory_version=VALUES(memory_version),
  summary=VALUES(summary), last_summarized_utc=VALUES(last_summarized_utc), updated_utc=UTC_TIMESTAMP(6)",
                    new
                    {
                        GameId = memory.gameId, NpcId = memory.npcId, PlayerId = memory.playerId,
                        SchemaVersion = 2, MemoryVersion = nextVersion,
                        Summary = memory.summary, LastSummarizedUtc = memory.lastSummarizedUtc
                    }, transaction, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM memory_facts WHERE game_id=@GameId AND npc_id=@NpcId AND player_id=@PlayerId",
                    new { GameId = memory.gameId, NpcId = memory.npcId, PlayerId = memory.playerId }, transaction, cancellationToken: ct));
                foreach (MemoryFact fact in memory.facts ?? new List<MemoryFact>())
                {
                    if (fact == null || string.IsNullOrWhiteSpace(fact.id)) continue;
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO memory_facts
 (id,game_id,npc_id,player_id,category,fact_key,fact_value,confidence,source,source_session_id,
  created_utc,updated_utc,pinned,expires_utc)
VALUES (@Id,@GameId,@NpcId,@PlayerId,@Category,@FactKey,@FactValue,@Confidence,@Source,@SourceSessionId,
        @CreatedUtc,@UpdatedUtc,@Pinned,@ExpiresUtc)", new
                    {
                        Id = fact.id, GameId = memory.gameId, NpcId = memory.npcId, PlayerId = memory.playerId,
                        Category = fact.category, FactKey = fact.key, FactValue = fact.value,
                        Confidence = fact.confidence, Source = fact.source, SourceSessionId = fact.sourceSessionId,
                        CreatedUtc = fact.createdUtc == default(DateTime) ? DateTime.UtcNow : fact.createdUtc,
                        UpdatedUtc = fact.updatedUtc == default(DateTime) ? DateTime.UtcNow : fact.updatedUtc,
                        Pinned = fact.pinned, ExpiresUtc = fact.expiresUtc
                    }, transaction, cancellationToken: ct));
                }
                transaction.Commit();
                PlayerLongTermMemory saved = Clone(memory);
                saved.schemaVersion = 2;
                saved.memoryVersion = nextVersion;
                saved.facts = saved.facts ?? new List<MemoryFact>();
                return saved;
            }
        }

        public async Task<MemoryListPage> ListPlayerMemoriesAsync(string gameId, string npcId,
            string playerId, int limit, int offset, CancellationToken ct)
        {
            if (!DataStore.IsValidId(gameId)) throw new ArgumentException("invalid game id");
            if (npcId != null && !DataStore.IsValidId(npcId)) throw new ArgumentException("invalid npc id");
            if (playerId != null && !DataStore.IsValidPlayerId(playerId)) throw new ArgumentException("invalid player id");
            int safeLimit = Math.Max(1, Math.Min(200, limit));
            int safeOffset = Math.Max(0, offset);
            using (IDbConnection connection = _factory.OpenConnection())
            {
                var args = new DynamicParameters();
                args.Add("GameId", gameId); args.Add("NpcId", npcId); args.Add("PlayerId", playerId);
                int total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM player_memories
WHERE game_id=@GameId AND (@NpcId IS NULL OR npc_id=@NpcId)
  AND (@PlayerId IS NULL OR player_id=@PlayerId)", args, cancellationToken: ct));
                List<MemoryListRow> rows = (await connection.QueryAsync<MemoryListRow>(new CommandDefinition(@"
SELECT m.game_id AS GameId, m.npc_id AS NpcId, m.player_id AS PlayerId,
       m.memory_version AS MemoryVersion, m.summary IS NOT NULL AND m.summary <> '' AS HasSummary,
       m.last_summarized_utc AS LastSummarizedUtc, m.updated_utc AS UpdatedUtc,
       (SELECT COUNT(*) FROM memory_facts f WHERE f.game_id=m.game_id AND f.npc_id=m.npc_id AND f.player_id=m.player_id) AS FactCount
FROM player_memories m
WHERE m.game_id=@GameId AND (@NpcId IS NULL OR m.npc_id=@NpcId)
  AND (@PlayerId IS NULL OR m.player_id=@PlayerId)
ORDER BY m.updated_utc DESC, m.npc_id, m.player_id LIMIT @Limit OFFSET @Offset",
                    new { GameId = gameId, NpcId = npcId, PlayerId = playerId, Limit = safeLimit, Offset = safeOffset }, cancellationToken: ct))).ToList();
                return new MemoryListPage
                {
                    total = total, limit = safeLimit, offset = safeOffset,
                    items = rows.Select(x => new MemoryListItem
                    {
                        gameId = x.GameId, npcId = x.NpcId, playerId = x.PlayerId,
                        memoryVersion = x.MemoryVersion, factCount = x.FactCount,
                        hasSummary = x.HasSummary, lastSummarizedUtc = x.LastSummarizedUtc,
                        updatedUtc = x.UpdatedUtc
                    }).ToList()
                };
            }
        }

        public async Task DeletePlayerMemoryAsync(string gameId, string npcId, string playerId,
            int? expectedVersion, CancellationToken ct)
        {
            ValidateKey(gameId, npcId, playerId);
            using (IDbConnection connection = _factory.OpenConnection())
            using (IDbTransaction transaction = connection.BeginTransaction())
            {
                int? actual = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT memory_version FROM player_memories WHERE game_id=@GameId AND npc_id=@NpcId AND player_id=@PlayerId FOR UPDATE",
                    new { GameId = gameId, NpcId = npcId, PlayerId = playerId }, transaction, cancellationToken: ct));
                if (!actual.HasValue) { transaction.Commit(); return; }
                if (expectedVersion.HasValue && actual.Value != expectedVersion.Value)
                    throw new MemoryVersionConflictException(expectedVersion.Value, actual.Value);
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM player_memories WHERE game_id=@GameId AND npc_id=@NpcId AND player_id=@PlayerId",
                    new { GameId = gameId, NpcId = npcId, PlayerId = playerId }, transaction, cancellationToken: ct));
                transaction.Commit();
            }
        }

        private static void ValidateKey(string gameId, string npcId, string playerId)
        {
            if (!DataStore.IsValidId(gameId) || !DataStore.IsValidId(npcId) || !DataStore.IsValidPlayerId(playerId))
                throw new ArgumentException("invalid memory key");
        }

        private static PlayerLongTermMemory NewMemory(string gameId, string npcId, string playerId)
        {
            return new PlayerLongTermMemory { gameId = gameId, npcId = npcId, playerId = playerId, memoryVersion = 0 };
        }

        private static PlayerLongTermMemory ToMemory(MemoryRow row, List<FactRow> facts)
        {
            return new PlayerLongTermMemory
            {
                schemaVersion = row.SchemaVersion,
                memoryVersion = row.MemoryVersion,
                gameId = row.GameId, npcId = row.NpcId, playerId = row.PlayerId,
                summary = row.Summary, lastSummarizedUtc = row.LastSummarizedUtc,
                facts = facts.Select(f => new MemoryFact
                {
                    id = f.Id, category = f.Category, key = f.FactKey, value = f.FactValue,
                    confidence = f.Confidence, source = f.Source, sourceSessionId = f.SourceSessionId,
                    createdUtc = f.CreatedUtc, updatedUtc = f.UpdatedUtc,
                    pinned = f.Pinned, expiresUtc = f.ExpiresUtc
                }).ToList()
            };
        }

        private static PlayerLongTermMemory Clone(PlayerLongTermMemory source)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<PlayerLongTermMemory>(
                Newtonsoft.Json.JsonConvert.SerializeObject(source));
        }

        private sealed class MemoryRow
        {
            public string GameId { get; set; }
            public string NpcId { get; set; }
            public string PlayerId { get; set; }
            public int SchemaVersion { get; set; }
            public int MemoryVersion { get; set; }
            public string Summary { get; set; }
            public DateTime? LastSummarizedUtc { get; set; }
        }

        private sealed class FactRow
        {
            public string Id { get; set; }
            public string Category { get; set; }
            public string FactKey { get; set; }
            public string FactValue { get; set; }
            public float Confidence { get; set; }
            public string Source { get; set; }
            public string SourceSessionId { get; set; }
            public DateTime CreatedUtc { get; set; }
            public DateTime UpdatedUtc { get; set; }
            public bool Pinned { get; set; }
            public DateTime? ExpiresUtc { get; set; }
        }

        private sealed class MemoryListRow
        {
            public string GameId { get; set; }
            public string NpcId { get; set; }
            public string PlayerId { get; set; }
            public int MemoryVersion { get; set; }
            public int FactCount { get; set; }
            public bool HasSummary { get; set; }
            public DateTime? LastSummarizedUtc { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }
    }
}
