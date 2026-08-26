using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Memory;

namespace AIBot.Server
{
    public sealed class MemoryRetentionScan
    {
        public int totalMemoryCount;
        public int batchLimit;
        public bool hasMoreCandidates;
        public List<MemoryListItem> candidates = new List<MemoryListItem>();
    }

    public sealed class MemoryValidationException : Exception
    {
        public MemoryValidationException(string message) : base(message) { }
    }

    /// <summary>玩家/NPC 长期记忆的加载、旧数据迁移、合并与版本化保存。</summary>
    public sealed class PlayerMemoryService
    {
        private readonly IMemoryRepository _repository;

        public PlayerMemoryService(IMemoryRepository repository)
        {
            _repository = repository;
        }

        public Task<PlayerLongTermMemory> LoadAsync(string gameId, string npcId, string playerId,
            CancellationToken ct)
        {
            return _repository.LoadPlayerMemoryAsync(gameId, npcId, playerId, ct);
        }

        public Task<MemoryListPage> ListAsync(string gameId, string npcId, string playerId,
            int limit, int offset, CancellationToken ct)
        {
            return _repository.ListPlayerMemoriesAsync(gameId, npcId, playerId, limit, offset, ct);
        }

        public Task DeleteAsync(string gameId, string npcId, string playerId, int? expectedVersion,
            CancellationToken ct)
        {
            return _repository.DeletePlayerMemoryAsync(gameId, npcId, playerId, expectedVersion, ct);
        }

        /// <summary>直接读取倒序分页的最后一批（最旧记录），避免只扫描最新记录而漏清理。</summary>
        public async Task<MemoryRetentionScan> FindRetentionCandidatesAsync(string gameId,
            DateTime cutoffUtc, int limit, CancellationToken ct)
        {
            int safeLimit = Math.Max(1, Math.Min(1000, limit));
            MemoryListPage probe = await _repository.ListPlayerMemoriesAsync(gameId, null, null,
                1, 0, ct);
            int total = Math.Max(0, probe.total);
            int fetchCount = Math.Min(total, safeLimit + 1);
            int offset = Math.Max(0, total - fetchCount);
            var oldestBatch = new List<MemoryListItem>();
            while (offset < total && oldestBatch.Count < fetchCount)
            {
                MemoryListPage page = await _repository.ListPlayerMemoriesAsync(gameId, null, null,
                    Math.Min(200, fetchCount - oldestBatch.Count), offset, ct);
                if (page.items == null || page.items.Count == 0) break;
                oldestBatch.AddRange(page.items);
                offset += page.items.Count;
            }
            List<MemoryListItem> inactive = oldestBatch
                .Where(x => x != null && x.updatedUtc < cutoffUtc)
                .OrderBy(x => x.updatedUtc).ToList();
            return new MemoryRetentionScan
            {
                totalMemoryCount = total,
                batchLimit = safeLimit,
                hasMoreCandidates = inactive.Count > safeLimit,
                candidates = inactive.Take(safeLimit).ToList()
            };
        }

        public async Task<PlayerLongTermMemory> UpdateSummaryAsync(string gameId, string npcId,
            string playerId, string summary, int expectedVersion, CancellationToken ct)
        {
            PlayerLongTermMemory memory = await LoadExpectedAsync(gameId, npcId, playerId,
                expectedVersion, ct);
            memory.summary = summary?.Trim() ?? string.Empty;
            return await _repository.SavePlayerMemoryAsync(memory, expectedVersion, ct);
        }

        public async Task<PlayerLongTermMemory> AddFactAsync(string gameId, string npcId,
            string playerId, MemoryFact fact, int expectedVersion, int maxFacts, CancellationToken ct)
        {
            PlayerLongTermMemory memory = await LoadExpectedAsync(gameId, npcId, playerId,
                expectedVersion, ct);
            fact = NormalizeManualFact(fact, null);
            if (memory.facts.Any(f => f != null && string.Equals(f.id, fact.id, StringComparison.Ordinal)))
                throw new MemoryValidationException("fact id 已存在");
            if (!string.IsNullOrEmpty(fact.key) && memory.facts.Any(f => f != null
                && string.Equals(f.key, fact.key, StringComparison.Ordinal)))
                throw new MemoryValidationException("fact key 已存在，请编辑原事实");
            if (!fact.pinned && memory.facts.Count(f => f != null && !f.pinned) >= Math.Max(1, maxFacts))
                throw new MemoryValidationException("非固定事实已达到 maxFacts 上限");
            memory.facts.Add(fact);
            return await _repository.SavePlayerMemoryAsync(memory, expectedVersion, ct);
        }

        public async Task<PlayerLongTermMemory> UpdateFactAsync(string gameId, string npcId,
            string playerId, string factId, MemoryFact fact, int expectedVersion, CancellationToken ct)
        {
            PlayerLongTermMemory memory = await LoadExpectedAsync(gameId, npcId, playerId,
                expectedVersion, ct);
            int index = memory.facts.FindIndex(f => f != null
                && string.Equals(f.id, factId, StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException("fact not found: " + factId);
            MemoryFact existing = memory.facts[index];
            MemoryFact updated = NormalizeManualFact(fact, existing);
            updated.id = existing.id;
            updated.createdUtc = existing.createdUtc;
            if (!string.IsNullOrEmpty(updated.key) && memory.facts.Any(f => f != null
                && !string.Equals(f.id, factId, StringComparison.Ordinal)
                && string.Equals(f.key, updated.key, StringComparison.Ordinal)))
                throw new MemoryValidationException("fact key 已被其他事实使用");
            memory.facts[index] = updated;
            return await _repository.SavePlayerMemoryAsync(memory, expectedVersion, ct);
        }

        public async Task<PlayerLongTermMemory> DeleteFactAsync(string gameId, string npcId,
            string playerId, string factId, int expectedVersion, CancellationToken ct)
        {
            PlayerLongTermMemory memory = await LoadExpectedAsync(gameId, npcId, playerId,
                expectedVersion, ct);
            int removed = memory.facts.RemoveAll(f => f != null
                && string.Equals(f.id, factId, StringComparison.Ordinal));
            if (removed == 0) throw new KeyNotFoundException("fact not found: " + factId);
            return await _repository.SavePlayerMemoryAsync(memory, expectedVersion, ct);
        }

        /// <summary>将 v1 session 中的摘要/字符串事实幂等迁移到玩家长期记忆。</summary>
        public async Task<PlayerLongTermMemory> LoadAndMigrateAsync(SessionState session, int maxFacts,
            CancellationToken ct)
        {
            PlayerLongTermMemory current = await LoadAsync(session.GameId, session.NpcId,
                session.PlayerId, ct);
            if (string.IsNullOrWhiteSpace(session.Summary)
                && (session.Facts == null || session.Facts.Count == 0)) return current;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                DateTime now = DateTime.UtcNow;
                var migrated = (session.Facts ?? new List<string>())
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => MemoryFactMerger.FromLegacyString(f, now, session.SessionId));
                current.summary = MergeLegacySummary(current.summary, session.Summary);
                current.facts = MemoryFactMerger.Merge(current.facts, migrated, maxFacts, now);
                try
                {
                    current = await _repository.SavePlayerMemoryAsync(current,
                        current.memoryVersion, ct);
                    session.Summary = null;
                    session.Facts = new List<string>();
                    return current;
                }
                catch (MemoryVersionConflictException) when (attempt < 3)
                {
                    current = await LoadAsync(session.GameId, session.NpcId, session.PlayerId, ct);
                }
            }
            return current;
        }

        /// <summary>同步摘要兼容路径：把 AgentLoop 的字符串事实写入玩家长期记忆。</summary>
        public async Task<PlayerLongTermMemory> SaveLegacySummaryAsync(string gameId, string npcId,
            string playerId, string sessionId, string summary, IEnumerable<string> facts,
            int maxFacts, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                PlayerLongTermMemory current = await LoadAsync(gameId, npcId, playerId, ct);
                DateTime now = DateTime.UtcNow;
                current.summary = summary ?? current.summary;
                current.facts = MemoryFactMerger.Merge(current.facts,
                    (facts ?? Array.Empty<string>()).Select(f =>
                        MemoryFactMerger.FromLegacyString(f, now, sessionId)), maxFacts, now);
                current.lastSummarizedUtc = now;
                try
                {
                    return await _repository.SavePlayerMemoryAsync(current,
                        current.memoryVersion, ct);
                }
                catch (MemoryVersionConflictException) when (attempt < 3) { }
            }
            throw new MemoryVersionConflictException(-1, -1);
        }

        /// <summary>保存一次结构化摘要。版本冲突由调用方重新加载并重新摘要。</summary>
        public Task<PlayerLongTermMemory> SaveStructuredAsync(PlayerLongTermMemory existing,
            PlayerMemorySummaryResult result, int maxFacts, CancellationToken ct)
        {
            DateTime now = DateTime.UtcNow;
            existing.summary = result.Summary;
            existing.facts = MemoryFactMerger.Merge(existing.facts, result.Facts, maxFacts, now);
            existing.lastSummarizedUtc = now;
            return _repository.SavePlayerMemoryAsync(existing, existing.memoryVersion, ct);
        }

        public static List<string> ToPromptFacts(PlayerLongTermMemory memory, MemoryPolicy policy)
        {
            if (memory?.facts == null) return new List<string>();
            DateTime now = DateTime.UtcNow;
            return memory.facts
                .Where(f => f != null && (!f.expiresUtc.HasValue || f.expiresUtc.Value > now))
                .Where(f => IsCategoryEnabled(f.category, policy))
                .Select(FormatFact)
                .ToList();
        }

        public static bool IsCategoryEnabled(string category, MemoryPolicy policy)
        {
            if (policy == null) return true;
            switch (category)
            {
                case "player_profile": return policy.rememberPlayerProfile;
                case "promise": return policy.rememberPromises;
                case "quest": return policy.rememberQuestEvents;
                case "casual": return policy.rememberCasualChat;
                default: return true;
            }
        }

        private static string FormatFact(MemoryFact fact)
        {
            string label;
            switch (fact.category)
            {
                case "player_profile": label = "玩家档案"; break;
                case "promise": label = "承诺"; break;
                case "quest": label = "剧情"; break;
                case "relationship": label = "关系"; break;
                default: label = "其他"; break;
            }
            return "[" + label + "] " + fact.value;
        }

        private static string MergeLegacySummary(string current, string legacy)
        {
            if (string.IsNullOrWhiteSpace(legacy)) return current;
            if (string.IsNullOrWhiteSpace(current)) return legacy;
            if (current.IndexOf(legacy, StringComparison.Ordinal) >= 0) return current;
            return current + "\n" + legacy;
        }

        private async Task<PlayerLongTermMemory> LoadExpectedAsync(string gameId, string npcId,
            string playerId, int expectedVersion, CancellationToken ct)
        {
            PlayerLongTermMemory memory = await LoadAsync(gameId, npcId, playerId, ct);
            if (memory.memoryVersion != expectedVersion)
                throw new MemoryVersionConflictException(expectedVersion, memory.memoryVersion);
            memory.facts = memory.facts ?? new List<MemoryFact>();
            return memory;
        }

        private static MemoryFact NormalizeManualFact(MemoryFact source, MemoryFact existing)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.value))
                throw new MemoryValidationException("fact.value 必填");
            DateTime now = DateTime.UtcNow;
            return new MemoryFact
            {
                id = string.IsNullOrWhiteSpace(source.id)
                    ? (existing?.id ?? "fact-" + Guid.NewGuid().ToString("N")) : source.id.Trim(),
                category = string.IsNullOrWhiteSpace(source.category) ? "general" : source.category.Trim(),
                key = string.IsNullOrWhiteSpace(source.key) ? null : source.key.Trim(),
                value = source.value.Trim(),
                confidence = Math.Max(0f, Math.Min(1f, source.confidence)),
                source = "admin",
                sourceSessionId = source.sourceSessionId,
                createdUtc = existing?.createdUtc ?? now,
                updatedUtc = now,
                pinned = source.pinned,
                expiresUtc = source.expiresUtc
            };
        }
    }
}
