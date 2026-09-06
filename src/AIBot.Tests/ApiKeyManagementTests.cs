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
                    new MemoryAuditService(() => tempRoot));
                return await ReadinessService.CheckAsync(new StorageOptions { Provider = "Json" },
                    mysql: null, queue: queue, config: config, ct: System.Threading.CancellationToken.None);
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
    }
}
