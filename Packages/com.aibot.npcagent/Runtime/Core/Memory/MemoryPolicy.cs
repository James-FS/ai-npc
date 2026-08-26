using System;
using System.Collections.Generic;
using AIBot.Core.Config;
using Newtonsoft.Json.Linq;

namespace AIBot.Core.Memory
{
    public static class MemoryPolicyValues
    {
        public const string TriggerMessageCount = "message_count";
        public const string TriggerTokenCount = "token_count";
        public const string TriggerImportantEvent = "important_event";
        public const string TriggerConversationEnd = "conversation_end";

        public const string ScopeSession = "session";
        public const string ScopePlayerNpc = "player_npc";
    }

    /// <summary>完全解析后的具体记忆策略；运行期不再处理继承和 nullable 覆盖。</summary>
    public sealed class MemoryPolicy
    {
        public int shortTermTurns = 12;
        public int summaryThreshold = 20;
        public string summaryTrigger = MemoryPolicyValues.TriggerMessageCount;
        public string memoryScope = MemoryPolicyValues.ScopeSession;
        public int maxFacts = 8;
        public bool rememberPlayerProfile = true;
        public bool rememberPromises = true;
        public bool rememberQuestEvents = true;
        public bool rememberCasualChat;
        public bool backgroundSummarization;
        public ModelSettings summaryModel;
        public JObject extensions = new JObject();

        public static MemoryPolicy Defaults() { return new MemoryPolicy(); }

        public MemoryPolicy Clone()
        {
            return new MemoryPolicy
            {
                shortTermTurns = shortTermTurns,
                summaryThreshold = summaryThreshold,
                summaryTrigger = summaryTrigger,
                memoryScope = memoryScope,
                maxFacts = maxFacts,
                rememberPlayerProfile = rememberPlayerProfile,
                rememberPromises = rememberPromises,
                rememberQuestEvents = rememberQuestEvents,
                rememberCasualChat = rememberCasualChat,
                backgroundSummarization = backgroundSummarization,
                summaryModel = CloneModel(summaryModel),
                extensions = extensions != null ? (JObject)extensions.DeepClone() : new JObject()
            };
        }

        internal static ModelSettings CloneModel(ModelSettings source)
        {
            if (source == null) return null;
            return new ModelSettings
            {
                baseUrl = source.baseUrl,
                apiKey = source.apiKey,
                model = source.model,
                temperature = source.temperature,
                maxTokens = source.maxTokens,
                timeoutMs = source.timeoutMs
            };
        }
    }

    /// <summary>NPC/Session 可空覆盖；null 表示继承下一级配置。</summary>
    public class MemoryPolicyOverrides
    {
        public int? shortTermTurns;
        public int? summaryThreshold;
        public string summaryTrigger;
        public string memoryScope;
        public int? maxFacts;
        public bool? rememberPlayerProfile;
        public bool? rememberPromises;
        public bool? rememberQuestEvents;
        public bool? rememberCasualChat;
        public bool? backgroundSummarization;
        public ModelSettings summaryModel;
        public bool? useMainSummaryModel;
        public JObject extensions;
    }

    /// <summary>Server 不可突破的边界，同时声明当前版本真正支持的策略能力。</summary>
    public sealed class MemoryPolicyLimits
    {
        public int maxShortTermTurns = 50;
        public int maxSummaryThreshold = 200;
        public int maxFacts = 20;
        public bool allowBackgroundSummarization;
        public List<string> supportedSummaryTriggers = new List<string>
        {
            MemoryPolicyValues.TriggerMessageCount
        };
        public List<string> supportedMemoryScopes = new List<string>
        {
            MemoryPolicyValues.ScopeSession
        };
    }

    public sealed class EffectiveMemoryPolicy
    {
        public MemoryPolicy policy = MemoryPolicy.Defaults();
        public Dictionary<string, string> sources = new Dictionary<string, string>();
        public List<string> adjustments = new List<string>();
        public MemoryPolicyLimits limits = new MemoryPolicyLimits();
    }

    /// <summary>Server/Game/NPC/Session 四级策略合并器；不读取文件，便于三端共享与单测。</summary>
    public static class MemoryPolicyResolver
    {
        private const string CoreSource = "core";

        public static EffectiveMemoryPolicy Resolve(MemoryPolicy gamePolicy, MemorySettings npcPolicy,
            MemoryPolicyOverrides sessionOverride, MemoryPolicyLimits limits = null)
        {
            limits = limits ?? new MemoryPolicyLimits();
            var result = new EffectiveMemoryPolicy
            {
                policy = MemoryPolicy.Defaults(),
                limits = CloneLimits(limits)
            };
            InitializeSources(result.sources, CoreSource);

            bool inheritGame = npcPolicy != null && npcPolicy.inheritGameDefaults;
            if (inheritGame && gamePolicy != null) ApplyConcrete(result, gamePolicy, "game");
            if (npcPolicy != null) ApplyOverrides(result, npcPolicy, "npc");
            if (sessionOverride != null) ApplyOverrides(result, sessionOverride, "session");
            ApplyLimits(result);
            return result;
        }

        private static void ApplyConcrete(EffectiveMemoryPolicy result, MemoryPolicy value, string source)
        {
            result.policy = value.Clone();
            InitializeSources(result.sources, source);
            if (value.extensions != null)
            {
                foreach (JProperty property in value.extensions.Properties())
                    result.sources["extensions." + property.Name] = source;
            }
        }

        private static void ApplyOverrides(EffectiveMemoryPolicy result, MemoryPolicyOverrides value, string source)
        {
            MemoryPolicy p = result.policy;
            if (value.shortTermTurns.HasValue) Set(result, "shortTermTurns", source, () => p.shortTermTurns = value.shortTermTurns.Value);
            if (value.summaryThreshold.HasValue) Set(result, "summaryThreshold", source, () => p.summaryThreshold = value.summaryThreshold.Value);
            if (value.summaryTrigger != null) Set(result, "summaryTrigger", source, () => p.summaryTrigger = value.summaryTrigger);
            if (value.memoryScope != null) Set(result, "memoryScope", source, () => p.memoryScope = value.memoryScope);
            if (value.maxFacts.HasValue) Set(result, "maxFacts", source, () => p.maxFacts = value.maxFacts.Value);
            if (value.rememberPlayerProfile.HasValue) Set(result, "rememberPlayerProfile", source, () => p.rememberPlayerProfile = value.rememberPlayerProfile.Value);
            if (value.rememberPromises.HasValue) Set(result, "rememberPromises", source, () => p.rememberPromises = value.rememberPromises.Value);
            if (value.rememberQuestEvents.HasValue) Set(result, "rememberQuestEvents", source, () => p.rememberQuestEvents = value.rememberQuestEvents.Value);
            if (value.rememberCasualChat.HasValue) Set(result, "rememberCasualChat", source, () => p.rememberCasualChat = value.rememberCasualChat.Value);
            if (value.backgroundSummarization.HasValue) Set(result, "backgroundSummarization", source, () => p.backgroundSummarization = value.backgroundSummarization.Value);
            if (value.useMainSummaryModel == true)
                Set(result, "summaryModel", source, () => p.summaryModel = null);
            else if (value.summaryModel != null)
                Set(result, "summaryModel", source, () => p.summaryModel = MemoryPolicy.CloneModel(value.summaryModel));

            if (value.extensions != null)
            {
                if (p.extensions == null) p.extensions = new JObject();
                foreach (JProperty property in value.extensions.Properties())
                {
                    p.extensions[property.Name] = property.Value.DeepClone();
                    result.sources["extensions." + property.Name] = source;
                }
            }
        }

        private static void ApplyLimits(EffectiveMemoryPolicy result)
        {
            MemoryPolicy p = result.policy;
            MemoryPolicyLimits limits = result.limits;
            Clamp(result, "shortTermTurns", ref p.shortTermTurns, 1, Math.Max(1, limits.maxShortTermTurns));
            Clamp(result, "summaryThreshold", ref p.summaryThreshold, 0, Math.Max(0, limits.maxSummaryThreshold));
            Clamp(result, "maxFacts", ref p.maxFacts, 1, Math.Max(1, limits.maxFacts));

            if (!Contains(limits.supportedSummaryTriggers, p.summaryTrigger))
            {
                Adjust(result, "summaryTrigger", p.summaryTrigger, MemoryPolicyValues.TriggerMessageCount);
                p.summaryTrigger = MemoryPolicyValues.TriggerMessageCount;
            }
            if (!Contains(limits.supportedMemoryScopes, p.memoryScope))
            {
                Adjust(result, "memoryScope", p.memoryScope, MemoryPolicyValues.ScopeSession);
                p.memoryScope = MemoryPolicyValues.ScopeSession;
            }
            if (p.backgroundSummarization && !limits.allowBackgroundSummarization)
            {
                Adjust(result, "backgroundSummarization", "true", "false");
                p.backgroundSummarization = false;
            }
        }

        private static void Clamp(EffectiveMemoryPolicy result, string field, ref int value, int min, int max)
        {
            int before = value;
            if (value < min) value = min;
            if (value > max) value = max;
            if (before != value) Adjust(result, field, before.ToString(), value.ToString());
        }

        private static void Adjust(EffectiveMemoryPolicy result, string field, string before, string after)
        {
            string originalSource = result.sources.TryGetValue(field, out string source) ? source : CoreSource;
            result.sources[field] = "server-limit:" + originalSource;
            result.adjustments.Add(field + ": " + (before ?? "null") + " -> " + (after ?? "null"));
        }

        private static void Set(EffectiveMemoryPolicy result, string field, string source, Action setter)
        {
            setter();
            result.sources[field] = source;
        }

        private static bool Contains(List<string> values, string value)
        {
            return values != null && value != null && values.Contains(value);
        }

        private static void InitializeSources(Dictionary<string, string> sources, string source)
        {
            string[] fields =
            {
                "shortTermTurns", "summaryThreshold", "summaryTrigger", "memoryScope", "maxFacts",
                "rememberPlayerProfile", "rememberPromises", "rememberQuestEvents", "rememberCasualChat",
                "backgroundSummarization", "summaryModel"
            };
            foreach (string field in fields) sources[field] = source;
        }

        private static MemoryPolicyLimits CloneLimits(MemoryPolicyLimits source)
        {
            return new MemoryPolicyLimits
            {
                maxShortTermTurns = source.maxShortTermTurns,
                maxSummaryThreshold = source.maxSummaryThreshold,
                maxFacts = source.maxFacts,
                allowBackgroundSummarization = source.allowBackgroundSummarization,
                supportedSummaryTriggers = source.supportedSummaryTriggers != null
                    ? new List<string>(source.supportedSummaryTriggers) : new List<string>(),
                supportedMemoryScopes = source.supportedMemoryScopes != null
                    ? new List<string>(source.supportedMemoryScopes) : new List<string>()
            };
        }
    }
}
