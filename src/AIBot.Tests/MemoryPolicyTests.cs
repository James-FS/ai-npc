using AIBot.Core.Config;
using AIBot.Core.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AIBot.Tests
{
    public class MemoryPolicyTests
    {
        [Fact]
        public void LegacyNpc_DoesNotInheritGamePolicy()
        {
            var game = new MemoryPolicy { shortTermTurns = 30, maxFacts = 16 };
            var npc = new MemorySettings
            {
                inheritGameDefaults = false,
                shortTermTurns = 8
            };

            EffectiveMemoryPolicy result = MemoryPolicyResolver.Resolve(game, npc, null);

            Assert.Equal(8, result.policy.shortTermTurns);
            Assert.Equal(8, result.policy.maxFacts); // Core 默认值，而不是 Game 的16
            Assert.Equal("npc", result.sources["shortTermTurns"]);
            Assert.Equal("core", result.sources["maxFacts"]);
        }

        [Fact]
        public void InheritingNpc_UsesGameThenNpcOverride()
        {
            var game = new MemoryPolicy
            {
                shortTermTurns = 18,
                summaryThreshold = 40,
                maxFacts = 12
            };
            var npc = new MemorySettings
            {
                inheritGameDefaults = true,
                summaryThreshold = 10
            };

            EffectiveMemoryPolicy result = MemoryPolicyResolver.Resolve(game, npc, null);

            Assert.Equal(18, result.policy.shortTermTurns);
            Assert.Equal(10, result.policy.summaryThreshold);
            Assert.Equal(12, result.policy.maxFacts);
            Assert.Equal("game", result.sources["shortTermTurns"]);
            Assert.Equal("npc", result.sources["summaryThreshold"]);
        }

        [Fact]
        public void SessionOverride_HasHighestPriority()
        {
            var game = new MemoryPolicy { shortTermTurns = 18 };
            var npc = new MemorySettings { inheritGameDefaults = true, shortTermTurns = 10 };
            var session = new MemoryPolicyOverrides { shortTermTurns = 4 };

            EffectiveMemoryPolicy result = MemoryPolicyResolver.Resolve(game, npc, session);

            Assert.Equal(4, result.policy.shortTermTurns);
            Assert.Equal("session", result.sources["shortTermTurns"]);
        }

        [Fact]
        public void ServerLimits_ClampValuesAndUnsupportedCapabilities()
        {
            var npc = new MemorySettings
            {
                shortTermTurns = 999,
                summaryThreshold = -5,
                maxFacts = 100,
                summaryTrigger = MemoryPolicyValues.TriggerTokenCount,
                memoryScope = MemoryPolicyValues.ScopePlayerNpc,
                backgroundSummarization = true
            };
            var limits = new MemoryPolicyLimits
            {
                maxShortTermTurns = 50,
                maxSummaryThreshold = 200,
                maxFacts = 20,
                allowBackgroundSummarization = false
            };

            EffectiveMemoryPolicy result = MemoryPolicyResolver.Resolve(null, npc, null, limits);

            Assert.Equal(50, result.policy.shortTermTurns);
            Assert.Equal(0, result.policy.summaryThreshold);
            Assert.Equal(20, result.policy.maxFacts);
            Assert.Equal(MemoryPolicyValues.TriggerMessageCount, result.policy.summaryTrigger);
            Assert.Equal(MemoryPolicyValues.ScopeSession, result.policy.memoryScope);
            Assert.False(result.policy.backgroundSummarization);
            Assert.StartsWith("server-limit:", result.sources["shortTermTurns"]);
            Assert.True(result.adjustments.Count >= 6);
        }

        [Fact]
        public void Extensions_AreMergedAndSourceTrackedPerKey()
        {
            var game = new MemoryPolicy
            {
                extensions = JObject.Parse("{\"decayDays\":30,\"rememberTrade\":false}")
            };
            var npc = new MemorySettings
            {
                inheritGameDefaults = true,
                extensions = JObject.Parse("{\"rememberTrade\":true}")
            };
            var session = new MemoryPolicyOverrides
            {
                extensions = JObject.Parse("{\"debugTag\":\"ab\"}")
            };

            EffectiveMemoryPolicy result = MemoryPolicyResolver.Resolve(game, npc, session);

            Assert.Equal(30, result.policy.extensions.Value<int>("decayDays"));
            Assert.True(result.policy.extensions.Value<bool>("rememberTrade"));
            Assert.Equal("ab", result.policy.extensions.Value<string>("debugTag"));
            Assert.Equal("game", result.sources["extensions.decayDays"]);
            Assert.Equal("npc", result.sources["extensions.rememberTrade"]);
            Assert.Equal("session", result.sources["extensions.debugTag"]);
        }

        [Fact]
        public void StructuredFact_JsonContractRoundTrips()
        {
            var fact = new MemoryFact
            {
                id = "fact-1",
                category = "player_profile",
                key = "player.name",
                value = "小明",
                confidence = 0.95f,
                source = "player_statement",
                pinned = true
            };

            MemoryFact restored = JsonConvert.DeserializeObject<MemoryFact>(JsonConvert.SerializeObject(fact));

            Assert.Equal("player.name", restored.key);
            Assert.Equal("小明", restored.value);
            Assert.True(restored.pinned);
        }

        [Fact]
        public void Limits_DeduplicateCapabilitiesAndTrimValues()
        {
            var limits = new MemoryPolicyLimits
            {
                supportedSummaryTriggers = new List<string>
                {
                    " message_count ", "message_count", "token_count", "TOKEN_COUNT", ""
                },
                supportedMemoryScopes = new List<string>
                {
                    "session", " session ", "player_npc"
                }
            };

            EffectiveMemoryPolicy result = MemoryPolicyResolver.Resolve(null,
                new MemorySettings(), null, limits);

            Assert.Equal(new[] { "message_count", "token_count" },
                result.limits.supportedSummaryTriggers);
            Assert.Equal(new[] { "session", "player_npc" },
                result.limits.supportedMemoryScopes);
        }

        [Fact]
        public void ServerLimits_DeduplicateConfigurationArrays()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string>("Memory:SupportedSummaryTriggers:0", "message_count"),
                    new KeyValuePair<string, string>("Memory:SupportedSummaryTriggers:1", "message_count"),
                    new KeyValuePair<string, string>("Memory:SupportedMemoryScopes:0", "session"),
                    new KeyValuePair<string, string>("Memory:SupportedMemoryScopes:1", "session")
                })
                .Build();

            MemoryPolicyLimits limits = AIBot.Server.MemoryPolicyService.LoadLimits(configuration);

            Assert.Single(limits.supportedSummaryTriggers);
            Assert.Single(limits.supportedMemoryScopes);
        }
    }
}
