using System.IO;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIBot.Editor
{
    /// <summary>One-time editor import path for projects that previously loaded data/ JSON at runtime.</summary>
    public static class LegacyConfigImporter
    {
        [MenuItem("AIBot/Import Legacy NPC JSON as Local Asset...")]
        private static void ImportLocal()
        {
            string source = EditorUtility.OpenFilePanel("Select legacy NPC JSON", "", "json");
            if (string.IsNullOrEmpty(source)) return;
            AgentConfigDto dto = Read(source);
            if (dto == null) return;
            if (string.Equals(dto.runtimeMode, "server", System.StringComparison.OrdinalIgnoreCase)
                && !EditorUtility.DisplayDialog("旧配置是 Server 模式", "这份 JSON 声明为 Server 配置。继续将它导入为 Local Asset 会复制模型配置到客户端；推荐取消并使用 Server Profile 导入。", "仍导入 Local", "取消")) return;
            string gameFolder = Path.GetDirectoryName(Path.GetDirectoryName(source));
            string gamePolicyPath = Path.Combine(gameFolder, "memory-policy.json");
            string gamePolicyJson = null;
            if (File.Exists(gamePolicyPath))
            {
                try
                {
                    gamePolicyJson = File.ReadAllText(gamePolicyPath);
                    if (JsonConvert.DeserializeObject<MemoryPolicy>(gamePolicyJson) == null)
                        throw new InvalidDataException("memory-policy.json 内容为空");
                }
                catch (System.Exception ex)
                {
                    EditorUtility.DisplayDialog("Game 记忆策略无法导入", "memory-policy.json 解析失败，已取消导入以避免丢失记忆参数。\n" + ex.Message, "确定");
                    return;
                }
            }
            string worldPath = Path.Combine(gameFolder, "world.json");
            WorldConfigDto worldDto = null;
            if (File.Exists(worldPath))
            {
                try
                {
                    worldDto = JsonConvert.DeserializeObject<WorldConfigDto>(File.ReadAllText(worldPath));
                    if (worldDto == null) throw new InvalidDataException("world.json 内容为空");
                }
                catch (System.Exception ex)
                {
                    EditorUtility.DisplayDialog("世界观无法导入", "world.json 解析失败，已取消导入以避免丢失世界观。\n" + ex.Message, "确定");
                    return;
                }
            }
            string destination = EditorUtility.SaveFilePanelInProject("Save Local Agent Config", dto.npcId ?? "NpcConfig", "asset", "Choose an asset path");
            if (string.IsNullOrEmpty(destination)) return;
            var asset = ScriptableObject.CreateInstance<AIBot.Unity.AgentConfigAsset>();
            asset.FromDto(dto);
            asset.runtimeMode = AIBot.Unity.AgentRuntimeMode.Local;
            asset.gameMemoryPolicyJson = gamePolicyJson;
            AssetDatabase.CreateAsset(asset, destination);
            if (worldDto != null)
            {
                string assetFolder = Path.GetDirectoryName(destination).Replace('\\', '/');
                string worldAssetPath = AssetDatabase.GenerateUniqueAssetPath(assetFolder + "/" + Path.GetFileNameWithoutExtension(destination) + "WorldConfig.asset");
                var worldAsset = ScriptableObject.CreateInstance<AIBot.Unity.WorldConfigAsset>();
                worldAsset.worldId = worldDto.worldId;
                worldAsset.description = worldDto.description;
                worldAsset.extraRules = worldDto.extraRules ?? new System.Collections.Generic.List<string>();
                AssetDatabase.CreateAsset(worldAsset, worldAssetPath);
                asset.worldId = string.IsNullOrWhiteSpace(worldDto.worldId) ? asset.worldId : worldDto.worldId;
                asset.worldConfigAsset = worldAsset;
            }
            else Debug.Log("[AIBot] 相邻目录没有 world.json；Local Asset 将使用空世界观配置。");
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
        }

        [MenuItem("AIBot/Import Legacy NPC JSON as Server Profile...")]
        private static void ImportServer()
        {
            string source = EditorUtility.OpenFilePanel("Select legacy Server NPC JSON", "", "json");
            if (string.IsNullOrEmpty(source)) return;
            AgentConfigDto dto = Read(source);
            if (dto == null) return;
            string npcFolder = Path.GetDirectoryName(source);
            string gameFolder = Path.GetDirectoryName(npcFolder);
            string gamesFolder = Path.GetDirectoryName(gameFolder);
            if (!string.Equals(Path.GetFileName(npcFolder), "npcs", System.StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(gamesFolder), "games", System.StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("无法确定 Game ID", "Server JSON 必须位于 data/games/{gameId}/npcs/ 下。旧场景配置可选择 Hierarchy 中的 NpcAgent，使用转换菜单以保留 Game ID。", "确定");
                return;
            }
            string destination = EditorUtility.SaveFilePanelInProject("Save Server Connection Profile", dto.npcId ?? "ServerProfile", "asset", "Choose an asset path");
            if (string.IsNullOrEmpty(destination)) return;
            var asset = ScriptableObject.CreateInstance<AIBot.Unity.AIBotConnectionProfile>();
            asset.gameId = new DirectoryInfo(gameFolder).Name;
            asset.npcId = dto.npcId ?? Path.GetFileNameWithoutExtension(source);
            asset.serverBaseUrl = string.IsNullOrWhiteSpace(dto.serverBaseUrl) ? "http://127.0.0.1:5000" : dto.serverBaseUrl;
            asset.enabledToolIds = dto.enabledToolIds ?? new System.Collections.Generic.List<string>();
            asset.enableSimulatedTools = false;
            EditorUtility.DisplayDialog("Server Profile 导入", "已导入 Server 地址、Game/NPC 标识和工具 ID 声明。模型 Key 不会复制到 Profile；请在 Server 模型连接管理中确认集中连接配置。", "确定");
            AssetDatabase.CreateAsset(asset, destination);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
        }

        [MenuItem("AIBot/Convert Selected Legacy Server NpcAgent to Connection Profile...")]
        private static void ConvertSelectedServerAsset()
        {
            var selectedObject = Selection.activeGameObject;
            var agent = selectedObject == null ? null : selectedObject.GetComponent<AIBot.Unity.NpcAgent>();
            var sourceAsset = agent == null ? null : agent.configAsset;
            if (agent == null || sourceAsset == null)
            {
                EditorUtility.DisplayDialog("选择旧版 NPC", "请在 Hierarchy 选择引用旧版 Server AgentConfigAsset 的 NpcAgent。", "确定");
                return;
            }
            string destination = EditorUtility.SaveFilePanelInProject("Save Server Connection Profile", sourceAsset.npcId ?? "ServerProfile", "asset", "Choose an asset path");
            if (string.IsNullOrEmpty(destination)) return;
            var profile = ScriptableObject.CreateInstance<AIBot.Unity.AIBotConnectionProfile>();
            profile.gameId = agent.gameId;
            profile.npcId = sourceAsset.npcId ?? agent.npcId;
            profile.serverBaseUrl = string.IsNullOrWhiteSpace(sourceAsset.serverBaseUrl) ? "http://127.0.0.1:5000" : sourceAsset.serverBaseUrl;
            profile.enableSimulatedTools = sourceAsset.enableServerSimulatedTools;
            profile.enabledToolIds = sourceAsset.enabledToolIds == null
                ? new System.Collections.Generic.List<string>()
                : new System.Collections.Generic.List<string>(sourceAsset.enabledToolIds);
            AssetDatabase.CreateAsset(profile, destination);
            AssetDatabase.SaveAssets();
            agent.connectionProfile = profile;
            agent.configAsset = null;
            EditorUtility.SetDirty(agent);
            EditorSceneManager.MarkSceneDirty(agent.gameObject.scene);
            Selection.activeObject = profile;
            EditorUtility.DisplayDialog("Profile 已创建", "Profile 已指派到该 NpcAgent。旧 AgentConfigAsset 和其中的数据保留；其中的模型 Key 不会复制到 Profile。", "确定");
        }

        private static AgentConfigDto Read(string path)
        {
            try
            {
                AgentConfigDto dto = JsonConvert.DeserializeObject<AgentConfigDto>(File.ReadAllText(path));
                if (dto == null) throw new InvalidDataException("NPC JSON 内容为空");
                return dto;
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("NPC JSON 导入失败", ex.Message, "确定");
                return null;
            }
        }
    }
}
