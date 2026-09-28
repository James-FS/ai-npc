using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Server;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>模型连接与旧 NPC 配置的脱敏响应和就绪状态。</summary>
    public class ApiKeyManagementTests : IDisposable
    {
        private readonly string _tempDir;

        public ApiKeyManagementTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "aibot-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { /* 临时目录清理失败可忽略 */ }
        }

        private static IConfiguration ConfigWith() => new ConfigurationBuilder().Build();

        [Fact]
        public void KeyInventory_MasksMainAndSummaryKeys_AndReportsTheirSources()
        {
            string originalDataRoot = Environment.GetEnvironmentVariable("AIBOT_DATA_ROOT");
            var cachedRoot = typeof(DataStore).GetField("_cachedRoot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            string originalCache = (string)cachedRoot.GetValue(null);
            string dataRoot = Path.Combine(_tempDir, "data");
            Directory.CreateDirectory(Path.Combine(dataRoot, "games"));
            try
            {
                Environment.SetEnvironmentVariable("AIBOT_DATA_ROOT", dataRoot);
                cachedRoot.SetValue(null, null);
                var gamePolicy = AIBot.Core.Memory.MemoryPolicy.Defaults();
                gamePolicy.summaryModel = new ModelSettings { apiKey = "game-summary-key-0123456789" };
                Assert.True(DataStore.SaveMemoryPolicy("demo", gamePolicy));
                var npc = new AgentConfigDto { npcId = "tester" };
                npc.model.apiKey = "npc-main-key-0123456789";
                npc.memory.inheritGameDefaults = true;
                Assert.True(DataStore.SaveNpc("demo", npc));

                var method = typeof(AdminEndpoints).GetMethod("LlmKeyInventory",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var snapshot = (Newtonsoft.Json.Linq.JObject)method.Invoke(null,
                    new object[] { ConfigWith(), false });
                string json = snapshot.ToString(Newtonsoft.Json.Formatting.None);
                Assert.DoesNotContain("npc-main-key-0123456789", json);
                Assert.DoesNotContain("game-summary-key-0123456789", json);
                var row = (Newtonsoft.Json.Linq.JObject)snapshot["npcs"][0];
                Assert.Equal("npc", (string)row["mainSource"]);
                Assert.Equal("game", (string)row["summarySource"]);
                Assert.Equal("npc-mai*****6789", (string)row["mainMaskedKey"]);
                Assert.Equal("game-su*****6789", (string)row["summaryMaskedKey"]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AIBOT_DATA_ROOT", originalDataRoot);
                cachedRoot.SetValue(null, originalCache);
            }
        }

        [Fact]
        public void RedactSecrets_ClearsKey_AndFlagsHasApiKey()
        {
            var withKey = new AgentConfigDto();
            withKey.model.apiKey = "sk-secret-0001";
            AgentConfigDto redacted = InvokeRedact(withKey);
            Assert.Equal(string.Empty, redacted.model.apiKey);
            Assert.True(redacted.hasApiKey.Value);

            var withoutKey = new AgentConfigDto();
            AgentConfigDto clean = InvokeRedact(withoutKey);
            Assert.Equal(string.Empty, clean.model.apiKey);
            Assert.False(clean.hasApiKey.Value);
        }

        [Fact]
        public void RedactedConfig_RoundTripsWithoutLeakingKey()
        {
            var source = new AgentConfigDto { npcId = "tester" };
            source.model.apiKey = "sk-secret-0001";
            source.memory.summaryModel = new ModelSettings { apiKey = "sk-summary-0002" };

            AgentConfigDto clone = InvokeRedact(source);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(clone);

            Assert.DoesNotContain("sk-secret-0001", json);
            Assert.DoesNotContain("sk-summary-0002", json);
            Assert.Contains("\"hasApiKey\":true", json);
        }

        [Fact]
        public void NpcSave_EmptyKeyInSubmission_PreservesExistingKey()
        {
            var existing = new AgentConfigDto { npcId = "tester", persona = "旧人设" };
            existing.model.apiKey = "sk-existing-0001";
            existing.memory.summaryModel = new ModelSettings { apiKey = "sk-existing-summary" };

            // 编辑器把输入框留空再保存：应视为"不改"，不得抹掉已配置 key
            var submitted = new AgentConfigDto { npcId = "tester", persona = "新人设" };
            submitted.model.apiKey = "";
            submitted.memory.summaryModel = new ModelSettings { apiKey = null };

            AgentConfigDto toSave = InvokeResolveNpcSaveBody(submitted, existing, clearKey: false);

            Assert.Equal("新人设", toSave.persona);
            Assert.Equal("sk-existing-0001", toSave.model.apiKey);
            Assert.Equal("sk-existing-summary", toSave.memory.summaryModel.apiKey);
        }

        [Fact]
        public void NpcSave_NewKeySubmission_ReplacesExistingKey()
        {
            var existing = new AgentConfigDto { npcId = "tester" };
            existing.model.apiKey = "sk-existing-0001";

            var submitted = new AgentConfigDto { npcId = "tester" };
            submitted.model.apiKey = "sk-new-key-0001";

            AgentConfigDto toSave = InvokeResolveNpcSaveBody(submitted, existing, clearKey: false);

            Assert.Equal("sk-new-key-0001", toSave.model.apiKey);
        }

        [Fact]
        public void NpcSave_ClearApiKey_RemovesKeys_KeepsAllOtherFields()
        {
            var existing = new AgentConfigDto { npcId = "tester", displayName = "林晓", persona = "旧人设" };
            existing.model.apiKey = "sk-secret-0001";
            existing.model.baseUrl = "https://custom.example.com";
            existing.memory.summaryModel = new ModelSettings { apiKey = "sk-summary-0002" };

            // clear 请求只带最小负载（npcId），未提及 persona/displayName 等字段
            var minimal = new AgentConfigDto { npcId = "tester" };

            AgentConfigDto toSave = InvokeResolveNpcSaveBody(minimal, existing, clearKey: true);

            Assert.Null(toSave.model.apiKey);
            Assert.Null(toSave.memory.summaryModel.apiKey);
            Assert.Equal("林晓", toSave.displayName);          // 非 key 字段不被抹掉
            Assert.Equal("旧人设", toSave.persona);
            Assert.Equal("https://custom.example.com", toSave.model.baseUrl);
        }

        private static AgentConfigDto InvokeResolveNpcSaveBody(AgentConfigDto submitted,
            AgentConfigDto existing, bool clearKey)
        {
            var method = typeof(AdminEndpoints).GetMethod("ResolveNpcSaveBody",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            return (AgentConfigDto)method.Invoke(null, new object[] { submitted, existing, clearKey });
        }

        private static AgentConfigDto InvokeRedact(AgentConfigDto source)
        {
            var method = typeof(AdminEndpoints).GetMethod("RedactSecrets",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            return (AgentConfigDto)method.Invoke(null, new object[] { source });
        }

        // ---- Readiness 探针只检查分配的连接或旧 NPC Key ----

        private static async Task<(bool Ready, object Body)> CheckReadiness(string tempRoot, Func<bool> anyNpcKey = null)
        {
            ReadinessService.HasAnyNpcKeyOverride = anyNpcKey;
            try
            {
                var config = ConfigWith();
                var repository = new JsonMemoryRepository(() => tempRoot);
                var queue = new MemorySummaryQueue(new PlayerMemoryService(repository), config,
                    new MemoryAuditService(new JsonMemoryAuditStore(() => tempRoot)));
                return await ReadinessService.CheckAsync(new StorageOptions { Provider = "Json" },
                    queue: queue, config: config, ct: System.Threading.CancellationToken.None);
            }
            finally
            {
                ReadinessService.HasAnyNpcKeyOverride = null;
            }
        }

        [Fact]
        public async Task Readiness_OnlyPerNpcKey_StillCountsAsReady()
        {
            (bool ready, _) = await CheckReadiness(_tempDir, anyNpcKey: () => true);

            Assert.True(ready);
        }

        [Fact]
        public async Task Readiness_NoKeyAnywhere_ReportsNotReady()
        {
            (bool ready, object body) = await CheckReadiness(_tempDir, anyNpcKey: () => false);

            Assert.False(ready);
            object checks = body.GetType().GetProperty("checks").GetValue(body);
            object llm = checks.GetType().GetProperty("llm").GetValue(checks);
            Assert.False((bool)llm.GetType().GetProperty("ok").GetValue(llm));
        }

        [Theory]
        [InlineData("sk-ca1a2QWERTYUIOPbd31", "sk-ca1a*****bd31")]  // 常规长度：前 7 位 + 末 4 位
        [InlineData("0123456789abcdef", "0123456*****cdef")]        // 16 位边界：仍按格式脱敏
        [InlineData("sk-short1234567", "***")]                      // 15 位：前后缀占比过高，整体遮蔽
        [InlineData("sk-abcd", "***")]
        [InlineData(null, null)]
        public void MaskKey_ShowsPrefixAndTail_AndHidesShortKeys(string key, string expected)
        {
            Assert.Equal(expected, (string)InvokeMaskKey(key));
        }

        [Fact]
        public void MaskKey_StarCountIsFixed_SoLengthIsNotLeaked()
        {
            string shorter = (string)InvokeMaskKey("sk-" + new string('a', 20) + "tail");
            string longer = (string)InvokeMaskKey("sk-" + new string('b', 80) + "tail");

            Assert.Equal(5, shorter.Split('*').Length - 1);
            Assert.Equal(5, longer.Split('*').Length - 1);
        }

        private static object InvokeMaskKey(string key)
        {
            var method = typeof(AdminEndpoints).GetMethod("MaskKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            return method.Invoke(null, new object[] { key });
        }
    }
}
