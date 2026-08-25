using System.Collections.Generic;
using System.IO;
using AIBot.Core.Config;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBot.Unity
{
    /// <summary>
    /// 开发期配置加载：定位 monorepo 的 data/ 根目录（主方案 §6.2 三段管线的第一段）。
    /// 候选顺序：手动 Override → 常见工程层级 → StreamingAssets/aibot（发布期拷贝目标）。
    /// </summary>
    public static class DevConfigStore
    {
        public static string DataRootOverride;

        private static string[] Candidates()
        {
            if (!string.IsNullOrEmpty(DataRootOverride)) return new[] { DataRootOverride };
            string assets = Application.dataPath;
            return new[]
            {
                Path.Combine(assets, "..", "data"),            // Unity 工程根即 monorepo
                Path.Combine(assets, "..", "..", "data"),      // 工程在 monorepo 子目录（如 unity-demo/）
                Path.Combine(assets, "..", "..", "..", "data"),
                Path.Combine(Application.streamingAssetsPath, "aibot")
            };
        }

        public static string FindDataRoot()
        {
            foreach (string candidate in Candidates())
            {
                if (Directory.Exists(candidate)) return candidate;
            }
            Debug.LogWarning("[AIBot] 未找到 data/ 根目录，可设置 DevConfigStore.DataRootOverride。候选："
                + string.Join(" | ", Candidates()));
            return null;
        }

        public static WorldConfigDto LoadWorld(string gameId, string worldId)
        {
            string root = FindDataRoot();
            if (root == null) return new WorldConfigDto { worldId = worldId };
            string path = Path.Combine(root, "games", gameId, "world.json");
            if (!File.Exists(path)) return new WorldConfigDto { worldId = worldId };
            return JsonConvert.DeserializeObject<WorldConfigDto>(File.ReadAllText(path));
        }

        public static AgentConfigDto LoadNpc(string gameId, string npcId)
        {
            string root = FindDataRoot();
            if (root == null) return null;
            string path = Path.Combine(root, "games", gameId, "npcs", npcId + ".json");
            if (!File.Exists(path))
            {
                Debug.LogWarning("[AIBot] NPC 配置不存在：" + path);
                return null;
            }
            return JsonConvert.DeserializeObject<AgentConfigDto>(File.ReadAllText(path));
        }

        /// <summary>列出某游戏下全部 NPC 配置（编辑器窗口/测试用）。</summary>
        public static List<AgentConfigDto> LoadAllNpcs(string gameId)
        {
            var result = new List<AgentConfigDto>();
            string root = FindDataRoot();
            if (root == null) return result;
            string dir = Path.Combine(root, "games", gameId, "npcs");
            if (!Directory.Exists(dir)) return result;
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                if (file.EndsWith(".template.json")) continue;    // 模板不是可聊的 NPC
                AgentConfigDto dto = JsonConvert.DeserializeObject<AgentConfigDto>(File.ReadAllText(file));
                if (dto != null) result.Add(dto);
            }
            return result;
        }
    }
}
