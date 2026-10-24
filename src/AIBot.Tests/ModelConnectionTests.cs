using System;
using System.IO;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    public class ModelConnectionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "aibot-connections-" + Guid.NewGuid().ToString("N"));

        public ModelConnectionTests()
        {
            Directory.CreateDirectory(_root);
            ModelConnectionStore.OverridePath = Path.Combine(_root, "model-connections.json");
        }

        public void Dispose()
        {
            ModelConnectionStore.OverridePath = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static ModelConnection NewConnection(string id, string key = "profile-key-0123456789") => new ModelConnection
        {
            id = id, name = id, baseUrl = "https://example.com/v1", model = "example-model",
            apiFormat = "openai_chat_completions", apiKey = key
        };

        [Fact]
        public void SelectedProfile_ReplacesAddressModelAndKeyAsOneUnit()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("primary"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_main", gameId = "demo", npcId = "smith", connectionId = "primary" }));
            var legacy = new ModelSettings
            { baseUrl = "https://other.example/v1", model = "old-model", apiKey = "old-key" };

            ModelSettings resolved = ModelConnectionStore.ResolveMain("demo", "smith", legacy);
            Assert.Equal("https://example.com/v1", resolved.baseUrl);
            Assert.Equal("example-model", resolved.model);
            Assert.Equal("profile-key-0123456789", resolved.apiKey);
            Assert.Equal("old-model", legacy.model);
        }

        [Fact]
        public void GlobalConnectionIsDefaultAndNpcConnectionOverridesIt()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("global"), false, true));
            Assert.True(ModelConnectionStore.Save(NewConnection("special", "special-key-0123456789"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "global_main", connectionId = "global" }));
            var legacy = new ModelSettings { model = "old", apiKey = "old-key" };
            Assert.Equal("profile-key-0123456789",
                ModelConnectionStore.ResolveMain("demo", "ordinary", legacy).apiKey);
            Assert.False(ModelConnectionStore.Delete("global"));

            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_main", gameId = "demo", npcId = "special", connectionId = "special" }));
            Assert.Equal("special-key-0123456789",
                ModelConnectionStore.ResolveMain("demo", "special", legacy).apiKey);
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "global_main", connectionId = null }));
            InvalidOperationException missing = Assert.Throws<InvalidOperationException>(
                () => ModelConnectionStore.ResolveMain("demo", "ordinary", legacy));
            Assert.Contains("未分配 Server 主模型连接", missing.Message);
            Assert.True(ModelConnectionStore.Delete("global"));
        }

        [Fact]
        public void SummaryProfileOverridesGameThenFallsBackAfterUnbind()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("game"), false, true));
            Assert.True(ModelConnectionStore.Save(NewConnection("npc", "npc-summary-key-0123456789"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "game_summary", gameId = "demo", connectionId = "game" }));
            var npcMemory = new MemorySettings { inheritGameDefaults = true };
            var policy = MemoryPolicy.Defaults();
            Assert.True(ModelConnectionStore.ApplySummary("demo", "smith", npcMemory, policy));
            Assert.Equal("profile-key-0123456789", policy.summaryModel.apiKey);

            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = "npc" }));
            policy = MemoryPolicy.Defaults();
            Assert.True(ModelConnectionStore.ApplySummary("demo", "smith", npcMemory, policy));
            Assert.Equal("npc-summary-key-0123456789", policy.summaryModel.apiKey);

            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = null }));
            Assert.True(ModelConnectionStore.Delete("npc"));
        }

        [Fact]
        public void SummaryGenerationOverrideStillUsesAssignedConnection()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("summary"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = "summary" }));
            Assert.Equal("summary", ModelConnectionStore.Snapshot().npcSummary["demo/smith"]);
            Assert.Equal("dedicated", ModelConnectionStore.Snapshot().npcSummaryMode["demo/smith"]);
            var custom = new ModelSettings { temperature = 0.15f, maxTokens = 900, timeoutMs = 5000 };
            var policy = MemoryPolicy.Defaults();
            policy.summaryModel = custom;
            Assert.True(ModelConnectionStore.ApplySummary("demo", "smith", new MemorySettings(), policy,
                new MemoryPolicyOverrides { summaryModel = custom }));
            Assert.Equal("https://example.com/v1", policy.summaryModel.baseUrl);
            Assert.Equal("example-model", policy.summaryModel.model);
            Assert.Equal("profile-key-0123456789", policy.summaryModel.apiKey);
            Assert.Equal(0.15f, policy.summaryModel.temperature);
            Assert.Equal(900, policy.summaryModel.maxTokens);

            policy = MemoryPolicy.Defaults();
            policy.summaryModel = new ModelSettings { temperature = 0.33f };
            Assert.True(ModelConnectionStore.Save(NewConnection("main"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "global_main", connectionId = "main" }));
            Assert.False(ModelConnectionStore.ApplySummary("demo", "smith", new MemorySettings(), policy,
                new MemoryPolicyOverrides { useMainSummaryModel = true }));
            Assert.Equal(0.33f, policy.summaryModel.temperature);
            Assert.Equal("example-model", policy.summaryModel.model);
        }

        [Fact]
        public void GenerationOnlySummaryInheritsGameConnectionAndMainKey()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("global"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "global_main", connectionId = "global" }));
            ModelConnectionDocument document = ModelConnectionStore.Snapshot();
            document.connections.Add(new ModelConnection
            {
                id = "game-summary", name = "Game Summary", baseUrl = "https://summary.example/v1",
                model = "summary-model", apiKey = null, apiFormat = "openai_chat_completions"
            });
            document.gameSummary["demo"] = "game-summary";
            ModelConnectionStore.SaveMigration(document);

            var policy = MemoryPolicy.Defaults();
            policy.summaryModel = new ModelSettings { temperature = 0.25f, maxTokens = 321 };
            var npcMemory = new MemorySettings { inheritGameDefaults = true, summaryModel = new ModelSettings { temperature = 0.6f } };
            Assert.True(ModelConnectionStore.ApplySummary("demo", "smith", npcMemory, policy));
            Assert.Equal("https://summary.example/v1", policy.summaryModel.baseUrl);
            Assert.Equal("summary-model", policy.summaryModel.model);
            Assert.Equal("profile-key-0123456789", policy.summaryModel.apiKey);
            Assert.Equal(0.25f, policy.summaryModel.temperature);
            Assert.Equal(321, policy.summaryModel.maxTokens);

            ModelSettings backgroundSettings = MemorySummaryQueue.ResolveSummarySettings(policy.summaryModel,
                ModelConnectionStore.ResolveMain("demo", "smith", null));
            Assert.Equal(policy.summaryModel.baseUrl, backgroundSettings.baseUrl);
            Assert.Equal(policy.summaryModel.model, backgroundSettings.model);
            Assert.Equal(policy.summaryModel.apiKey, backgroundSettings.apiKey);
            Assert.Equal(policy.summaryModel.temperature, backgroundSettings.temperature);
            Assert.Equal(policy.summaryModel.maxTokens, backgroundSettings.maxTokens);
        }

        [Fact]
        public void LegacyModelCannotBypassConnectionStoreWhenNoBindingExists()
        {
            var legacy = new ModelSettings
            { baseUrl = "https://legacy.example/v1", model = "legacy", apiKey = "legacy-key-0123456789" };
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => ModelConnectionStore.ResolveMain("demo", "unbound", legacy));
            Assert.Contains("未分配 Server 主模型连接", error.Message);
        }

        [Fact]
        public void SummaryParameterOverrideDoesNotChooseOrRequireAConnection()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("main"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "global_main", connectionId = "main" }));
            var policy = MemoryPolicy.Defaults();
            policy.summaryModel = new ModelSettings { temperature = 0.3f };
            Assert.False(ModelConnectionStore.ApplySummary("demo", "smith", new MemorySettings(), policy));
            Assert.Equal(0.3f, policy.summaryModel.temperature);
            Assert.Equal("https://example.com/v1", policy.summaryModel.baseUrl);
            Assert.Equal("profile-key-0123456789", policy.summaryModel.apiKey);
        }

        [Fact]
        public void ReusingMainSummaryPreservesResolvedMainGenerationSettings()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("global"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "global_main", connectionId = "global" }));
            var mainDefaults = new ModelSettings { temperature = 0.17f, maxTokens = 4321, timeoutMs = 7654 };
            var policy = MemoryPolicy.Defaults();

            SummaryModelResolution resolved = ModelConnectionStore.ResolveSummary("demo", "smith",
                new MemorySettings(), policy, ModelConnectionStore.ResolveMain("demo", "smith", mainDefaults));
            policy.summaryModel = resolved.settings;
            Assert.Equal(0.17f, policy.summaryModel.temperature);
            Assert.Equal(4321, policy.summaryModel.maxTokens);
            Assert.Equal(7654, policy.summaryModel.timeoutMs);
        }

        [Fact]
        public void ClearingNpcBindingsRemovesMainAndSummaryAssignments()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("primary"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_main", gameId = "demo", npcId = "smith", connectionId = "primary" }));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = "primary" }));

            ModelConnectionStore.ClearNpcBindings("demo", "smith");

            var snapshot = ModelConnectionStore.Snapshot();
            Assert.Empty(snapshot.npcMain);
            Assert.Empty(snapshot.npcSummary);
            Assert.True(ModelConnectionStore.Delete("primary"));
        }

        [Fact]
        public void ProfileSnapshotNeverReturnsPlaintextKey()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("primary"), false, true));
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(ModelConnectionStore.RedactedSnapshot());
            Assert.DoesNotContain("profile-key-0123456789", json);
            Assert.Contains("profile*****6789", json);
        }

        [Fact]
        public void KeyIsRequiredOnCreateAndCanBeRetainedOnEdit()
        {
            ModelConnection connection = NewConnection("primary", null);
            Assert.Equal("API Key 必填", ModelConnectionStore.Validate(connection, requireKey: true));
            Assert.False(ModelConnectionStore.Save(connection, false, true));

            connection.apiKey = "profile-key-0123456789";
            Assert.True(ModelConnectionStore.Save(connection, false, true));
            ModelConnection edit = NewConnection("primary", null);
            edit.name = "Renamed";
            Assert.True(ModelConnectionStore.HasApiKey("primary"));
            Assert.True(ModelConnectionStore.Save(edit, false, false));
            Assert.Equal("profile-key-0123456789", ModelConnectionStore.Snapshot().connections[0].apiKey);
            Assert.False(ModelConnectionStore.Save(NewConnection("primary", "another-key"), true, false));
        }

        [Fact]
        public void KeylessSummaryConnectionCanBeEditedAndBoundWithoutReplacingMainCredentials()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("global"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "global_main", connectionId = "global" }));
            ModelConnectionDocument document = ModelConnectionStore.Snapshot();
            document.connections.Add(new ModelConnection
            { id = "summary", name = "Summary", baseUrl = "https://summary.example/v1", model = "sum", apiFormat = "openai_chat_completions" });
            ModelConnectionStore.SaveMigration(document);

            ModelConnection edit = new ModelConnection
            { id = "summary", name = "Summary renamed", baseUrl = "https://summary.example/v2", model = "sum-v2", apiFormat = "openai_chat_completions" };
            Assert.True(ModelConnectionStore.Save(edit, false, false));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "game_summary", gameId = "demo", connectionId = "summary" }));
            var policy = MemoryPolicy.Defaults();
            Assert.True(ModelConnectionStore.ApplySummary("demo", "smith", new MemorySettings { inheritGameDefaults = true }, policy));
            Assert.Equal("https://summary.example/v2", policy.summaryModel.baseUrl);
            Assert.Equal("profile-key-0123456789", policy.summaryModel.apiKey);
        }

        [Fact]
        public void CentralSummaryModeWinsOverLegacyFlagsAndParametersDoNotSelectConnections()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("main"), false, true));
            Assert.True(ModelConnectionStore.Save(NewConnection("game"), false, true));
            Assert.True(ModelConnectionStore.Save(NewConnection("npc", "npc-summary-key-0123456789"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "global_main", connectionId = "main" }));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "game_summary", gameId = "demo", connectionId = "game" }));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = "npc", summaryMode = "dedicated" }));

            var legacy = new MemorySettings { inheritGameDefaults = true, useMainSummaryModel = true };
            var policy = MemoryPolicy.Defaults();
            policy.summaryModel = new ModelSettings { temperature = 0.2f, maxTokens = 777, timeoutMs = 3333 };
            Assert.True(ModelConnectionStore.ApplySummary("demo", "smith", legacy, policy));
            Assert.Equal("npc-summary-key-0123456789", policy.summaryModel.apiKey);
            Assert.Equal(0.2f, policy.summaryModel.temperature);
            Assert.Equal(777, policy.summaryModel.maxTokens);

            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = null, summaryMode = "main" }));
            policy = MemoryPolicy.Defaults();
            policy.summaryModel = new ModelSettings { temperature = 0.27f, maxTokens = 901, timeoutMs = 7012 };
            Assert.False(ModelConnectionStore.ApplySummary("demo", "smith", legacy, policy));
            Assert.Equal(0.27f, policy.summaryModel.temperature);
            Assert.Equal(901, policy.summaryModel.maxTokens);
            SummaryModelResolution sessionDedicated = ModelConnectionStore.ResolveSummary("demo", "smith", legacy,
                policy, null, new MemoryPolicyOverrides { useMainSummaryModel = false });
            Assert.Equal("invalid", sessionDedicated.source);
            Assert.Contains("未分配有效摘要连接", sessionDedicated.diagnostic);

            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = null, summaryMode = "inherit" }));
            policy = MemoryPolicy.Defaults();
            Assert.True(ModelConnectionStore.ApplySummary("demo", "smith", new MemorySettings { inheritGameDefaults = true, useMainSummaryModel = true }, policy));
            Assert.Equal("profile-key-0123456789", policy.summaryModel.apiKey);

            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = null }));
            Assert.Equal("inherit", ModelConnectionStore.Snapshot().npcSummaryMode["demo/smith"]);
            SummaryModelResolution sessionError = ModelConnectionStore.ResolveSummary("demo", "smith",
                new MemorySettings { inheritGameDefaults = false }, MemoryPolicy.Defaults(), null,
                new MemoryPolicyOverrides { useMainSummaryModel = false });
            Assert.Contains("未分配有效摘要连接", sessionError.diagnostic);
        }

        [Fact]
        public void LegacyUseMainFlagStillWinsBeforeTheNpcRevisitsCentralChoice()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("main", "main-key-0123456789"), false, true));
            Assert.True(ModelConnectionStore.Save(NewConnection("summary", "summary-key-0123456789"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest { scope = "global_main", connectionId = "main" }));
            var document = ModelConnectionStore.Snapshot();
            document.npcSummary["demo/smith"] = "summary";
            ModelConnectionStore.SaveMigration(document);

            var policy = MemoryPolicy.Defaults();
            SummaryModelResolution result = ModelConnectionStore.ResolveSummary("demo", "smith",
                new MemorySettings { inheritGameDefaults = true, useMainSummaryModel = true }, policy,
                ModelConnectionStore.ResolveMain("demo", "smith", null));
            Assert.Equal("main", result.source);
            Assert.Equal("main", result.connectionName);
            Assert.Equal("main-key-0123456789", result.settings.apiKey);
        }

        [Fact]
        public void ExplicitDedicatedModeWithoutBindingReturnsActionableError()
        {
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_summary", gameId = "demo", npcId = "smith", summaryMode = "dedicated" }));
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                ModelConnectionStore.ApplySummary("demo", "smith", new MemorySettings(), MemoryPolicy.Defaults()));
            Assert.Contains("未分配有效摘要连接", ex.Message);
        }

        [Fact]
        public void UnsupportedProtocolAndCompletedEndpointAreRejected()
        {
            ModelConnection connection = NewConnection("invalid");
            connection.apiFormat = "responses";
            Assert.NotNull(ModelConnectionStore.Validate(connection));
            connection.apiFormat = "openai_chat_completions";
            connection.baseUrl = "https://example.com/v1/chat/completions";
            Assert.NotNull(ModelConnectionStore.Validate(connection));
        }
    }
}
