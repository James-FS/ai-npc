using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Llm;
using AIBot.Core.Logging;
using AIBot.Core.Memory;
using Newtonsoft.Json;

namespace AIBot.Server
{
    /// <summary>单个会话的短期状态。长期摘要在 player/NPC 记忆文件中独立保存。</summary>
    public sealed class SessionState
    {
        public string GameId;
        public string NpcId;
        public string PlayerId;
        public string SessionId;
        public ShortTermMemory Memory;
        public string Summary;                 // 仅兼容旧 session；迁移成功后清空
        public List<string> Facts = new List<string>();
        public AIBot.Core.Context.SimGameState SimState = new AIBot.Core.Context.SimGameState();
        public DateTime LastActiveUtc = DateTime.UtcNow;
        internal string LegacySourcePath;
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    }

    public sealed class PendingMemorySession
    {
        public string GameId;
        public string NpcId;
        public string PlayerId;
        public string SessionId;
    }

    /// <summary>
    /// 会话注册表：玩家会话写入 sessions/{npcId}/{playerId}/{sessionId}.json；
    /// 未提供 playerId 时继续使用旧的 sessions/{npcId}/{sessionId}.json。
    /// </summary>
    public static class SessionStore
    {
        private static readonly ConcurrentDictionary<string, SessionState> Map =
            new ConcurrentDictionary<string, SessionState>();
        private static readonly object IoLock = new object();
        private static readonly ILogSink Log = new ConsoleLogSink();
        private static MySqlSessionPersistence MySqlPersistence;

        public static void UseMySql(MySqlConnectionFactory factory)
        {
            MySqlPersistence = factory == null ? null : new MySqlSessionPersistence(factory);
            Map.Clear();
        }

        public sealed class SessionFileDto
        {
            public int schemaVersion = 2;
            public string npcId;
            public string playerId;
            public string sessionId;
            public string summary;             // v1 兼容字段
            public List<string> facts = new List<string>();
            public AIBot.Core.Context.SimGameState simState;
            public List<LlmMessage> messages = new List<LlmMessage>();
            public List<LlmMessage> evictedMessages = new List<LlmMessage>();
            public DateTime lastActiveUtc;
        }

        private static string Key(string gid, string npcId, string playerId, string sid)
        {
            return gid + "|" + npcId + "|" + (playerId ?? "<legacy>") + "|" + sid;
        }

        private static string SafeName(string id)
        {
            return Uri.EscapeDataString(id ?? "x");
        }

        private static string FilePath(string gid, string npcId, string playerId, string sid)
        {
            string root = DataStore.FindDataRoot();
            if (root == null) return null;
            string npcRoot = Path.Combine(root, "games", gid, "sessions", SafeName(npcId));
            return string.IsNullOrEmpty(playerId)
                ? Path.Combine(npcRoot, SafeName(sid) + ".json")
                : Path.Combine(npcRoot, SafeName(playerId), SafeName(sid) + ".json");
        }

        public static SessionState GetOrCreate(string gid, string npcId, string sid, int maxTurns)
        {
            return GetOrCreate(gid, npcId, null, sid, maxTurns);
        }

        public static SessionState GetOrCreate(string gid, string npcId, string playerId, string sid, int maxTurns)
        {
            string key = Key(gid, npcId, playerId, sid);
            SessionState state = Map.GetOrAdd(key, _ => LoadFromDisk(gid, npcId, playerId, sid, maxTurns)
                ?? new SessionState
                {
                    GameId = gid,
                    NpcId = npcId,
                    PlayerId = playerId,
                    SessionId = sid,
                    Memory = new ShortTermMemory(ToMessageCapacity(maxTurns))
                });
            state.Memory.Resize(ToMessageCapacity(maxTurns));
            return state;
        }

        private static SessionState LoadFromDisk(string gid, string npcId, string playerId,
            string sid, int maxTurns)
        {
            if (MySqlPersistence != null)
            {
                try
                {
                    SessionFileDto mysqlDto = MySqlPersistence.Load(gid, npcId, playerId, sid);
                    return mysqlDto == null ? null : FromDto(gid, playerId, sid, maxTurns, mysqlDto, null);
                }
                catch (Exception ex)
                {
                    Log.Log(LogLevel.Warning, "MySQL 会话恢复失败(" + sid + ")，从空白开始: " + ex.Message);
                    return null;
                }
            }
            string path = FilePath(gid, npcId, playerId, sid);
            string legacyPath = null;
            if (!string.IsNullOrEmpty(playerId) && (path == null || !File.Exists(path)))
            {
                legacyPath = FilePath(gid, npcId, null, sid);
                if (legacyPath != null && File.Exists(legacyPath)) path = legacyPath;
            }
            if (path == null || !File.Exists(path)) return null;

            try
            {
                SessionFileDto dto = JsonConvert.DeserializeObject<SessionFileDto>(File.ReadAllText(path));
                if (dto == null) return null;
                return FromDto(gid, playerId, sid, maxTurns, dto, legacyPath,
                    File.GetLastWriteTimeUtc(path));
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "会话恢复失败(" + sid + ")，从空白开始: " + ex.Message);
                return null;
            }
        }

        /// <summary>原子落盘；待摘要队列也持久化，后台失败或重启后可继续处理。</summary>
        public static bool Save(SessionState session)
        {
            try
            {
                var dto = new SessionFileDto
                {
                    npcId = session.NpcId,
                    playerId = session.PlayerId,
                    sessionId = session.SessionId,
                    summary = session.Summary,
                    facts = session.Facts ?? new List<string>(),
                    simState = session.SimState,
                    messages = session.Memory.Messages.Select(CopyMessage).ToList(),
                    evictedMessages = session.Memory.SnapshotEvicted().Select(CopyMessage).ToList(),
                    lastActiveUtc = session.LastActiveUtc
                };
                if (MySqlPersistence != null)
                {
                    MySqlPersistence.Save(session.GameId, dto);
                    return true;
                }
                string path = FilePath(session.GameId, session.NpcId, session.PlayerId, session.SessionId);
                if (path == null) return false;
                lock (IoLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    WriteAtomic(path, JsonConvert.SerializeObject(dto, Formatting.Indented));

                    // 只有旧长期字段已经成功迁出后，才归档 v1 文件。
                    if (!string.IsNullOrEmpty(session.LegacySourcePath)
                        && string.IsNullOrEmpty(session.Summary)
                        && (session.Facts == null || session.Facts.Count == 0)
                        && File.Exists(session.LegacySourcePath))
                    {
                        try
                        {
                            string backup = session.LegacySourcePath + ".migrated.bak";
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(session.LegacySourcePath, backup);
                            session.LegacySourcePath = null;
                        }
                        catch (Exception ex)
                        {
                            // v2 文件已经安全写入；归档失败不应把本次持久化判为失败。
                            Log.Log(LogLevel.Warning, "旧会话归档失败，稍后重试: " + ex.Message);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "会话保存失败(" + session.SessionId + "): " + ex.Message);
                return false;
            }
        }

        public static List<SessionState> ListByGame(string gid, string npcId = null, string playerId = null)
        {
            var result = Map.Values
                .Where(s => s.GameId == gid
                    && (npcId == null || s.NpcId == npcId)
                    && (playerId == null || s.PlayerId == playerId))
                .ToDictionary(s => Key(s.GameId, s.NpcId, s.PlayerId, s.SessionId), s => s);

            if (MySqlPersistence != null)
            {
                foreach (SessionFileDto dto in MySqlPersistence.List(gid, npcId, playerId))
                {
                    if (dto == null || string.IsNullOrEmpty(dto.npcId) || string.IsNullOrEmpty(dto.sessionId)) continue;
                    string key = Key(gid, dto.npcId, dto.playerId, dto.sessionId);
                    if (!result.ContainsKey(key)) result[key] = FromDto(gid, dto.playerId,
                        dto.sessionId, Math.Max(1, (dto.messages?.Count ?? 0) / 2 + 1), dto, null);
                }
                return result.Values.OrderByDescending(s => s.LastActiveUtc).ToList();
            }

            foreach (string path in EnumerateSessionFiles(gid))
            {
                try
                {
                    SessionFileDto dto = JsonConvert.DeserializeObject<SessionFileDto>(File.ReadAllText(path));
                    if (dto == null || string.IsNullOrEmpty(dto.npcId) || string.IsNullOrEmpty(dto.sessionId)) continue;
                    if (npcId != null && dto.npcId != npcId) continue;
                    if (playerId != null && dto.playerId != playerId) continue;
                    string key = Key(gid, dto.npcId, dto.playerId, dto.sessionId);
                    if (result.ContainsKey(key)) continue;
                    var memory = new ShortTermMemory(Math.Max(2, (dto.messages?.Count ?? 0) + 2));
                    memory.RestoreEvicted((dto.evictedMessages ?? new List<LlmMessage>()).Select(CopyMessage));
                    foreach (LlmMessage message in dto.messages ?? new List<LlmMessage>()) memory.Add(CopyMessage(message));
                    result[key] = new SessionState
                    {
                        GameId = gid,
                        NpcId = dto.npcId,
                        PlayerId = dto.playerId,
                        SessionId = dto.sessionId,
                        Memory = memory,
                        Summary = dto.summary,
                        Facts = dto.facts ?? new List<string>(),
                        SimState = dto.simState ?? new AIBot.Core.Context.SimGameState(),
                        LastActiveUtc = dto.lastActiveUtc == default(DateTime) ? File.GetLastWriteTimeUtc(path) : dto.lastActiveUtc
                    };
                }
                catch (Exception ex)
                {
                    Log.Log(LogLevel.Warning, "跳过损坏的会话文件: " + path + " - " + ex.Message);
                }
            }
            return result.Values.OrderByDescending(s => s.LastActiveUtc).ToList();
        }

        public static List<PendingMemorySession> ScanPendingPlayerSessions()
        {
            if (MySqlPersistence != null) return MySqlPersistence.ScanPending();
            var result = new List<PendingMemorySession>();
            string root = DataStore.FindDataRoot();
            if (root == null) return result;
            string gamesRoot = Path.Combine(root, "games");
            if (!Directory.Exists(gamesRoot)) return result;
            foreach (string gameDir in Directory.GetDirectories(gamesRoot))
            {
                string gid = Path.GetFileName(gameDir);
                if (!DataStore.IsValidId(gid)) continue;
                foreach (string path in EnumerateSessionFiles(gid))
                {
                    try
                    {
                        SessionFileDto dto = JsonConvert.DeserializeObject<SessionFileDto>(File.ReadAllText(path));
                        if (dto == null || string.IsNullOrEmpty(dto.playerId)
                            || dto.evictedMessages == null || dto.evictedMessages.Count == 0) continue;
                        result.Add(new PendingMemorySession
                        {
                            GameId = gid,
                            NpcId = dto.npcId,
                            PlayerId = dto.playerId,
                            SessionId = dto.sessionId
                        });
                    }
                    catch (Exception) { }
                }
            }
            return result;
        }

        /// <summary>删除长期记忆时同时清除该玩家全部 Session 的窗口与待摘要批次，防止旧对话重新生成记忆。</summary>
        public static async Task<bool> ClearPlayerMemoryAsync(string gid, string npcId,
            string playerId, CancellationToken ct)
        {
            bool savedAll = true;
            foreach (SessionState session in ListByGame(gid, npcId, playerId))
            {
                await session.Gate.WaitAsync(ct);
                try
                {
                    session.Memory.Clear();
                    session.Summary = null;
                    session.Facts = new List<string>();
                    savedAll = Save(session) && savedAll;
                }
                finally { session.Gate.Release(); }
            }
            return savedAll;
        }

        public static bool Delete(string gid, string npcId, string sid)
        {
            return Delete(gid, npcId, null, sid);
        }

        public static bool Delete(string gid, string npcId, string playerId, string sid)
        {
            string key = Key(gid, npcId, playerId, sid);
            if (MySqlPersistence != null)
            {
                bool mysqlPersisted = MySqlPersistence.Delete(gid, npcId, playerId, sid);
                bool removedFromMemory = Map.TryRemove(key, out _);
                return mysqlPersisted || removedFromMemory;
            }
            string path = FilePath(gid, npcId, playerId, sid);
            bool persisted = false;
            try
            {
                lock (IoLock)
                {
                    persisted = path != null && File.Exists(path);
                    if (persisted) File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "会话文件删除失败(" + sid + "): " + ex.Message);
                throw new IOException("会话文件删除失败，内存状态已保留，可安全重试", ex);
            }
            bool removed = Map.TryRemove(key, out _);
            return persisted || removed;
        }

        public static int Count { get { return Map.Count; } }

        private static IEnumerable<string> EnumerateSessionFiles(string gid)
        {
            string root = DataStore.FindDataRoot();
            string dir = root == null ? null : Path.Combine(root, "games", gid, "sessions");
            return dir != null && Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories)
                : Array.Empty<string>();
        }

        private static LlmMessage CopyMessage(LlmMessage message)
        {
            return new LlmMessage { Role = message?.Role, Content = message?.Content };
        }

        private static SessionState FromDto(string gid, string playerId, string sid, int maxTurns,
            SessionFileDto dto, string legacySourcePath, DateTime? fileTime = null)
        {
            var memory = new ShortTermMemory(ToMessageCapacity(maxTurns));
            memory.RestoreEvicted((dto.evictedMessages ?? new List<LlmMessage>()).Select(CopyMessage));
            foreach (LlmMessage message in dto.messages ?? new List<LlmMessage>()) memory.Add(CopyMessage(message));
            return new SessionState
            {
                GameId = gid,
                NpcId = dto.npcId,
                PlayerId = playerId ?? dto.playerId,
                SessionId = sid,
                Memory = memory,
                Summary = dto.summary,
                Facts = dto.facts ?? new List<string>(),
                SimState = dto.simState ?? new AIBot.Core.Context.SimGameState(),
                LastActiveUtc = dto.lastActiveUtc == default(DateTime)
                    ? (fileTime ?? DateTime.UtcNow) : dto.lastActiveUtc,
                LegacySourcePath = legacySourcePath
            };
        }

        private static void WriteAtomic(string path, string content)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, content);
                if (File.Exists(path)) File.Move(temp, path, true);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static int ToMessageCapacity(int turns)
        {
            return Math.Max(2, turns * 2);
        }
    }
}
