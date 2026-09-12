using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIBot.Core.Logging;
using Newtonsoft.Json;

namespace AIBot.Server
{
    /// <summary>
    /// Session 的 JSON 文件实现：sessions/{npcId}/{playerId}/{sessionId}.json（playerId 为空时走 legacy 路径）。
    /// 承接原 SessionStore 的全部文件 I/O：legacy 回退、文件时间兜底、v1 归档与闲置文件清理。
    /// </summary>
    public sealed class JsonSessionPersistence : ISessionPersistence
    {
        private readonly object _ioLock = new object();
        private readonly ILogSink _log = new ConsoleLogSink();
        private readonly Func<string> _dataRoot;

        public JsonSessionPersistence(Func<string> dataRoot)
        {
            _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        }

        public SessionStore.SessionFileDto Load(string gameId, string npcId, string playerId, string sessionId)
        {
            string path = FilePath(gameId, npcId, playerId, sessionId);
            string legacyPath = null;
            if (!string.IsNullOrEmpty(playerId) && (path == null || !File.Exists(path)))
            {
                legacyPath = FilePath(gameId, npcId, null, sessionId);
                if (legacyPath != null && File.Exists(legacyPath)) path = legacyPath;
            }
            if (path == null || !File.Exists(path)) return null;

            SessionStore.SessionFileDto dto =
                JsonConvert.DeserializeObject<SessionStore.SessionFileDto>(File.ReadAllText(path));
            if (dto == null) return null;
            dto.legacySourcePath = legacyPath;
            if (dto.lastActiveUtc == default(DateTime)) dto.lastActiveUtc = File.GetLastWriteTimeUtc(path);
            return dto;
        }

        public SessionSaveResult Save(string gameId, SessionStore.SessionFileDto dto)
        {
            if (dto == null) throw new ArgumentNullException(nameof(dto));
            var result = new SessionSaveResult();
            try
            {
                string path = FilePath(gameId, dto.npcId, dto.playerId, dto.sessionId);
                if (path == null) return result;
                lock (_ioLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    WriteAtomic(path, JsonConvert.SerializeObject(dto, Formatting.Indented));

                    // 只有旧长期字段已经成功迁出后，才归档 v1 文件。
                    if (!string.IsNullOrEmpty(dto.legacySourcePath)
                        && string.IsNullOrEmpty(dto.summary)
                        && (dto.facts == null || dto.facts.Count == 0)
                        && File.Exists(dto.legacySourcePath))
                    {
                        try
                        {
                            string backup = dto.legacySourcePath + ".migrated.bak";
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(dto.legacySourcePath, backup);
                            result.LegacyArchived = true;
                        }
                        catch (Exception ex)
                        {
                            // v2 文件已经安全写入；归档失败不应把本次持久化判为失败。
                            _log.Log(LogLevel.Warning, "旧会话归档失败，稍后重试: " + ex.Message);
                        }
                    }
                }
                result.Persisted = true;
                return result;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "会话保存失败(" + dto.sessionId + "): " + ex.Message);
                return result;
            }
        }

        public List<SessionStore.SessionFileDto> List(string gameId, string npcId, string playerId)
        {
            var result = new List<SessionStore.SessionFileDto>();
            foreach (string path in EnumerateSessionFiles(gameId))
            {
                try
                {
                    SessionStore.SessionFileDto dto =
                        JsonConvert.DeserializeObject<SessionStore.SessionFileDto>(File.ReadAllText(path));
                    if (dto == null || string.IsNullOrEmpty(dto.npcId) || string.IsNullOrEmpty(dto.sessionId)) continue;
                    if (npcId != null && dto.npcId != npcId) continue;
                    if (playerId != null && dto.playerId != playerId) continue;
                    if (dto.lastActiveUtc == default(DateTime)) dto.lastActiveUtc = File.GetLastWriteTimeUtc(path);
                    result.Add(dto);
                }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Warning, "跳过损坏的会话文件: " + path + " - " + ex.Message);
                }
            }
            return result;
        }

        public List<PendingMemorySession> ScanPending()
        {
            var result = new List<PendingMemorySession>();
            string root = _dataRoot();
            string gamesRoot = root == null ? null : Path.Combine(root, "games");
            if (gamesRoot == null || !Directory.Exists(gamesRoot)) return result;
            foreach (string gameDir in Directory.GetDirectories(gamesRoot))
            {
                string gid = Path.GetFileName(gameDir);
                if (!DataStore.IsValidId(gid)) continue;
                foreach (string path in EnumerateSessionFiles(gid))
                {
                    try
                    {
                        SessionStore.SessionFileDto dto =
                            JsonConvert.DeserializeObject<SessionStore.SessionFileDto>(File.ReadAllText(path));
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

        public bool Delete(string gameId, string npcId, string playerId, string sessionId)
        {
            string path = FilePath(gameId, npcId, playerId, sessionId);
            bool persisted = false;
            try
            {
                lock (_ioLock)
                {
                    persisted = path != null && File.Exists(path);
                    if (persisted) File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "会话文件删除失败(" + sessionId + "): " + ex.Message);
                throw new IOException("会话文件删除失败，内存状态已保留，可安全重试", ex);
            }
            return persisted;
        }

        public int DeleteExpired(TimeSpan idle, ISet<string> activeKeys, ISet<string> protectedPaths)
        {
            if (idle <= TimeSpan.Zero) return 0;
            DateTime cutoff = DateTime.UtcNow - idle;
            int removed = 0;
            string root = _dataRoot();
            string gamesRoot = root == null ? null : Path.Combine(root, "games");
            if (gamesRoot == null || !Directory.Exists(gamesRoot)) return 0;
            foreach (string gameDir in Directory.GetDirectories(gamesRoot))
            {
                string gid = Path.GetFileName(gameDir);
                if (!DataStore.IsValidId(gid)) continue;
                foreach (string path in EnumerateSessionFiles(gid))
                {
                    if (protectedPaths != null && protectedPaths.Contains(path)) continue;
                    try
                    {
                        DateTime fileTime = File.GetLastWriteTimeUtc(path);
                        if (fileTime >= cutoff) continue;
                        SessionStore.SessionFileDto dto =
                            JsonConvert.DeserializeObject<SessionStore.SessionFileDto>(File.ReadAllText(path));
                        if (dto == null) continue;
                        // 活跃会话（含以带 playerId 身份加载的 legacy 文件）不得因闲置删除。
                        string identity = SessionStore.IdentityKey(gid, dto.npcId, dto.playerId, dto.sessionId);
                        if (activeKeys != null && activeKeys.Contains(identity)) continue;
                        // 待摘要消息、processing 幂等请求或未消费的工具挂起轮仍可能带有业务副作用。
                        if ((dto.evictedMessages != null && dto.evictedMessages.Count > 0)
                            || dto.pendingToolRound != null
                            || (dto.recentRequests ?? new List<ChatRequestRecord>())
                                .Any(x => x != null && x.status == ChatRequestStatuses.Processing))
                            continue;
                        DateTime lastActive = dto.lastActiveUtc == default(DateTime)
                            ? fileTime : dto.lastActiveUtc.ToUniversalTime();
                        if (lastActive >= cutoff) continue;
                        lock (_ioLock)
                        {
                            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) < cutoff)
                            {
                                File.Delete(path);
                                removed++;
                            }
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (JsonException) { }
                }
            }
            return removed;
        }

        private IEnumerable<string> EnumerateSessionFiles(string gid)
        {
            string root = _dataRoot();
            string dir = root == null ? null : Path.Combine(root, "games", gid, "sessions");
            return dir != null && Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories)
                : Array.Empty<string>();
        }

        private string FilePath(string gid, string npcId, string playerId, string sid)
        {
            string root = _dataRoot();
            if (root == null) return null;
            string npcRoot = Path.Combine(root, "games", gid, "sessions", SafeName(npcId));
            return string.IsNullOrEmpty(playerId)
                ? Path.Combine(npcRoot, SafeName(sid) + ".json")
                : Path.Combine(npcRoot, SafeName(playerId), SafeName(sid) + ".json");
        }

        private static string SafeName(string id)
        {
            return Uri.EscapeDataString(id ?? "x");
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
    }
}
