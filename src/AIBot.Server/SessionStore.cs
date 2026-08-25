using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIBot.Core.Llm;
using AIBot.Core.Logging;
using AIBot.Core.Memory;
using Newtonsoft.Json;

namespace AIBot.Server
{
    /// <summary>单个会话的完整状态。</summary>
    public sealed class SessionState
    {
        public string GameId;
        public string NpcId;
        public string SessionId;
        public ShortTermMemory Memory;
        public string Summary;
        public List<string> Facts = new List<string>();
        public AIBot.Core.Context.SimGameState SimState = new AIBot.Core.Context.SimGameState();
        public DateTime LastActiveUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 会话注册表：内存缓存 + data/games/{gid}/sessions/{npcId}/{sid}.json 落盘。
    /// 重启后首次访问某会话时自动从磁盘恢复（消息窗口 + 摘要 + 事实）。
    /// </summary>
    public static class SessionStore
    {
        private static readonly ConcurrentDictionary<string, SessionState> Map =
            new ConcurrentDictionary<string, SessionState>();
        private static readonly object IoLock = new object();
        private static readonly ILogSink Log = new ConsoleLogSink();

        public sealed class SessionFileDto
        {
            public string npcId;
            public string sessionId;
            public string summary;
            public List<string> facts = new List<string>();
            public AIBot.Core.Context.SimGameState simState;
            public List<LlmMessage> messages = new List<LlmMessage>();   // 仅保留 role/content
        }

        private static string Key(string gid, string npcId, string sid)
        {
            return gid + "|" + npcId + "|" + sid;
        }

        private static string SafeName(string id)
        {
            return Uri.EscapeDataString(id ?? "x");
        }

        private static string FilePath(string gid, string npcId, string sid)
        {
            string root = DataStore.FindDataRoot();
            if (root == null) return null;
            return Path.Combine(root, "games", gid, "sessions", SafeName(npcId), SafeName(sid) + ".json");
        }

        public static SessionState GetOrCreate(string gid, string npcId, string sid, int maxTurns)
        {
            string key = Key(gid, npcId, sid);
            SessionState cached = Map.GetOrAdd(key, _ => LoadFromDisk(gid, npcId, sid, maxTurns)
                ?? new SessionState
                {
                    GameId = gid,
                    NpcId = npcId,
                    SessionId = sid,
                    Memory = new ShortTermMemory(maxTurns)
                });
            return cached;
        }

        private static SessionState LoadFromDisk(string gid, string npcId, string sid, int maxTurns)
        {
            try
            {
                string path = FilePath(gid, npcId, sid);
                if (path == null || !File.Exists(path)) return null;
                SessionFileDto dto = JsonConvert.DeserializeObject<SessionFileDto>(File.ReadAllText(path));
                if (dto == null) return null;
                var memory = new ShortTermMemory(maxTurns);
                foreach (LlmMessage m in dto.messages ?? new List<LlmMessage>())
                {
                    memory.Add(new LlmMessage { Role = m.Role, Content = m.Content });
                }
                return new SessionState
                {
                    GameId = gid,
                    NpcId = npcId,
                    SessionId = sid,
                    Memory = memory,
                    Summary = dto.summary,
                    Facts = dto.facts ?? new List<string>(),
                    SimState = dto.simState ?? new AIBot.Core.Context.SimGameState()
                };
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "会话恢复失败(" + sid + ")，从空白开始: " + ex.Message);
                return null;
            }
        }

        /// <summary>落盘（每轮对话后调用）。只存消息窗口与长期记忆，淘汰队列是瞬态的。</summary>
        public static void Save(SessionState session)
        {
            string path = FilePath(session.GameId, session.NpcId, session.SessionId);
            if (path == null) return;
            try
            {
                var dto = new SessionFileDto
                {
                    npcId = session.NpcId,
                    sessionId = session.SessionId,
                    summary = session.Summary,
                    facts = session.Facts,
                    simState = session.SimState,
                    messages = session.Memory.Messages
                        .Select(m => new LlmMessage { Role = m.Role, Content = m.Content })
                        .ToList()
                };
                lock (IoLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonConvert.SerializeObject(dto, Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "会话保存失败(" + session.SessionId + "): " + ex.Message);
            }
        }

        public static List<SessionState> ListByGame(string gid, string npcId = null)
        {
            var list = Map.Values
                .Where(s => s.GameId == gid && (npcId == null || s.NpcId == npcId))
                .OrderByDescending(s => s.LastActiveUtc)
                .ToList();

            // 磁盘上有、内存里没有的会话也列出来（重启后可见）
            try
            {
                string root = DataStore.FindDataRoot();
                if (root != null)
                {
                    string sessDir = Path.Combine(root, "games", gid, "sessions");
                    if (Directory.Exists(sessDir))
                    {
                        foreach (string npcDir in Directory.GetDirectories(sessDir))
                        {
                            string fileNpc = Path.GetFileName(npcDir);
                            string npcIdDecoded = Uri.UnescapeDataString(fileNpc);
                            if (npcId != null && npcIdDecoded != npcId) continue;
                            foreach (string file in Directory.GetFiles(npcDir, "*.json"))
                            {
                                string sid = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(file));
                                if (list.Any(s => s.SessionId == sid && s.NpcId == npcIdDecoded)) continue;
                                var stub = new SessionState
                                {
                                    GameId = gid, NpcId = npcIdDecoded, SessionId = sid,
                                    Memory = new ShortTermMemory(2)   // 占位：详情/恢复时按需加载
                                };
                                list.Add(stub);
                            }
                        }
                        list.Sort((a, b) => b.LastActiveUtc.CompareTo(a.LastActiveUtc));
                    }
                }
            }
            catch (Exception) { /* 目录缺失属正常 */ }
            return list;
        }

        public static bool Delete(string gid, string npcId, string sid)
        {
            bool removed = Map.TryRemove(Key(gid, npcId, sid), out _);
            string path = FilePath(gid, npcId, sid);
            try
            {
                if (path != null && File.Exists(path))
                {
                    lock (IoLock) { File.Delete(path); }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "会话文件删除失败(" + sid + "): " + ex.Message);
            }
            return removed;
        }

        public static int Count { get { return Map.Count; } }
    }
}
