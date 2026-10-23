using System;
using System.IO;
using System.Reflection;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    [CollectionDefinition("DataStore", DisableParallelization = true)]
    public sealed class DataStoreCollection { }

    [Collection("DataStore")]
    public sealed class LegacyModelConnectionMigrationTests
    {
        private static readonly FieldInfo CachedRoot = typeof(DataStore).GetField("_cachedRoot", BindingFlags.NonPublic | BindingFlags.Static);

        [Fact]
        public void Run_MovesInlineConnectionsOnceAndRetainsGenerationSettings()
        {
            string originalRoot = Environment.GetEnvironmentVariable("AIBOT_DATA_ROOT");
            FieldInfo cachedRoot = typeof(DataStore).GetField("_cachedRoot", BindingFlags.NonPublic | BindingFlags.Static);
            string originalCache = (string)cachedRoot.GetValue(null);
            string originalPath = ModelConnectionStore.OverridePath;
            string temp = Path.Combine(Path.GetTempPath(), "aibot-migration-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(temp, "data");
            Directory.CreateDirectory(Path.Combine(root, "games", "demo", "npcs"));
            Environment.SetEnvironmentVariable("AIBOT_DATA_ROOT", root);
            cachedRoot.SetValue(null, null);
            ModelConnectionStore.OverridePath = Path.Combine(root, "model-connections.json");
            try
            {
                var npc = new AgentConfigDto { npcId = "actor" };
                npc.model = new ModelSettings { baseUrl = "https://example.test/v1", model = "main-model", apiKey = "legacy-main-token", temperature = 0.65f };
                npc.memory.inheritGameDefaults = true;
                npc.memory.summaryModel = new ModelSettings { baseUrl = "https://example.test/v1", model = "summary-model", apiKey = "legacy-summary-token", maxTokens = 700 };
                Assert.True(DataStore.SaveNpc("demo", npc));
                MemoryPolicy game = MemoryPolicy.Defaults();
                game.summaryModel = new ModelSettings { baseUrl = "https://example.test/v1", model = "game-summary", apiKey = "legacy-game-token", temperature = 0.2f };
                Assert.True(DataStore.SaveMemoryPolicy("demo", game));

                Assert.Equal(3, LegacyModelConnectionMigration.Run());
                AgentConfigDto migrated = DataStore.LoadNpc("demo", "actor");
                Assert.Null(migrated.model.apiKey);
                Assert.Null(migrated.model.baseUrl);
                Assert.Equal(0.65f, migrated.model.temperature);
                Assert.Null(migrated.memory.summaryModel.apiKey);
                Assert.Equal(700, migrated.memory.summaryModel.maxTokens);
                Assert.Null(DataStore.LoadMemoryPolicy("demo").summaryModel.apiKey);
                var connections = ModelConnectionStore.Snapshot();
                Assert.Equal(3, connections.connections.Count);
                Assert.True(connections.npcMain.ContainsKey("demo/actor"));
                Assert.True(connections.npcSummary.ContainsKey("demo/actor"));
                Assert.True(connections.gameSummary.ContainsKey("demo"));
                Assert.Equal(0, LegacyModelConnectionMigration.Run());
                Assert.Equal(3, ModelConnectionStore.Snapshot().connections.Count);
            }
            finally
            {
                ModelConnectionStore.OverridePath = originalPath;
                cachedRoot.SetValue(null, originalCache);
                Environment.SetEnvironmentVariable("AIBOT_DATA_ROOT", originalRoot);
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        [Fact]
        public void Run_PreservesExistingMainBindingsAndMigratesKeylessSummaryToInheritMainKey()
        {
            string originalRoot = Environment.GetEnvironmentVariable("AIBOT_DATA_ROOT");
            string originalCache = (string)CachedRoot.GetValue(null);
            string originalPath = ModelConnectionStore.OverridePath;
            string temp = Path.Combine(Path.GetTempPath(), "aibot-migration-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(temp, "data");
            Directory.CreateDirectory(Path.Combine(root, "games", "demo", "npcs"));
            Environment.SetEnvironmentVariable("AIBOT_DATA_ROOT", root);
            CachedRoot.SetValue(null, null);
            ModelConnectionStore.OverridePath = Path.Combine(root, "model-connections.json");
            try
            {
                var connectionDocument = new ModelConnectionDocument
                {
                    globalMain = "global",
                    connections = new System.Collections.Generic.List<ModelConnection>
                    {
                        new ModelConnection { id = "global", name = "Global", baseUrl = "https://main.example/v1", model = "main-model", apiKey = "global-main-token-012345" }
                    }
                };
                ModelConnectionStore.SaveMigration(connectionDocument);

                Assert.True(DataStore.SaveNpc("demo", new AgentConfigDto { npcId = "ordinary" }));
                var inlineNpc = new AgentConfigDto { npcId = "inline" };
                inlineNpc.model = new ModelSettings
                { baseUrl = "https://old-main.example/v1", model = "old-inline", apiKey = "legacy-main-token-012345" };
                Assert.True(DataStore.SaveNpc("demo", inlineNpc));

                MemoryPolicy gamePolicy = MemoryPolicy.Defaults();
                gamePolicy.summaryModel = new ModelSettings
                { baseUrl = "https://summary.example/v1", model = "game-summary-model", temperature = 0.2f, maxTokens = 500 };
                Assert.True(DataStore.SaveMemoryPolicy("demo", gamePolicy));
                var inheritingNpc = DataStore.LoadNpc("demo", "ordinary");
                inheritingNpc.memory.inheritGameDefaults = true;
                Assert.True(DataStore.SaveNpc("demo", inheritingNpc));

                Assert.Equal(3, LegacyModelConnectionMigration.Run());
                ModelConnectionDocument after = ModelConnectionStore.Snapshot();
                Assert.Equal("global", after.globalMain);
                Assert.Empty(after.npcMain);
                Assert.Contains("demo", after.gameSummary.Keys);
                Assert.Equal("global-main-token-012345", ModelConnectionStore.ResolveMain("demo", "ordinary", DataStore.LoadNpc("demo", "ordinary").model).apiKey);
                Assert.Equal("global-main-token-012345", ModelConnectionStore.ResolveMain("demo", "inline", DataStore.LoadNpc("demo", "inline").model).apiKey);
                Assert.Null(DataStore.LoadNpc("demo", "ordinary").model.baseUrl);
                Assert.Null(DataStore.LoadNpc("demo", "ordinary").model.model);
                Assert.Null(DataStore.LoadNpc("demo", "inline").model.apiKey);

                MemoryPolicy resolvedPolicy = MemoryPolicy.Defaults();
                resolvedPolicy.summaryModel = new ModelSettings { temperature = 0.4f, maxTokens = 350 };
                MemorySettings npcMemory = new MemorySettings { inheritGameDefaults = true, summaryModel = new ModelSettings { temperature = 0.6f } };
                Assert.True(ModelConnectionStore.ApplySummary("demo", "ordinary", npcMemory, resolvedPolicy));
                Assert.Equal("https://summary.example/v1", resolvedPolicy.summaryModel.baseUrl);
                Assert.Equal("game-summary-model", resolvedPolicy.summaryModel.model);
                Assert.Equal("global-main-token-012345", resolvedPolicy.summaryModel.apiKey);
                Assert.Equal(0.4f, resolvedPolicy.summaryModel.temperature);
                Assert.Equal(350, resolvedPolicy.summaryModel.maxTokens);
                Assert.Equal(0, LegacyModelConnectionMigration.Run());
                Assert.Equal(3, ModelConnectionStore.Snapshot().connections.Count);
            }
            finally
            {
                ModelConnectionStore.OverridePath = originalPath;
                CachedRoot.SetValue(null, originalCache);
                Environment.SetEnvironmentVariable("AIBOT_DATA_ROOT", originalRoot);
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
