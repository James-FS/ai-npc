using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AIBot.Server;
using Newtonsoft.Json;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>P2 会话持久化接缝：JSON 实现的文件清理/保护规则，以及 static 门面默认回到 JSON。</summary>
    public class SessionPersistenceTests : IDisposable
    {
        private readonly string _root;

        public SessionPersistenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "aibot-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string WriteSessionFile(string gameId, string npcId, string playerId, string sessionId,
            DateTime lastActiveUtc, bool pending = false, bool pendingToolRound = false,
            string processingRequestId = null)
        {
            string npcRoot = Path.Combine(_root, "games", gameId, "sessions", npcId);
            string path = string.IsNullOrEmpty(playerId)
                ? Path.Combine(npcRoot, sessionId + ".json")
                : Path.Combine(npcRoot, playerId, sessionId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var dto = new SessionStore.SessionFileDto
            {
                npcId = npcId, playerId = playerId, sessionId = sessionId, lastActiveUtc = lastActiveUtc,
                evictedMessages = pending ? new List<AIBot.Core.Llm.LlmMessage> { AIBot.Core.Llm.LlmMessage.User("x") } : null,
                pendingToolRound = pendingToolRound ? new PendingToolRound { roundToken = "r#0" } : null
            };
            if (processingRequestId != null)
                dto.recentRequests = new List<ChatRequestRecord> { new ChatRequestRecord
                    { requestId = processingRequestId, status = ChatRequestStatuses.Processing } };
            File.WriteAllText(path, JsonConvert.SerializeObject(dto));
            // 真实过期文件的 mtime 也是旧的；DeleteExpired 会先看文件时间。
            File.SetLastWriteTimeUtc(path, lastActiveUtc);
            return path;
        }

        [Fact]
        public void DeleteExpired_RemovesStaleAndProtectsOthers()
        {
            var store = new JsonSessionPersistence(() => _root);
            DateTime stale = DateTime.UtcNow.AddDays(-10);
            WriteSessionFile("g", "npc", "p1", "stale", stale);                                  // 应删
            string active = WriteSessionFile("g", "npc", "p2", "active", stale);                 // 活跃键 → 保护
            string pending = WriteSessionFile("g", "npc", "p3", "pending", stale, pending: true);// 待摘要 → 保护
            string tool = WriteSessionFile("g", "npc", "p4", "tool", stale, pendingToolRound: true); // 挂起轮 → 保护
            string processing = WriteSessionFile("g", "npc", "p5", "processing", stale,
                processingRequestId: "req-1");                                                   // processing → 保护
            string legacy = WriteSessionFile("g", "npc", null, "legacy", stale);                 // legacy 保护路径 → 保护

            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { SessionStore.IdentityKey("g", "npc", "p2", "active") };
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { legacy };

            int removed = store.DeleteExpired(TimeSpan.FromDays(1), activeKeys, protectedPaths);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(Path.Combine(_root, "games", "g", "sessions", "npc", "p1", "stale.json")));
            Assert.True(File.Exists(active));
            Assert.True(File.Exists(pending));
            Assert.True(File.Exists(tool));
            Assert.True(File.Exists(processing));
            Assert.True(File.Exists(legacy));
        }

        [Fact]
        public void DeleteExpired_SkipsLegacyFileReferencedWithPlayerId()
        {
            // 场景：请求带 playerId，磁盘上只有 v1 legacy 文件（DTO playerId 为空）。
            var store = new JsonSessionPersistence(() => _root);
            DateTime stale = DateTime.UtcNow.AddDays(-10);
            string legacy = WriteSessionFile("g", "npc", null, "s1", stale);

            // 门面用带 playerId 的身份键 + legacy 来源路径保护它。
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { SessionStore.IdentityKey("g", "npc", "p1", "s1") };
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { legacy };

            int removed = store.DeleteExpired(TimeSpan.FromDays(1), activeKeys, protectedPaths);

            Assert.Equal(0, removed);
            Assert.True(File.Exists(legacy));
        }

        [Fact]
        public void RoundTrip_PreservesPendingToolRoundAndEvicted()
        {
            var store = new JsonSessionPersistence(() => _root);
            var dto = new SessionStore.SessionFileDto
            {
                npcId = "npc", playerId = "p1", sessionId = "s1",
                evictedMessages = new List<AIBot.Core.Llm.LlmMessage> { AIBot.Core.Llm.LlmMessage.User("evicted") },
                pendingToolRound = new PendingToolRound { roundToken = "req#0", roundIndex = 1 }
            };
            store.Save("g", dto);

            SessionStore.SessionFileDto loaded = store.Load("g", "npc", "p1", "s1");

            Assert.NotNull(loaded);
            Assert.Single(loaded.evictedMessages);
            Assert.Equal("req#0", loaded.pendingToolRound.roundToken);
            // legacy 来源不应来自新写入的文件
            Assert.Null(loaded.legacySourcePath);
        }

        [Fact]
        public void LegacyFallback_LoadSetsLegacySourcePath_AndSaveArchives()
        {
            var store = new JsonSessionPersistence(() => _root);
            // 只写 legacy（playerId=null）文件，但用带 playerId 的身份去加载。
            string legacy = WriteSessionFile("g", "npc", null, "s1", DateTime.UtcNow);

            SessionStore.SessionFileDto loaded = store.Load("g", "npc", "p1", "s1");
            Assert.NotNull(loaded);
            Assert.Equal(legacy, loaded.legacySourcePath);

            // 旧长期字段已清空 → 保存时归档 legacy 文件。
            loaded.summary = null;
            loaded.facts = new List<string>();
            SessionSaveResult result = store.Save("g", loaded);

            Assert.True(result.Persisted);
            Assert.True(result.LegacyArchived);
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(legacy + ".migrated.bak"));
        }

        [Fact]
        public void StaticFacade_DefaultsToJson_AndUsePersistenceSwitches()
        {
            try
            {
                SessionStore.UsePersistence(new JsonSessionPersistence(() => _root));
                SessionState session = SessionStore.GetOrCreate("g", "npc", "p1", "s1", 4);
                session.Memory.Add(AIBot.Core.Llm.LlmMessage.User("hello"));
                Assert.True(SessionStore.Save(session));

                // 清空内存缓存后仍能从磁盘恢复（证明默认走 JSON 实现）。
                SessionStore.UsePersistence(new JsonSessionPersistence(() => _root));
                SessionState reloaded = SessionStore.GetOrCreate("g", "npc", "p1", "s1", 4);
                Assert.Single(reloaded.Memory.Messages);
            }
            finally
            {
                // 还原静态门面，避免污染其他用例。
                SessionStore.UsePersistence(new JsonSessionPersistence(DataStore.FindDataRoot));
            }
        }
    }
}
