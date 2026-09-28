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
            Assert.Equal("old-key", ModelConnectionStore.ResolveMain("demo", "ordinary", legacy).apiKey);
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
        public void SessionSummaryOverrideTakesPriorityOverAssignedConnection()
        {
            Assert.True(ModelConnectionStore.Save(NewConnection("summary"), false, true));
            Assert.True(ModelConnectionStore.SetBinding(new ModelConnectionBindingRequest
            { scope = "npc_summary", gameId = "demo", npcId = "smith", connectionId = "summary" }));
            var policy = MemoryPolicy.Defaults();
            Assert.False(ModelConnectionStore.ApplySummary("demo", "smith", new MemorySettings(), policy,
                new MemoryPolicyOverrides { useMainSummaryModel = true }));
            Assert.Null(policy.summaryModel);

            var custom = new ModelSettings { model = "debug-summary" };
            policy.summaryModel = custom;
            Assert.False(ModelConnectionStore.ApplySummary("demo", "smith", new MemorySettings(), policy,
                new MemoryPolicyOverrides { summaryModel = custom }));
            Assert.Same(custom, policy.summaryModel);
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
