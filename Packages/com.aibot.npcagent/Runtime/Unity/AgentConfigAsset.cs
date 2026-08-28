using System;
using System.Collections.Generic;
using AIBot.Core.Config;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBot.Unity
{
    public enum AgentRuntimeMode
    {
        Local,
        Server
    }

    /// <summary>人设配置的 Unity 内等价物：编辑体验 + 与 data/ JSON 互转（M3 补导入导出器 UI）。</summary>
    [CreateAssetMenu(menuName = "AI NPC/Agent Config", fileName = "NewNpcConfig")]
    public class AgentConfigAsset : ScriptableObject
    {
        public string npcId = "new_npc";
        public string displayName = "新NPC";
        [TextArea(2, 6)] public string persona;
        [TextArea(2, 6)] public string backstory;
        public string worldId = "default";

        [Header("运行模式")]
        [Tooltip("local 直连模型；server 通过 AIBot.Server 中转。")]
        public AgentRuntimeMode runtimeMode = AgentRuntimeMode.Local;
        public string serverBaseUrl = "http://127.0.0.1:5000";

        [Serializable]
        public class LoreEntry
        {
            public string title;
            [TextArea(2, 6)] public string content;
            public int unlockStage;
            public bool isSecret;
            public bool enabled = true;
        }
        public List<LoreEntry> loreBlocks = new List<LoreEntry>();

        public List<string> enabledToolIds = new List<string>();
        public List<string> fallbackReplies = new List<string>();

        [Header("模型")]
        public string baseUrl = "https://api.deepseek.com";
        public string model = "deepseek-chat";
        [Tooltip("仅开发期使用；正式项目建议通过环境变量或 Server 模式提供 Key。")]
        public string apiKey;
        [Range(0f, 2f)] public float temperature = 0.8f;
        public int maxTokens = 500;
        public int timeoutMs = 20000;

        [Header("记忆")]
        public int shortTermTurns = 12;
        public int summaryThreshold = 20;

        [Header("输出枚举（与游戏 Animator 参数对齐）")]
        public List<string> emotions = new List<string> { "neutral", "happy", "angry", "sad", "surprised" };
        public List<string> actions = new List<string> { "idle", "wave", "point", "offer" };

        private void OnValidate()
        {
            shortTermTurns = Mathf.Max(1, shortTermTurns);
            summaryThreshold = Mathf.Max(0, summaryThreshold);
            maxTokens = Mathf.Max(1, maxTokens);
            timeoutMs = Mathf.Max(1000, timeoutMs);
            loreBlocks = loreBlocks ?? new List<LoreEntry>();
            enabledToolIds = enabledToolIds ?? new List<string>();
            fallbackReplies = fallbackReplies ?? new List<string>();
            emotions = emotions ?? new List<string>();
            actions = actions ?? new List<string>();
        }

        public AgentConfigDto ToDto()
        {
            var dto = new AgentConfigDto
            {
                npcId = npcId,
                displayName = displayName,
                persona = persona,
                backstory = backstory,
                worldId = worldId,
                enabledToolIds = enabledToolIds,
                fallbackReplies = fallbackReplies,
                model = new ModelSettings
                {
                    baseUrl = baseUrl,
                    model = model,
                    apiKey = apiKey,
                    temperature = temperature,
                    maxTokens = maxTokens,
                    timeoutMs = timeoutMs
                },
                memory = new MemorySettings
                {
                    shortTermTurns = shortTermTurns,
                    summaryThreshold = summaryThreshold
                },
                output = new OutputSettings { emotions = emotions, actions = actions },
                runtimeMode = runtimeMode == AgentRuntimeMode.Server ? "server" : "local",
                serverBaseUrl = serverBaseUrl
            };
            foreach (LoreEntry entry in loreBlocks)
            {
                dto.loreBlocks.Add(new LoreBlock
                {
                    title = entry.title,
                    content = entry.content,
                    unlockStage = entry.unlockStage,
                    isSecret = entry.isSecret,
                    enabled = entry.enabled
                });
            }
            return dto;
        }

        public void FromDto(AgentConfigDto dto)
        {
            npcId = dto.npcId; displayName = dto.displayName;
            persona = dto.persona; backstory = dto.backstory; worldId = dto.worldId;
            runtimeMode = string.Equals(dto.runtimeMode, "server", StringComparison.OrdinalIgnoreCase)
                ? AgentRuntimeMode.Server : AgentRuntimeMode.Local;
            serverBaseUrl = string.IsNullOrEmpty(dto.serverBaseUrl) ? "http://127.0.0.1:5000" : dto.serverBaseUrl;
            enabledToolIds = dto.enabledToolIds; fallbackReplies = dto.fallbackReplies;
            baseUrl = dto.model.baseUrl; model = dto.model.model; apiKey = dto.model.apiKey;
            temperature = dto.model.temperature; maxTokens = dto.model.maxTokens; timeoutMs = dto.model.timeoutMs;
            shortTermTurns = dto.memory?.shortTermTurns ?? 12;
            summaryThreshold = dto.memory?.summaryThreshold ?? 20;
            emotions = dto.output.emotions; actions = dto.output.actions;
            loreBlocks = new List<LoreEntry>();
            foreach (LoreBlock block in dto.loreBlocks)
            {
                loreBlocks.Add(new LoreEntry
                {
                    title = block.title, content = block.content,
                    unlockStage = block.unlockStage, isSecret = block.isSecret, enabled = block.enabled
                });
            }
        }

        public string ToJson() { return JsonConvert.SerializeObject(ToDto(), Formatting.Indented); }
    }
}
