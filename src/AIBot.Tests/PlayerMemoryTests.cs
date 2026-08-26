using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    public sealed class PlayerMemoryTests
    {
        [Fact]
        public void SameKey_UpdatesValueAndPreservesIdentity()
        {
            DateTime now = DateTime.UtcNow;
            var oldFact = Fact("fact-1", "player.name", "小明", 0.8f, now.AddDays(-1));
            var newFact = Fact(null, "player.name", "小王", 0.9f, now);

            List<MemoryFact> merged = MemoryFactMerger.Merge(
                new[] { oldFact }, new[] { newFact }, 10, now);

            Assert.Single(merged);
            Assert.Equal("fact-1", merged[0].id);
            Assert.Equal("小王", merged[0].value);
            Assert.Equal(oldFact.createdUtc, merged[0].createdUtc);
        }

        [Fact]
        public void PinnedFact_CannotBeOverwrittenByModel()
        {
            DateTime now = DateTime.UtcNow;
            MemoryFact pinned = Fact("fixed", "player.name", "小明", 0.5f, now);
            pinned.pinned = true;

            List<MemoryFact> merged = MemoryFactMerger.Merge(new[] { pinned },
                new[] { Fact(null, "player.name", "小王", 1f, now) }, 10, now);

            Assert.Equal("小明", Assert.Single(merged).value);
            Assert.True(merged[0].pinned);
        }

        [Fact]
        public void LowerConfidenceConflict_KeepsExistingFact()
        {
            DateTime now = DateTime.UtcNow;
            List<MemoryFact> merged = MemoryFactMerger.Merge(
                new[] { Fact("old", "player.job", "铁匠", 0.9f, now) },
                new[] { Fact(null, "player.job", "商人", 0.4f, now.AddMinutes(1)) }, 10, now);

            Assert.Equal("铁匠", Assert.Single(merged).value);
        }

        [Fact]
        public void ExpiredFacts_AreRemovedAndLimitIsApplied()
        {
            DateTime now = DateTime.UtcNow;
            MemoryFact expired = Fact("expired", "old", "旧消息", 1f, now.AddDays(-2));
            expired.expiresUtc = now.AddSeconds(-1);
            MemoryFact pinned = Fact("pinned", "pin", "固定", 0.1f, now.AddDays(-2));
            pinned.pinned = true;

            List<MemoryFact> merged = MemoryFactMerger.Merge(new[] { expired, pinned }, new[]
            {
                Fact("new-1", "one", "一", 0.8f, now),
                Fact("new-2", "two", "二", 0.9f, now.AddSeconds(1))
            }, 2, now);

            Assert.Equal(2, merged.Count);
            Assert.DoesNotContain(merged, f => f.id == "expired");
            Assert.Contains(merged, f => f.id == "pinned");
        }

        [Fact]
        public async Task StructuredSummarizer_ParsesObjectAndLegacyFacts()
        {
            var backend = new MockLlmBackend(Sse.Round(Sse.Token(
                "{\"summary\":\"玩家答应调查矿洞\",\"facts\":[" +
                "{\"category\":\"promise\",\"key\":\"promise.mine\",\"value\":\"调查矿洞\",\"confidence\":0.95}," +
                "\"玩家喜欢苹果\"]}")));

            PlayerMemorySummaryResult result = await MemorySummarizer.RunStructuredAsync(
                backend, new ModelSettings(), new PlayerLongTermMemory(),
                new List<LlmMessage> { LlmMessage.User("我会调查矿洞") },
                5, "session-1", null, CancellationToken.None);

            Assert.Equal("玩家答应调查矿洞", result.Summary);
            Assert.Equal(2, result.Facts.Count);
            Assert.Equal("promise.mine", result.Facts[0].key);
            Assert.Equal("legacy", result.Facts[1].category);
            Assert.All(result.Facts, fact => Assert.Equal("session-1", fact.sourceSessionId));
        }

        [Fact]
        public async Task BackgroundPolicy_WithoutHostDeferral_SummarizesSynchronously()
        {
            var cfg = new AgentConfigDto { npcId = "background", displayName = "t", persona = "p" };
            var memory = new ShortTermMemory(2);
            memory.Add(LlmMessage.User("旧问题"));
            memory.Add(LlmMessage.Assistant("旧回答"));
            var backend = new MockLlmBackend(
                Sse.Round(Sse.Token(
                    "{\"say\":\"新回答\",\"emotion\":\"neutral\",\"action\":\"idle\"}")),
                Sse.Round(Sse.Token("{\"summary\":\"旧对话摘要\",\"facts\":[]}")));
            MemoryPolicy policy = MemoryPolicy.Defaults();
            policy.summaryThreshold = 1;
            policy.backgroundSummarization = true;

            AgentLoopResult result = await new AgentLoop(backend).RunAsync(new AgentRunInput
            {
                Config = cfg,
                World = new WorldConfigDto(),
                Game = new SimGameContext(new SimGameState()),
                UserMessage = "新问题",
                Memory = memory,
                ResolvedMemoryPolicy = policy
            }, new RecordingSink(), CancellationToken.None);

            Assert.Equal("旧对话摘要", result.MemorySummary);
            Assert.Equal(2, backend.Requests.Count);
            Assert.Equal(0, memory.EvictedCount);
        }

        [Fact]
        public async Task BackgroundPolicy_WithHostDeferral_PreservesPendingBatch()
        {
            var cfg = new AgentConfigDto { npcId = "background-host", displayName = "t", persona = "p" };
            var memory = new ShortTermMemory(2);
            memory.Add(LlmMessage.User("旧问题"));
            memory.Add(LlmMessage.Assistant("旧回答"));
            var backend = new MockLlmBackend(Sse.Round(Sse.Token(
                "{\"say\":\"新回答\",\"emotion\":\"neutral\",\"action\":\"idle\"}")));
            MemoryPolicy policy = MemoryPolicy.Defaults();
            policy.memoryScope = MemoryPolicyValues.ScopePlayerNpc;
            policy.summaryThreshold = 1;
            policy.backgroundSummarization = true;

            AgentLoopResult result = await new AgentLoop(backend).RunAsync(new AgentRunInput
            {
                Config = cfg,
                World = new WorldConfigDto(),
                Game = new SimGameContext(new SimGameState()),
                UserMessage = "新问题",
                Memory = memory,
                ResolvedMemoryPolicy = policy,
                DeferMemorySummarizationToHost = true
            }, new RecordingSink(), CancellationToken.None);

            Assert.Null(result.MemorySummary);
            Assert.Single(backend.Requests);
            Assert.True(memory.EvictedCount >= 2);
        }

        [Fact]
        public async Task DisabledSessionSummary_DropsWindowOverflow()
        {
            var cfg = new AgentConfigDto { npcId = "disabled-summary", displayName = "t", persona = "p" };
            var memory = new ShortTermMemory(2);
            memory.Add(LlmMessage.User("旧问题"));
            memory.Add(LlmMessage.Assistant("旧回答"));
            var backend = new MockLlmBackend(Sse.Round(Sse.Token(
                "{\"say\":\"新回答\",\"emotion\":\"neutral\",\"action\":\"idle\"}")));
            MemoryPolicy policy = MemoryPolicy.Defaults();
            policy.memoryScope = MemoryPolicyValues.ScopeSession;
            policy.summaryThreshold = 0;

            await new AgentLoop(backend).RunAsync(new AgentRunInput
            {
                Config = cfg,
                World = new WorldConfigDto(),
                Game = new SimGameContext(new SimGameState()),
                UserMessage = "新问题",
                Memory = memory,
                ResolvedMemoryPolicy = policy
            }, new RecordingSink(), CancellationToken.None);

            Assert.Single(backend.Requests);
            Assert.Equal(0, memory.EvictedCount);
        }

        [Fact]
        public async Task DisabledPlayerAutoSummary_KeepsOnlyBoundedManualBacklog()
        {
            var cfg = new AgentConfigDto { npcId = "manual-backlog", displayName = "t", persona = "p" };
            var memory = new ShortTermMemory(2);
            for (int i = 0; i < 10; i++) memory.Add(LlmMessage.User("旧消息" + i));
            var backend = new MockLlmBackend(Sse.Round(Sse.Token(
                "{\"say\":\"新回答\",\"emotion\":\"neutral\",\"action\":\"idle\"}")));
            MemoryPolicy policy = MemoryPolicy.Defaults();
            policy.memoryScope = MemoryPolicyValues.ScopePlayerNpc;
            policy.shortTermTurns = 1;
            policy.summaryThreshold = 0;

            await new AgentLoop(backend).RunAsync(new AgentRunInput
            {
                Config = cfg,
                World = new WorldConfigDto(),
                Game = new SimGameContext(new SimGameState()),
                UserMessage = "新问题",
                Memory = memory,
                ResolvedMemoryPolicy = policy,
                DeferMemorySummarizationToHost = true
            }, new RecordingSink(), CancellationToken.None);

            Assert.Equal(2, memory.EvictedCount);
            Assert.Contains("旧消息8", memory.SnapshotEvicted()[0].Content);
        }

        [Fact]
        public void Resize_ShrinksActiveWindowAndPreservesEvictedOrder()
        {
            var memory = new ShortTermMemory(6);
            memory.Add(LlmMessage.User("一"));
            memory.Add(LlmMessage.Assistant("二"));
            memory.Add(LlmMessage.User("三"));
            memory.Add(LlmMessage.Assistant("四"));

            memory.Resize(2);

            Assert.Equal(2, memory.Capacity);
            Assert.Equal(new[] { "三", "四" }, new[]
            {
                memory.Messages[0].Content, memory.Messages[1].Content
            });
            Assert.Equal(new[] { "一", "二" }, memory.SnapshotEvicted().ConvertAll(x => x.Content));
        }

        [Fact]
        public async Task CategoryPolicy_IsIncludedInStructuredSummaryContract()
        {
            var backend = new MockLlmBackend(Sse.Round(Sse.Token(
                "{\"summary\":\"只保留关系信息\",\"facts\":[]}")));
            MemoryPolicy policy = MemoryPolicy.Defaults();
            policy.rememberPlayerProfile = false;
            policy.rememberPromises = false;
            policy.rememberQuestEvents = false;
            policy.rememberCasualChat = false;

            await MemorySummarizer.RunStructuredAsync(backend, new ModelSettings(),
                new PlayerLongTermMemory(), new List<LlmMessage> { LlmMessage.User("旧消息") },
                8, "session", null, CancellationToken.None, policy);

            string prompt = backend.Requests[0].Messages[0].Content;
            Assert.Contains("禁止在summary和facts中保留", prompt);
            Assert.Contains("player_profile", prompt);
            Assert.Contains("promise", prompt);
            Assert.Contains("quest", prompt);
            Assert.Contains("casual", prompt);
        }

        [Fact]
        public async Task JsonRepository_UsesOptimisticVersionAndPlayerNpcPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "aibot-memory-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var repository = new JsonMemoryRepository(() => root);
                PlayerLongTermMemory memory = await repository.LoadPlayerMemoryAsync(
                    "game", "npc", "player-1", CancellationToken.None);
                Assert.Equal(0, memory.memoryVersion);
                memory.summary = "记得这个玩家";
                PlayerLongTermMemory saved = await repository.SavePlayerMemoryAsync(
                    memory, 0, CancellationToken.None);

                Assert.Equal(1, saved.memoryVersion);
                Assert.True(File.Exists(Path.Combine(root, "games", "game", "memories", "npc", "player-1.json")));
                MemoryListPage page = await repository.ListPlayerMemoriesAsync(
                    "game", null, null, 50, 0, CancellationToken.None);
                Assert.Single(page.items);
                Assert.Equal("player-1", page.items[0].playerId);
                await Assert.ThrowsAsync<MemoryVersionConflictException>(() =>
                    repository.SavePlayerMemoryAsync(memory, 0, CancellationToken.None));
                await Assert.ThrowsAsync<MemoryVersionConflictException>(() =>
                    repository.DeletePlayerMemoryAsync("game", "npc", "player-1", 0,
                        CancellationToken.None));
                await repository.DeletePlayerMemoryAsync("game", "npc", "player-1", 1,
                    CancellationToken.None);
                Assert.Empty((await repository.ListPlayerMemoriesAsync(
                    "game", null, null, 50, 0, CancellationToken.None)).items);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task LegacySessionMigration_IsIdempotentAndClearsOnlyAfterSave()
        {
            string root = Path.Combine(Path.GetTempPath(), "aibot-memory-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var service = new PlayerMemoryService(new JsonMemoryRepository(() => root));
                var session = new SessionState
                {
                    GameId = "game",
                    NpcId = "npc",
                    PlayerId = "player-1",
                    SessionId = "session-1",
                    Memory = new ShortTermMemory(4),
                    Summary = "玩家曾来过",
                    Facts = new List<string> { "玩家名叫小明" }
                };

                PlayerLongTermMemory first = await service.LoadAndMigrateAsync(session, 8, CancellationToken.None);
                Assert.Null(session.Summary);
                Assert.Empty(session.Facts);
                Assert.Single(first.facts);

                session.Summary = "玩家曾来过";
                session.Facts.Add("玩家名叫小明");
                PlayerLongTermMemory second = await service.LoadAndMigrateAsync(session, 8, CancellationToken.None);
                Assert.Equal("玩家曾来过", second.summary);
                Assert.Single(second.facts);
                Assert.Equal(2, second.memoryVersion);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task ManualFactCrud_UsesVersionAndPreservesFactIdentity()
        {
            string root = Path.Combine(Path.GetTempPath(), "aibot-memory-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var service = new PlayerMemoryService(new JsonMemoryRepository(() => root));
                PlayerLongTermMemory created = await service.AddFactAsync("game", "npc", "player-1",
                    new MemoryFact
                    {
                        category = "player_profile",
                        key = "player.name",
                        value = "小明",
                        confidence = 1f
                    }, 0, 8, CancellationToken.None);
                string factId = Assert.Single(created.facts).id;
                Assert.Equal(1, created.memoryVersion);

                PlayerLongTermMemory updated = await service.UpdateFactAsync("game", "npc", "player-1",
                    factId, new MemoryFact
                    {
                        category = "player_profile",
                        key = "player.name",
                        value = "小王",
                        confidence = 0.9f,
                        pinned = true
                    }, 1, CancellationToken.None);
                Assert.Equal(factId, Assert.Single(updated.facts).id);
                Assert.True(updated.facts[0].pinned);
                Assert.Equal("admin", updated.facts[0].source);

                MemoryVersionConflictException conflict = await Assert.ThrowsAsync<MemoryVersionConflictException>(
                    () => service.UpdateSummaryAsync("game", "npc", "player-1", "旧版本修改", 1,
                        CancellationToken.None));
                Assert.Equal(2, conflict.ActualVersion);

                PlayerLongTermMemory deleted = await service.DeleteFactAsync("game", "npc", "player-1",
                    factId, 2, CancellationToken.None);
                Assert.Empty(deleted.facts);
                Assert.Equal(3, deleted.memoryVersion);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public void MemoryAudit_IsPersistedAndFilterable()
        {
            string root = Path.Combine(Path.GetTempPath(), "aibot-audit-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var audit = new MemoryAuditService(() => root);
                Assert.True(audit.Record(new MemoryAuditEntry
                {
                    gameId = "game",
                    npcId = "npc",
                    playerId = "player-1",
                    actor = "tester",
                    action = "memory.fact.create",
                    before = Newtonsoft.Json.Linq.JValue.CreateNull(),
                    after = Newtonsoft.Json.Linq.JObject.FromObject(new { value = "事实" })
                }));

                Newtonsoft.Json.Linq.JObject result = audit.Query("game", "npc", "player-1",
                    "memory.fact.create", null, 50, 0);
                Assert.Equal(1, (int)result["total"]);
                Assert.Equal("tester", result["items"][0]["actor"].ToString());
                Assert.Equal("事实", result["items"][0]["after"]["value"].ToString());
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public void RequiredAudit_ThrowsWhenDataRootIsUnavailable()
        {
            var audit = new MemoryAuditService(() => null);

            MemoryAuditWriteException error = Assert.Throws<MemoryAuditWriteException>(() =>
                audit.RecordRequired(new MemoryAuditEntry
                {
                    gameId = "game",
                    action = "memory.test"
                }));

            Assert.Contains("不能被视为已完整提交", error.Message);
        }

        [Fact]
        public async Task ClearPlayerMemory_RemovesActiveAndPendingSessionMemory()
        {
            string gameId = "test_" + Guid.NewGuid().ToString("N");
            string npcId = "npc";
            string playerId = "player";
            string sessionId = "session";
            string root = DataStore.FindDataRoot();
            Assert.False(string.IsNullOrEmpty(root));
            try
            {
                SessionState session = SessionStore.GetOrCreate(gameId, npcId, playerId, sessionId, 1);
                session.Memory.Add(LlmMessage.User("旧问题"));
                session.Memory.Add(LlmMessage.Assistant("旧回答"));
                session.Memory.Add(LlmMessage.User("新问题"));
                session.Summary = "旧摘要";
                session.Facts.Add("旧事实");
                Assert.True(SessionStore.Save(session));
                Assert.True(session.Memory.EvictedCount > 0);

                Assert.True(await SessionStore.ClearPlayerMemoryAsync(gameId, npcId, playerId,
                    CancellationToken.None));

                Assert.Empty(session.Memory.Messages);
                Assert.Equal(0, session.Memory.EvictedCount);
                Assert.Null(session.Summary);
                Assert.Empty(session.Facts);
            }
            finally
            {
                SessionStore.Delete(gameId, npcId, playerId, sessionId);
                string gameDirectory = Path.Combine(root, "games", gameId);
                if (Directory.Exists(gameDirectory)) Directory.Delete(gameDirectory, true);
            }
        }

        [Fact]
        public async Task RetentionScan_ReadsOldestPageInsteadOfNewestPage()
        {
            DateTime now = DateTime.UtcNow;
            var repository = new RetentionRepository(Enumerable.Range(0, 600)
                .Select(i => new MemoryListItem
                {
                    gameId = "game",
                    npcId = "npc",
                    playerId = "player-" + i,
                    updatedUtc = now.AddDays(-i)
                }).ToList());
            var service = new PlayerMemoryService(repository);

            MemoryRetentionScan result = await service.FindRetentionCandidatesAsync("game",
                now.AddDays(-100), 2, CancellationToken.None);

            Assert.Equal(600, result.totalMemoryCount);
            Assert.Equal(new[] { "player-599", "player-598" },
                result.candidates.Select(x => x.playerId));
            Assert.True(result.hasMoreCandidates);
            Assert.Equal(new[] { 0, 597 }, repository.Offsets);
        }

        [Fact]
        public void SessionDeleteFailure_PreservesCachedAndPersistedState()
        {
            string gameId = "test_" + Guid.NewGuid().ToString("N");
            string npcId = "npc";
            string playerId = "player";
            string sessionId = "session";
            string root = DataStore.FindDataRoot();
            string file = Path.Combine(root, "games", gameId, "sessions", npcId, playerId,
                sessionId + ".json");
            try
            {
                SessionState session = SessionStore.GetOrCreate(gameId, npcId, playerId, sessionId, 2);
                session.Memory.Add(LlmMessage.User("保留我"));
                Assert.True(SessionStore.Save(session));
                File.SetAttributes(file, FileAttributes.ReadOnly);

                Assert.Throws<IOException>(() =>
                    SessionStore.Delete(gameId, npcId, playerId, sessionId));
                Assert.True(File.Exists(file));
                Assert.Contains(SessionStore.ListByGame(gameId, npcId, playerId),
                    item => item.SessionId == sessionId && item.Memory.Messages.Count == 1);
            }
            finally
            {
                if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
                SessionStore.Delete(gameId, npcId, playerId, sessionId);
                string gameDirectory = Path.Combine(root, "games", gameId);
                if (Directory.Exists(gameDirectory)) Directory.Delete(gameDirectory, true);
            }
        }

        private sealed class RetentionRepository : IMemoryRepository
        {
            private readonly List<MemoryListItem> _items;
            public readonly List<int> Offsets = new List<int>();

            public RetentionRepository(List<MemoryListItem> items) { _items = items; }

            public Task<MemoryListPage> ListPlayerMemoriesAsync(string gameId, string npcId,
                string playerId, int limit, int offset, CancellationToken ct)
            {
                Offsets.Add(offset);
                return Task.FromResult(new MemoryListPage
                {
                    total = _items.Count,
                    limit = limit,
                    offset = offset,
                    items = _items.Skip(offset).Take(limit).ToList()
                });
            }

            public Task<PlayerLongTermMemory> LoadPlayerMemoryAsync(string gameId, string npcId,
                string playerId, CancellationToken ct) { throw new NotSupportedException(); }
            public Task<PlayerLongTermMemory> SavePlayerMemoryAsync(PlayerLongTermMemory memory,
                int expectedVersion, CancellationToken ct) { throw new NotSupportedException(); }
            public Task DeletePlayerMemoryAsync(string gameId, string npcId, string playerId,
                int? expectedVersion, CancellationToken ct) { throw new NotSupportedException(); }
        }

        private static MemoryFact Fact(string id, string key, string value, float confidence, DateTime updated)
        {
            return new MemoryFact
            {
                id = id,
                category = "general",
                key = key,
                value = value,
                confidence = confidence,
                createdUtc = updated,
                updatedUtc = updated
            };
        }
    }
}
