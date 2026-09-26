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
    /// <summary>API Key 集中管理：解析优先级、系统设置存储、管理响应脱敏。</summary>
    public class ApiKeyManagementTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _originalEnv;

        public ApiKeyManagementTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "aibot-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _originalEnv = Environment.GetEnvironmentVariable(ApiKeyResolver.EnvVarName);
            SystemSettingsStore.OverridePath = Path.Combine(_tempDir, "system-settings.json");
        }

        public void Dispose()
        {
            SystemSettingsStore.OverridePath = null;
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, _originalEnv);
            try { Directory.Delete(_tempDir, true); } catch { /* 临时目录清理失败可忽略 */ }
        }

        private static IConfiguration ConfigWith(string appsettingsKey)
        {
            return new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string> { ["Llm:ApiKey"] = appsettingsKey }).Build();
        }

        [Fact]
        public void Resolve_NpcKeyWinsOverAllFallbacks()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, "env-key-0123456789");
            SystemSettingsStore.SaveLlmApiKey("console-key-0123456789");

            string resolved = ApiKeyResolver.Resolve("npc-key-0123456789", ConfigWith("appsettings-key"));

            Assert.Equal("npc-key-0123456789", resolved);
        }

        [Fact]
        public void Resolve_ConsoleSettingsBeatEnvAndAppsettings()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, "env-key-0123456789");
            SystemSettingsStore.SaveLlmApiKey("console-key-0123456789");

            string resolved = ApiKeyResolver.Resolve(null, ConfigWith("appsettings-key"));

            Assert.Equal("console-key-0123456789", resolved);
        }

        [Fact]
        public void Resolve_EnvBeatsAppsettings_AndAppsettingsIsFinalFallback()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, "env-key-0123456789");
            Assert.Equal("env-key-0123456789", ApiKeyResolver.Resolve(null, ConfigWith("appsettings-key")));

            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, null);
            Assert.Equal("appsettings-key", ApiKeyResolver.Resolve(null, ConfigWith("appsettings-key")));
        }

        [Fact]
        public void Resolve_BlankInputsAreSkipped()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, "  ");
            SystemSettingsStore.SaveLlmApiKey(null);

            Assert.Equal("appsettings-key", ApiKeyResolver.Resolve("   ", ConfigWith("appsettings-key")));
            Assert.Null(ApiKeyResolver.Resolve(null, ConfigWith("")));
        }

        [Fact]
        public void SourceOf_ReportsWinningLayer()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, "env-key-0123456789");
            SystemSettingsStore.SaveLlmApiKey("console-key-0123456789");
            Assert.Equal("npc", ApiKeyResolver.SourceOf("npc-key", ConfigWith(null)));
            Assert.Equal("console", ApiKeyResolver.SourceOf(null, ConfigWith(null)));

            SystemSettingsStore.SaveLlmApiKey(null);
            Assert.Equal("env", ApiKeyResolver.SourceOf(null, ConfigWith(null)));

            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, null);
            Assert.Equal("appsettings", ApiKeyResolver.SourceOf(null, ConfigWith("appsettings-key")));
            Assert.Equal("none", ApiKeyResolver.SourceOf(null, ConfigWith("")));
        }

        [Fact]
        public void SystemSettingsStore_RoundTripsTrimsAndClears()
        {
            string path = SystemSettingsStore.SettingsPath;
            Assert.False(File.Exists(path));

            Assert.True(SystemSettingsStore.SaveLlmApiKey("  sk-console-key-001  "));
            Assert.True(File.Exists(path));
            Assert.Equal("sk-console-key-001", SystemSettingsStore.LoadLlmApiKey());

            Assert.True(SystemSettingsStore.SaveLlmApiKey(null));
            Assert.Null(SystemSettingsStore.LoadLlmApiKey());
        }

        [Fact]
        public void SystemSettingsStore_ClearWhenMissing_IsIdempotentSuccess()
        {
            Assert.False(File.Exists(SystemSettingsStore.SettingsPath));
            Assert.True(SystemSettingsStore.SaveLlmApiKey(null));
            Assert.False(File.Exists(SystemSettingsStore.SettingsPath));   // 不应凭空创建文件
            Assert.Null(SystemSettingsStore.LoadLlmApiKey());
        }

        [Fact]
        public void SystemSettingsStore_InvalidJsonYieldsNullNotThrow()
        {
            File.WriteAllText(SystemSettingsStore.SettingsPath, "{ not valid json !!");

            Assert.Null(SystemSettingsStore.LoadLlmApiKey());
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

        // ---- Readiness 探针与解析链一致（控制台全局 key 计入就绪判定）----

        private static async Task<(bool Ready, object Body)> CheckReadiness(string tempRoot, Func<bool> anyNpcKey = null)
        {
            ReadinessService.HasAnyNpcKeyOverride = anyNpcKey;
            try
            {
                var config = ConfigWith(null);
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
        public async Task Readiness_ConsoleManagedKeyAlone_CountsAsReady()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, null);
            SystemSettingsStore.SaveLlmApiKey("sk-console-only-0123456789");

            (bool ready, object body) = await CheckReadiness(_tempDir, anyNpcKey: () => false);

            Assert.True(ready);
            object checks = body.GetType().GetProperty("checks").GetValue(body);
            object llm = checks.GetType().GetProperty("llm").GetValue(checks);
            Assert.True((bool)llm.GetType().GetProperty("ok").GetValue(llm));
        }

        [Fact]
        public async Task Readiness_OnlyPerNpcKey_StillCountsAsReady()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, null);
            SystemSettingsStore.SaveLlmApiKey(null);

            (bool ready, _) = await CheckReadiness(_tempDir, anyNpcKey: () => true);

            Assert.True(ready);
        }

        [Fact]
        public async Task Readiness_NoKeyAnywhere_ReportsNotReady()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, null);
            SystemSettingsStore.SaveLlmApiKey(null);

            (bool ready, object body) = await CheckReadiness(_tempDir, anyNpcKey: () => false);

            Assert.False(ready);
            object checks = body.GetType().GetProperty("checks").GetValue(body);
            object llm = checks.GetType().GetProperty("llm").GetValue(checks);
            Assert.False((bool)llm.GetType().GetProperty("ok").GetValue(llm));
        }

        [Fact]
        public void SettingsLlm_StatusCarriesAllFields_EnvCardDoesNotStaleAfterSave()
        {
            Environment.SetEnvironmentVariable(ApiKeyResolver.EnvVarName, "env-key-0123456789");
            SystemSettingsStore.SaveLlmApiKey("sk-new-key-0123456789");

            // 直接反射 GET/PUT 共用的 LlmSettingsStatus：若端点漏掉 envConfigured/priority，
            // 前端把 PUT 响应赋给状态后，环境变量卡片会误显「未配置」直到手动刷新。
            var status = (Newtonsoft.Json.Linq.JObject)InvokeLlmStatus(includeOk: true);
            string json = status.ToString(Newtonsoft.Json.Formatting.None);

            Assert.Contains("\"ok\":true", json);
            Assert.Contains("\"hasConsoleKey\":true", json);
            Assert.Contains("\"envConfigured\":true", json);
            Assert.Contains("\"priority\":\"npc > console > env > appsettings\"", json);
            Assert.Contains("\"maskedKey\":\"sk-new-*****6789\"", json);      // 前 7 位 + 末 4 位
            Assert.Contains("\"envMaskedKey\":\"env-key*****6789\"", json);   // 环境变量同样脱敏回显
            Assert.DoesNotContain("sk-new-key-0123456789", json);             // 明文永不回显
            Assert.DoesNotContain("env-key-0123456789", json);
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

        private static object InvokeLlmStatus(bool includeOk)
        {
            var method = typeof(AdminEndpoints).GetMethod("LlmSettingsStatus",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            return method.Invoke(null, new object[] { includeOk });
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
