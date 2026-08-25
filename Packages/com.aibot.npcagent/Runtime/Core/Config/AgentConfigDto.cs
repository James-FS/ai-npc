using System.Collections.Generic;

namespace AIBot.Core.Config
{
    /// <summary>NPC 人设配置（data/games/{gid}/npcs/{npcId}.json）。字段名与 TS 端一一对应。</summary>
    public class AgentConfigDto
    {
        public string npcId;
        public string displayName;
        public string persona;
        public string backstory;
        public string worldId = "default";
        public List<LoreBlock> loreBlocks = new List<LoreBlock>();
        public List<string> enabledToolIds = new List<string>();
        public List<string> fallbackReplies = new List<string>();
        public ModelSettings model = new ModelSettings();
        public MemorySettings memory = new MemorySettings();
        public OutputSettings output = new OutputSettings();
        public int configVersion = 1;
    }

    public class LoreBlock
    {
        public string title;
        public string content;
        public int unlockStage;              // 阶段过滤：unlockStage ≤ 当前剧情阶段才注入
        public bool isSecret;                // 秘密：除非条件满足，不主动透露
        public bool enabled = true;          // 临时停用而不删除
    }

    public class ModelSettings
    {
        public string baseUrl = "https://api.deepseek.com";
        public string apiKey;                // Unity 开发期本地填；Server 侧从环境变量/配置注入
        public string model = "deepseek-chat";
        public float temperature = 0.8f;
        public int maxTokens = 500;
        public int timeoutMs = 20000;
    }

    public class MemorySettings
    {
        public int shortTermTurns = 12;
        public int summaryThreshold = 20;
        public ModelSettings summaryModel;   // 为空则复用主模型
    }

    public class OutputSettings
    {
        public List<string> emotions = new List<string> { "neutral", "happy", "angry", "sad", "surprised" };
        public List<string> actions = new List<string> { "idle", "wave", "point", "offer" };
    }

    /// <summary>全局世界观（data/games/{gid}/world.json），同游戏 NPC 共享。</summary>
    public class WorldConfigDto
    {
        public string worldId = "default";
        public string description = "";
        public List<string> extraRules = new List<string>();
    }
}
