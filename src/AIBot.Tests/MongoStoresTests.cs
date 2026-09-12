using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Server;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>P1 的 Mongo 持久化实现集成用例（opt-in：需 AIBOT_MONGO_TEST_CONNECTION）。</summary>
    [Collection("mongo-integration")]
    public class MongoStoresTests
    {
        private const string GameId = "zz_mongo_stores_game";

        private static MongoConnectionFactory NewFactory()
        {
            return new MongoConnectionFactory(
                Environment.GetEnvironmentVariable(MongoFactAttribute.EnvVar) ?? "mongodb://127.0.0.1:27017",
                "ai_npc_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        // ---- 审计 store ----

        [MongoFact]
        public void Audit_SameId_WrittenOnce()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoMemoryAuditStore(factory);
            var entry = new MemoryAuditEntry
            {
                id = "audit-fixed-1", gameId = GameId, action = "memory.test",
                actor = "tester", before = JValue.CreateNull(), after = JValue.CreateNull()
            };

            store.Write(entry);
            store.Write(entry);   // 重复写应幂等
            store.Write(entry);

            JObject result = store.Query(GameId, null, null, "memory.test",
                DateTime.UtcNow.ToString("yyyy-MM-dd"), 50, 0);
            Assert.Equal(1, (int)result["total"]);
        }

        [MongoFact]
        public void Audit_BeforeAfterShapes_RoundTrip()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoMemoryAuditStore(factory);
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");

            store.Write(new MemoryAuditEntry
            {
                id = "audit-null", gameId = GameId, action = "a.null",
                before = JValue.CreateNull(), after = JValue.CreateNull()
            });
            store.Write(new MemoryAuditEntry
            {
                id = "audit-scalar", gameId = GameId, action = "a.scalar",
                before = new JValue("text"), after = new JValue(42)
            });
            store.Write(new MemoryAuditEntry
            {
                id = "audit-object", gameId = GameId, action = "a.object",
                before = JObject.Parse("{\"value\":\"事实\"}"), after = JValue.CreateNull()
            });

            JObject nulls = store.Query(GameId, null, null, "a.null", date, 50, 0);
            Assert.Equal(JTokenType.Null, ((JToken)nulls["items"][0]["before"]).Type);

            JObject scalar = store.Query(GameId, null, null, "a.scalar", date, 50, 0);
            Assert.Equal("text", scalar["items"][0]["before"].ToString());
            Assert.Equal("42", scalar["items"][0]["after"].ToString());

            JObject obj = store.Query(GameId, null, null, "a.object", date, 50, 0);
            Assert.Equal("事实", obj["items"][0]["before"]["value"].ToString());
        }

        [MongoFact]
        public void Audit_Ts_IsStoredAsBsonDate_AndReadBackIso()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoMemoryAuditStore(factory);
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            store.Write(new MemoryAuditEntry { id = "audit-ts", gameId = GameId, action = "a.ts" });

            // 直接读原始文档确认落成 BSON Date（TTL 依赖这一类型）
            var raw = factory.Collection<MongoDB.Bson.BsonDocument>("memory_audits")
                .Find(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "audit-ts"))
                .First();
            Assert.Equal(MongoDB.Bson.BsonType.DateTime, raw["ts"].BsonType);

            JObject result = store.Query(GameId, null, null, "a.ts", date, 50, 0);
            Assert.True(DateTime.TryParse(result["items"][0]["ts"].ToString(), out _));
        }

        [MongoFact]
        public void Audit_Int64AndNestedValues_RoundTrip()
        {
            // 走 JSON 后端的数据可能是任意 JToken：大整数会变成 BsonInt64。
            // 若用 BsonValue.ToJson()（Shell 模式）回读会得到 NumberLong(...)，Newtonsoft 解析直接失败。
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoMemoryAuditStore(factory);
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");

            store.Write(new MemoryAuditEntry
            {
                id = "audit-long",
                gameId = GameId,
                action = "a.long",
                before = new JObject
                {
                    ["big"] = new JValue(99999999999L),
                    ["nested"] = new JArray { new JObject { ["k"] = new JValue(1L) } }
                },
                after = new JValue(1234567890123L),
                metadata = new JObject { ["elapsed"] = new JValue(9876543210L), ["tag"] = new JValue("x") }
            });

            JObject result = store.Query(GameId, null, null, "a.long", date, 50, 0);
            Assert.Equal(1, (int)result["total"]);
            Assert.Equal(99999999999L, (long)result["items"][0]["before"]["big"]);
            Assert.Equal(1L, (long)result["items"][0]["before"]["nested"][0]["k"]);
            Assert.Equal(1234567890123L, (long)result["items"][0]["after"]);
            Assert.Equal(9876543210L, (long)result["items"][0]["metadata"]["elapsed"]);
        }

        // ---- 对话日志 store ----

        [MongoFact]
        public void ChatLog_WriteAndQuery_WithNpcFilterAndPaging()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoChatLogStore(factory);
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");

            for (int i = 0; i < 3; i++)
                store.Write(GameId, new ChatLogService.ChatLogEntry
                { npcId = "lin", sessionId = "s" + i, say = "hi" + i, tools = new List<string> { "t" + i } });
            store.Write(GameId, new ChatLogService.ChatLogEntry { npcId = "wang", sessionId = "s9", say = "yo" });

            JObject all = store.Query(GameId, date, null, 50, 0);
            Assert.Equal(4, (int)all["total"]);

            JObject filtered = store.Query(GameId, date, "lin", 50, 0);
            Assert.Equal(3, (int)filtered["total"]);

            JObject page = store.Query(GameId, date, "lin", 2, 0);
            Assert.Equal(3, (int)page["total"]);
            Assert.Equal(2, ((JArray)page["items"]).Count);
            // 同一毫秒内写入的 ts 相同，排序对相等值不保证稳定，故只断言工具字段随条目带出。
            var toolsSeen = new List<string>();
            foreach (JToken item in (JArray)page["items"]) toolsSeen.Add(item["tools"][0].ToString());
            Assert.All(toolsSeen, t => Assert.Contains(t, new[] { "t0", "t1", "t2" }));

            var raw = factory.Collection<MongoDB.Bson.BsonDocument>("chat_logs")
                .Find(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("sessionId", "s0"))
                .First();
            Assert.Equal(MongoDB.Bson.BsonType.DateTime, raw["ts"].BsonType);
        }

        // ---- 摘要任务 store ----

        [MongoFact]
        public void SummaryJob_StatusTransitions_AndRecovery()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoMemorySummaryJobPersistence(factory);
            var job = new MemorySummaryJob
            {
                GameId = GameId, NpcId = "lin", PlayerId = "p1", SessionId = "s1",
                Force = false, Actor = "system", Generation = 1
            };
            string key = "zz|lin|p1|s1|g1";

            store.UpsertPending(job, key);
            Assert.Single(store.LoadRecoverable());

            store.MarkProcessing(key);
            Assert.Equal(1, store.LoadRecoverable()[0].Attempts);

            store.MarkFailed(key, "boom");
            Assert.Empty(store.LoadRecoverable());
            List<MemorySummaryJobRecord> failed = store.LoadFailed();
            Assert.Single(failed);
            Assert.Equal("boom", failed[0].LastError);

            store.UpsertPending(job, key);   // 重置为 pending
            Assert.Single(store.LoadRecoverable());

            store.MarkSucceeded(key);
            Assert.Empty(store.LoadRecoverable());
            Assert.Empty(store.LoadFailed());

            store.UpsertPending(job, key);
            store.DeleteForPlayer(GameId, "lin", "p1");
            Assert.Empty(store.LoadRecoverable());
        }

        // ---- 会话持久化 ----

        [MongoFact]
        public void Session_RoundTrip_PreservesPayloadAndNormalizesPlayerKey()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoSessionPersistence(factory);
            var dto = new SessionStore.SessionFileDto
            {
                npcId = "lin", playerId = "p1", sessionId = "s1",
                simState = new AIBot.Core.Context.SimGameState { stage = 3, favorability = 7 },
                messages = new List<AIBot.Core.Llm.LlmMessage> { AIBot.Core.Llm.LlmMessage.User("你好") },
                evictedMessages = new List<AIBot.Core.Llm.LlmMessage> { AIBot.Core.Llm.LlmMessage.User("evicted") },
                pendingToolRound = new PendingToolRound { roundToken = "req#0", roundIndex = 1 }
            };
            SessionSaveResult saved = store.Save(GameId, dto);
            Assert.True(saved.Persisted);

            SessionStore.SessionFileDto loaded = store.Load(GameId, "lin", "p1", "s1");
            Assert.NotNull(loaded);
            Assert.Equal(3, loaded.simState.stage);
            Assert.Single(loaded.messages);
            Assert.Single(loaded.evictedMessages);
            Assert.Equal("req#0", loaded.pendingToolRound.roundToken);

            // legacy（playerId=null）写成 playerKey=""，读回必须还原为 null
            store.Save(GameId, new SessionStore.SessionFileDto { npcId = "lin", playerId = null, sessionId = "legacy" });
            SessionStore.SessionFileDto legacy = store.Load(GameId, "lin", null, "legacy");
            Assert.NotNull(legacy);
            Assert.Null(legacy.playerId);
        }

        [MongoFact]
        public void Session_ScanPending_ExcludesEmptyPlayerKey()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoSessionPersistence(factory);
            var evicted = new List<AIBot.Core.Llm.LlmMessage> { AIBot.Core.Llm.LlmMessage.User("e") };

            store.Save(GameId, new SessionStore.SessionFileDto
            { npcId = "lin", playerId = "p1", sessionId = "s1", evictedMessages = evicted });
            store.Save(GameId, new SessionStore.SessionFileDto
            { npcId = "lin", playerId = null, sessionId = "legacy", evictedMessages = evicted }); // 应被排除
            store.Save(GameId, new SessionStore.SessionFileDto
            { npcId = "lin", playerId = "p2", sessionId = "s2" });                                 // 无待摘要

            List<PendingMemorySession> pending = store.ScanPending()
                .Where(x => x.GameId == GameId).ToList();
            Assert.Single(pending);
            Assert.Equal("p1", pending[0].PlayerId);
        }

        [MongoFact]
        public void Session_DeleteExpired_ProtectsPendingAndProcessing()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            var store = new MongoSessionPersistence(factory);
            DateTime stale = DateTime.UtcNow.AddDays(-10);

            store.Save(GameId, new SessionStore.SessionFileDto
            { npcId = "lin", playerId = "stale", sessionId = "s1", lastActiveUtc = stale });        // 应删
            store.Save(GameId, new SessionStore.SessionFileDto
            { npcId = "lin", playerId = "active", sessionId = "s2", lastActiveUtc = stale });       // 活跃键 → 保护
            store.Save(GameId, new SessionStore.SessionFileDto
            {
                npcId = "lin", playerId = "pending", sessionId = "s3", lastActiveUtc = stale,
                evictedMessages = new List<AIBot.Core.Llm.LlmMessage> { AIBot.Core.Llm.LlmMessage.User("e") }
            });                                                                                      // 待摘要 → 保护
            store.Save(GameId, new SessionStore.SessionFileDto
            {
                npcId = "lin", playerId = "processing", sessionId = "s4", lastActiveUtc = stale,
                recentRequests = new List<ChatRequestRecord> { new ChatRequestRecord
                    { requestId = "req-1", status = ChatRequestStatuses.Processing } }
            });                                                                                      // processing → 保护

            var activeKeys = new HashSet<string>(StringComparer.Ordinal)
            { SessionStore.IdentityKey(GameId, "lin", "active", "s2") };
            int removed = store.DeleteExpired(TimeSpan.FromDays(1), activeKeys, new HashSet<string>());

            Assert.Equal(1, removed);
            Assert.Null(store.Load(GameId, "lin", "stale", "s1"));
            Assert.NotNull(store.Load(GameId, "lin", "active", "s2"));
            Assert.NotNull(store.Load(GameId, "lin", "pending", "s3"));
            Assert.NotNull(store.Load(GameId, "lin", "processing", "s4"));
        }

        [MongoFact]
        public void Facade_WithMongoPersistence_SaveAndScanPending()
        {
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            try
            {
                SessionStore.UsePersistence(new MongoSessionPersistence(factory));
                // maxTurns=1 → 容量 2；加入 4 条会产生待摘要（evicted）消息。
                SessionState session = SessionStore.GetOrCreate(GameId, "lin", "p1", "s1", 1);
                session.Memory.Add(AIBot.Core.Llm.LlmMessage.User("m1"));
                session.Memory.Add(AIBot.Core.Llm.LlmMessage.Assistant("m2"));
                session.Memory.Add(AIBot.Core.Llm.LlmMessage.User("m3"));
                session.Memory.Add(AIBot.Core.Llm.LlmMessage.Assistant("m4"));
                Assert.True(session.Memory.EvictedCount > 0);

                Assert.True(SessionStore.Save(session));

                List<PendingMemorySession> pending = SessionStore.ScanPendingPlayerSessions()
                    .Where(x => x.GameId == GameId).ToList();
                Assert.Single(pending);
                Assert.Equal("p1", pending[0].PlayerId);
                Assert.Equal("s1", pending[0].SessionId);

                // 重新加载（清空进程内缓存）后消息窗口与待摘要批次应完整恢复。
                // Messages 只是活跃窗口（容量 2），另外 2 条在被淘汰队列里。
                SessionStore.UsePersistence(new MongoSessionPersistence(factory));
                SessionState reloaded = SessionStore.GetOrCreate(GameId, "lin", "p1", "s1", 1);
                Assert.Equal(2, reloaded.Memory.Messages.Count);
                Assert.Equal(2, reloaded.Memory.EvictedCount);
            }
            finally
            {
                // 还原静态门面，避免污染其他用例。
                SessionStore.UsePersistence(new JsonSessionPersistence(DataStore.FindDataRoot));
            }
        }

        [MongoFact]
        public void Queue_WithMongoJobPersistence_PersistsAndInvalidates()
        {
            // 验证 Mongo 模式下 MemorySummaryQueue 真正调用 IMemorySummaryJobPersistence：
            // EnqueueManual→UpsertPending 落库；InvalidatePlayer→DeleteForPlayer 清除。
            // 队列不 StartAsync 就没有消费者，pending 行不会被后台删掉，断言是确定性的。
            MongoConnectionFactory factory = NewFactory();
            MongoInitializer.ApplyAsync(factory, new ConfigurationBuilder().Build(), CancellationToken.None)
                .GetAwaiter().GetResult();
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "aibot-queue-mongo-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("Memory:SummaryQueueCapacity", "16")
                }).Build();
                var repository = new JsonMemoryRepository(() => root);
                var persistence = new MongoMemorySummaryJobPersistence(factory);
                var queue = new MemorySummaryQueue(new PlayerMemoryService(repository), config,
                    new MemoryAuditService(new JsonMemoryAuditStore(() => root)), null, persistence);

                Assert.True(queue.EnqueueManual(GameId, "lin", "p9", "s9", "tester"));

                List<MemorySummaryJobRecord> recoverable = persistence.LoadRecoverable();
                Assert.Contains(recoverable, record =>
                    record.JobKey == GameId + "|lin|p9|s9|g0" && record.Status == "pending");

                queue.InvalidatePlayer(GameId, "lin", "p9");
                Assert.Empty(persistence.LoadRecoverable());
            }
            finally
            {
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            }
        }
    }
}
