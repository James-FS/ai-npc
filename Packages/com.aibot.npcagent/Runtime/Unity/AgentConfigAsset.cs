using System;
using System.Collections.Generic;
using AIBot.Core.Config;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBot.Unity
{
    /// <summary>人设配置的 Unity 内等价物：编辑体验 + 与 data/ JSON 互转（M3 补导入导出器 UI）。</summary>
    [CreateAssetMenu(menuName = "AI NPC/Agent Config", fileName = "NewNpcConfig")]
    public class AgentConfigAsset : ScriptableObject
    {
        public string npcId = "new_npc";
        public string displayName = "新NPC";
        [TextArea(2, 6)] public string persona;
        [TextArea(2, 6)] public string backstory;
        public string worldId = "default";

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
        [Range(0f, 2f)] public float temperature = 0.8f;
        public int maxTokens = 500;

        [Header("记忆")]
        public int shortTermTurns = 12;

        [Header("输出枚举（与游戏 Animator 参数对齐）")]
        public List<string> emotions = new List<string> { "neutral", "happy", "angry", "sad", "surprised" };
        public List<string> actions = new List<string> { "idle", "wave", "point", "offer" };

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
                    temperature = temperature,
                    maxTokens = maxTokens
                },
                memory = new MemorySettings { shortTermTurns = shortTermTurns },
                output = new OutputSettings { emotions = emotions, actions = actions }
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
            enabledToolIds = dto.enabledToolIds; fallbackReplies = dto.fallbackReplies;
            baseUrl = dto.model.baseUrl; model = dto.model.model;
            temperature = dto.model.temperature; maxTokens = dto.model.maxTokens;
            shortTermTurns = dto.memory.shortTermTurns;
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
