using System;
using System.Collections.Generic;
using System.IO;
using AIBot.Core.Config;
using AIBot.Core.Logging;
using AIBot.Core.Memory;
using Newtonsoft.Json;

namespace AIBot.Server
{
    /// <summary>定位并读取 data/ 目录（配置唯一真源）。可用环境变量 AIBOT_DATA_ROOT 显式指定。</summary>
    public static class DataStore
    {
        private static string _cachedRoot;
        private static readonly ILogSink Log = new ConsoleLogSink();
        private static readonly object IoLock = new object();

        /// <summary>npcId/gameId 合法性（同时防路径穿越）：字母数字下划线短横线，1~64 位。</summary>
        public static bool IsValidId(string id)
        {
            return !string.IsNullOrEmpty(id)
                && id.Length <= 64
                && System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]+$");
        }

        public static bool IsValidSessionId(string id)
        {
            return !string.IsNullOrEmpty(id)
                && id.Length <= 128
                && System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_.:-]+$");
        }

        /// <summary>playerId 使用稳定内部 ID；允许与 sessionId 相同字符集，最长 128 位。</summary>
        public static bool IsValidPlayerId(string id)
        {
            return IsValidSessionId(id);
        }

        private static string NpcDir(string gameId, bool create)
        {
            if (!IsValidId(gameId)) return null;
            string root = FindDataRoot();
            if (root == null) { Log.Log(LogLevel.Error, "data/ 根目录未找到，设置 AIBOT_DATA_ROOT"); return null; }
            string dir = Path.Combine(root, "games", gameId, "npcs");
            if (create) Directory.CreateDirectory(dir);
            return dir;
        }

        public static string FindDataRoot()
        {
            if (_cachedRoot != null) return _cachedRoot;

            string fromEnv = Environment.GetEnvironmentVariable("AIBOT_DATA_ROOT");
            if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv)) return _cachedRoot = fromEnv;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "data");
                if (Directory.Exists(Path.Combine(candidate, "games"))) return _cachedRoot = candidate;
            }
            return null;
        }

        public static AgentConfigDto LoadNpc(string gameId, string npcId)
        {
            if (!IsValidId(gameId) || !IsValidId(npcId)) return null;
            string root = FindDataRoot();
            if (root == null) { Log.Log(LogLevel.Error, "data/ 根目录未找到，设置 AIBOT_DATA_ROOT"); return null; }
            string path = Path.Combine(root, "games", gameId, "npcs", npcId + ".json");
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<AgentConfigDto>(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Error, "NPC 配置解析失败: " + path, ex);
                return null;
            }
        }

        public static WorldConfigDto LoadWorld(string gameId, string worldId)
        {
            if (!IsValidId(gameId)) return new WorldConfigDto { worldId = worldId };
            string root = FindDataRoot();
            string path = root == null ? null : Path.Combine(root, "games", gameId, "world.json");
            if (path == null || !File.Exists(path)) return new WorldConfigDto { worldId = worldId };
            try
            {
                return JsonConvert.DeserializeObject<WorldConfigDto>(File.ReadAllText(path))
                    ?? new WorldConfigDto { worldId = worldId };
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Error, "世界观配置解析失败: " + path, ex);
                return new WorldConfigDto { worldId = worldId };
            }
        }

        public static MemoryPolicy LoadMemoryPolicy(string gameId)
        {
            if (!IsValidId(gameId)) return null;
            string root = FindDataRoot();
            string path = root == null ? null : Path.Combine(root, "games", gameId, "memory-policy.json");
            if (path == null || !File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<MemoryPolicy>(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Error, "Game 记忆策略解析失败: " + path, ex);
                return null;
            }
        }

        public static bool SaveMemoryPolicy(string gameId, MemoryPolicy policy)
        {
            string root = FindDataRoot();
            if (root == null || policy == null || !IsValidId(gameId)) return false;
            string dir = Path.Combine(root, "games", gameId);
            Directory.CreateDirectory(dir);
            lock (IoLock)
            {
                File.WriteAllText(Path.Combine(dir, "memory-policy.json"),
                    JsonConvert.SerializeObject(policy, Formatting.Indented));
            }
            return true;
        }

        public static List<string> ListNpcIds(string gameId)
        {
            var ids = new List<string>();
            if (!IsValidId(gameId)) return ids;
            string root = FindDataRoot();
            string dir = root == null ? null : Path.Combine(root, "games", gameId, "npcs");
            if (dir == null || !Directory.Exists(dir)) return ids;
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                if (file.EndsWith(".template.json")) continue;    // 模板不是可聊的 NPC
                try
                {
                    AgentConfigDto dto = JsonConvert.DeserializeObject<AgentConfigDto>(File.ReadAllText(file));
                    if (dto != null && IsValidId(dto.npcId)) ids.Add(dto.npcId);
                }
                catch (Exception ex)
                {
                    Log.Log(LogLevel.Warning, "跳过损坏的 NPC 配置: " + file, ex);
                }
            }
            return ids;
        }

        /// <summary>列出 data/games/ 下全部 Game 目录名（管理台 Game 选择器用）。</summary>
        public static List<string> ListGameIds()
        {
            var ids = new List<string>();
            string root = FindDataRoot();
            string dir = root == null ? null : Path.Combine(root, "games");
            if (dir == null || !Directory.Exists(dir)) return ids;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (IsValidId(name)) ids.Add(name);
            }
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }

        /// <summary>保存 NPC 配置（覆盖写，缩进格式便于人工检查）。</summary>
        public static bool SaveNpc(string gameId, AgentConfigDto dto)
        {
            if (!IsValidId(gameId) || dto == null || !IsValidId(dto.npcId)) return false;
            string dir = NpcDir(gameId, true);
            if (dir == null) return false;
            string path = Path.Combine(dir, dto.npcId + ".json");
            lock (IoLock)
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(dto, Formatting.Indented));
            }
            return true;
        }

        public static bool DeleteNpc(string gameId, string npcId)
        {
            if (!IsValidId(gameId) || !IsValidId(npcId)) return false;
            string dir = NpcDir(gameId, false);
            if (dir == null) return false;
            string path = Path.Combine(dir, npcId + ".json");
            if (!File.Exists(path)) return false;
            lock (IoLock) { File.Delete(path); }
            return true;
        }

        public static bool SaveWorld(string gameId, WorldConfigDto world)
        {
            string root = FindDataRoot();
            if (root == null || world == null || !IsValidId(gameId)) return false;
            string dir = Path.Combine(root, "games", gameId);
            Directory.CreateDirectory(dir);
            lock (IoLock)
            {
                File.WriteAllText(Path.Combine(dir, "world.json"), JsonConvert.SerializeObject(world, Formatting.Indented));
            }
            return true;
        }

        /// <summary>读取 NPC 创建模板（不存在则返回内置默认）。</summary>
        public static AgentConfigDto LoadTemplate(string gameId)
        {
            if (!IsValidId(gameId)) return null;
            string root = FindDataRoot();
            string path = root == null ? null : Path.Combine(root, "games", gameId, "npcs", "new_npc.template.json");
            if (path != null && File.Exists(path))
            {
                try
                {
                    AgentConfigDto dto = JsonConvert.DeserializeObject<AgentConfigDto>(File.ReadAllText(path));
                    if (dto != null) return dto;
                }
                catch (Exception ex)
                {
                    Log.Log(LogLevel.Warning, "NPC 模板解析失败，使用内置模板: " + path, ex);
                }
            }
            return new AgentConfigDto
            {
                npcId = "new_npc",
                displayName = "新NPC",
                persona = "（填写性格与说话风格）",
                backstory = "（填写背景故事）",
                fallbackReplies = { "（……）" }
            };
        }
    }
}

