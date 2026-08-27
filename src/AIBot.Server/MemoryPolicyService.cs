using System.Collections.Generic;
using System;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace AIBot.Server
{
    /// <summary>Server/Game/NPC/Session 四级记忆配置解析与敏感字段过滤。</summary>
    public static class MemoryPolicyService
    {
        public static EffectiveMemoryPolicy Resolve(string gameId, AgentConfigDto npc,
            MemoryPolicyOverrides sessionOverride, IConfiguration configuration)
        {
            MemoryPolicy gamePolicy = DataStore.LoadMemoryPolicy(gameId);
            MemorySettings npcPolicy = npc?.memory ?? new MemorySettings();
            return MemoryPolicyResolver.Resolve(gamePolicy, npcPolicy, sessionOverride,
                LoadLimits(configuration));
        }

        public static MemoryPolicyLimits LoadLimits(IConfiguration configuration)
        {
            return new MemoryPolicyLimits
            {
                maxShortTermTurns = configuration.GetValue<int?>("Memory:MaxShortTermTurns") ?? 50,
                maxSummaryThreshold = configuration.GetValue<int?>("Memory:MaxSummaryThreshold") ?? 200,
                maxFacts = configuration.GetValue<int?>("Memory:MaxFacts") ?? 20,
                allowBackgroundSummarization = configuration.GetValue<bool?>("Memory:AllowBackgroundSummarization") ?? false,
                supportedSummaryTriggers = ReadList(configuration, "Memory:SupportedSummaryTriggers",
                    new List<string> { MemoryPolicyValues.TriggerMessageCount }),
                supportedMemoryScopes = ReadList(configuration, "Memory:SupportedMemoryScopes",
                    new List<string> { MemoryPolicyValues.ScopeSession })
            };
        }

        public static EffectiveMemoryPolicy Redact(EffectiveMemoryPolicy source)
        {
            EffectiveMemoryPolicy clone = JsonConvert.DeserializeObject<EffectiveMemoryPolicy>(
                JsonConvert.SerializeObject(source));
            if (clone?.policy?.summaryModel != null) clone.policy.summaryModel.apiKey = string.Empty;
            return clone;
        }

        public static MemoryPolicy Redact(MemoryPolicy source)
        {
            if (source == null) return null;
            MemoryPolicy clone = JsonConvert.DeserializeObject<MemoryPolicy>(JsonConvert.SerializeObject(source));
            if (clone.summaryModel != null) clone.summaryModel.apiKey = string.Empty;
            return clone;
        }

        private static List<string> ReadList(IConfiguration configuration, string key, List<string> fallback)
        {
            string[] values = configuration.GetSection(key).Get<string[]>();
            List<string> source = values != null && values.Length > 0 ? new List<string>(values) : fallback;
            // 配置提供程序可能同时合并数组索引和逗号分隔值；对外能力契约必须稳定且无重复项。
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in source ?? new List<string>())
            {
                string normalized = value?.Trim();
                if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized)) result.Add(normalized);
            }
            return result.Count > 0 ? result : new List<string>(fallback ?? new List<string>());
        }
    }
}
