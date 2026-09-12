using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Logging;
using AIBot.Core.Llm;
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
        public List<ChatRequestRecord> RecentRequests = new List<ChatRequestRecord>();
        public PendingToolRound PendingToolRound;   // game 模式：非空表示有未消费的工具挂起轮
        internal string LegacySourcePath;
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    }

    /// <summary>持久化的一轮聊天请求状态，用于 requestId 去重与断线后的 SSE 重放。</summary>
    public sealed class ChatRequestRecord
    {
        public string requestId;
        public string fingerprint;
        public string status;
        public List<string> events = new List<string>();
        public DateTime createdUtc;
        public DateTime completedUtc;
    }

    /// <summary>game 模式的挂起工具轮：等待客户端执行工具并携带结果发起续跑请求。</summary>
    public sealed class PendingToolRound
    {
        public string roundToken;                              // "<首个requestId>#<roundIndex>"
        public int roundIndex;                                 // 链式轮数计数，防绕过工具轮上限
        public List<ToolCallDto> calls = new List<ToolCallDto>();
        public List<LlmMessage> messages = new List<LlmMessage>();  // 续跑所需完整消息列表（含旧 system）
        public List<ToolSchema> schemas = new List<ToolSchema>();   // 续跑轮继续向模型提供工具
        public DateTime createdUtc;
    }

    public static class ChatRequestStatuses
    {
        public const string Processing = "processing";
        public const string Completed = "completed";
        public const string Failed = "failed";
    }

    public sealed class PendingMemorySession
    {
        public string GameId;
        public string NpcId;
        public string PlayerId;
        public string SessionId;
    }

    /// <summary>
    /// 会话注册表门面：进程内 Map + 可插拔的 <see cref="ISessionPersistence"/>（JSON / MongoDB）。
    /// 默认实现为 JSON，便于测试与未启动 Program 的场景直接使用。
    /// </summary>
    public static class SessionStore
    {
        private static readonly ConcurrentDictionary<string, SessionState> Map =
            new ConcurrentDictionary<string, SessionState>();
        private static readonly ILogSink Log = new ConsoleLogSink();
        private static ISessionPersistence _persistence = new JsonSessionPersistence(DataStore.FindDataRoot);

        public static void UsePersistence(ISessionPersistence persistence)
        {
            _persistence = persistence ?? new JsonSessionPersistence(DataStore.FindDataRoot);
            // 切换后端时丢弃进程内缓存，避免读到上一个后端的陈旧会话。
            Map.Clear();
        }

        public sealed class SessionFileDto
        {
            public int schemaVersion = 4;
            public string npcId;
            public string playerId;
            public string sessionId;
            public string summary;             // v1 兼容字段
            public List<string> facts = new List<string>();
            public AIBot.Core.Context.SimGameState simState;
            public List<LlmMessage> messages = new List<LlmMessage>();
            public List<LlmMessage> evictedMessages = new List<LlmMessage>();
            public List<ChatRequestRecord> recentRequests = new List<ChatRequestRecord>();
            public PendingToolRound pendingToolRound;   // v4：game 模式挂起工具轮
            public DateTime lastActiveUtc;
            /// <summary>仅进程内使用：v1 legacy 文件的来源路径（不落盘、不进 BSON）。</summary>
            [JsonIgnore] public string legacySourcePath;
        }

        private static string Key(string gid, string npcId, string playerId, string sid)
        {
            return gid + "|" + npcId + "|" + (playerId ?? "<legacy>") + "|" + sid;
        }

        /// <summary>存储层规范身份键（playerId 空位为空串），JSON 保护路径与 Mongo _id 共用。</summary>
        public static string IdentityKey(string gid, string npcId, string playerId, string sid)
        {
            return gid + "|" + npcId + "|" + (playerId ?? string.Empty) + "|" + sid;
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
            try
            {
                SessionFileDto dto = _persistence.Load(gid, npcId, playerId, sid);
                return dto == null ? null : FromDto(gid, playerId, sid, maxTurns, dto);
            }
            catch (Exception ex)
            {
                // 存储抖动不应升级为聊天请求失败：从空白会话继续。
                Log.Log(LogLevel.Warning, "会话恢复失败(" + sid + ")，从空白开始: " + ex.Message);
                return null;
            }
        }

        /// <summary>原子落盘；待摘要队列也持久化，后台失败或重启后可继续处理。</summary>
        public static bool Save(SessionState session)
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
                recentRequests = CopyRequests(session.RecentRequests),
                pendingToolRound = session.PendingToolRound,
                lastActiveUtc = session.LastActiveUtc,
                legacySourcePath = session.LegacySourcePath
            };
            SessionSaveResult result = _persistence.Save(session.GameId, dto);
            if (result.LegacyArchived) session.LegacySourcePath = null;
            return result.Persisted;
        }

        public static List<SessionState> ListByGame(string gid, string npcId = null, string playerId = null)
        {
            var result = Map.Values
                .Where(s => s.GameId == gid
                    && (npcId == null || s.NpcId == npcId)
                    && (playerId == null || s.PlayerId == playerId))
                .ToDictionary(s => Key(s.GameId, s.NpcId, s.PlayerId, s.SessionId), s => s);

            foreach (SessionFileDto dto in _persistence.List(gid, npcId, playerId))
            {
                if (dto == null || string.IsNullOrEmpty(dto.npcId) || string.IsNullOrEmpty(dto.sessionId)) continue;
                string key = Key(gid, dto.npcId, dto.playerId, dto.sessionId);
                if (!result.ContainsKey(key))
                {
                    // 与磁盘加载保持一致的容量：按消息条数（原 JSON 列表分支语义）。
                    result[key] = FromDto(gid, dto.playerId, dto.sessionId,
                        Math.Max(1, (dto.messages?.Count ?? 0) / 2 + 1), dto,
                        Math.Max(2, (dto.messages?.Count ?? 0) + 2));
                }
            }
            return result.Values.OrderByDescending(s => s.LastActiveUtc).ToList();
        }

        public static List<PendingMemorySession> ScanPendingPlayerSessions()
        {
            return _persistence.ScanPending();
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
                    session.RecentRequests = new List<ChatRequestRecord>();
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
            bool persisted = _persistence.Delete(gid, npcId, playerId, sid);
            bool removedFromMemory = Map.TryRemove(key, out _);
            return persisted || removedFromMemory;
        }

        public static int Count { get { return Map.Count; } }

        /// <summary>
        /// 淘汰长时间不活跃的内存会话。持久化文件/数据库不删除，下次访问仍会恢复。
        /// 正在使用的会话无法立即获取 Gate 时跳过，避免打断聊天。
        /// </summary>
        public static int PruneInactive(TimeSpan idle)
        {
            if (idle <= TimeSpan.Zero) return 0;
            DateTime cutoff = DateTime.UtcNow - idle;
            int removed = 0;
            foreach (KeyValuePair<string, SessionState> pair in Map)
            {
                SessionState session = pair.Value;
                if (session == null || session.LastActiveUtc >= cutoff) continue;
                if (!session.Gate.Wait(0)) continue;
                try
                {
                    if (session.LastActiveUtc < cutoff && Map.TryRemove(pair.Key, out _)) removed++;
                }
                finally { session.Gate.Release(); }
            }
            return removed;
        }

        /// <summary>
        /// 清理长期不活跃的持久化会话。门面从 Map 构造规范身份键与 legacy 保护路径，删除动作下沉到
        /// <see cref="ISessionPersistence.DeleteExpired"/>（JSON 删文件；Mongo deleteMany；MySQL 跳过）。
        /// </summary>
        public static int PruneInactiveFiles(TimeSpan idle)
        {
            if (idle <= TimeSpan.Zero) return 0;
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SessionState session in Map.Values)
            {
                if (session == null) continue;
                activeKeys.Add(IdentityKey(session.GameId, session.NpcId, session.PlayerId, session.SessionId));
                if (!string.IsNullOrEmpty(session.LegacySourcePath)) protectedPaths.Add(session.LegacySourcePath);
            }
            return _persistence.DeleteExpired(idle, activeKeys, protectedPaths);
        }

        private static LlmMessage CopyMessage(LlmMessage message)
        {
            return new LlmMessage { Role = message?.Role, Content = message?.Content };
        }

        private static SessionState FromDto(string gid, string playerId, string sid, int maxTurns,
            SessionFileDto dto, int? messageCapacity = null)
        {
            var memory = new ShortTermMemory(messageCapacity ?? ToMessageCapacity(maxTurns));
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
                RecentRequests = CopyRequests(dto.recentRequests),
                PendingToolRound = dto.pendingToolRound,
                LastActiveUtc = dto.lastActiveUtc == default(DateTime) ? DateTime.UtcNow : dto.lastActiveUtc,
                LegacySourcePath = dto.legacySourcePath
            };
        }

        public static ChatRequestRecord FindRequest(SessionState session, string requestId)
        {
            if (session == null || string.IsNullOrEmpty(requestId)) return null;
            return (session.RecentRequests ?? new List<ChatRequestRecord>())
                .FirstOrDefault(item => string.Equals(item.requestId, requestId, StringComparison.Ordinal));
        }

        /// <summary>game 模式挂起轮的存活窗口；超时视为客户端放弃，允许新一轮对话。</summary>
        public static readonly TimeSpan PendingToolRoundTimeout = TimeSpan.FromMinutes(10);

        /// <summary>取会话当前挂起轮；已超时的视为失效并就地清除。调用方必须已持有会话 Gate。</summary>
        public static PendingToolRound TakeLivePendingToolRound(SessionState session)
        {
            PendingToolRound pending = session?.PendingToolRound;
            if (pending == null) return null;
            if (DateTime.UtcNow - pending.createdUtc > PendingToolRoundTimeout)
            {
                session.PendingToolRound = null;
                return null;
            }
            return pending;
        }

        public static ChatRequestRecord BeginRequest(SessionState session, string requestId, string fingerprint)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrEmpty(requestId)) throw new ArgumentException("requestId required", nameof(requestId));
            session.RecentRequests = session.RecentRequests ?? new List<ChatRequestRecord>();
            ChatRequestRecord existing = FindRequest(session, requestId);
            if (existing != null) return existing;
            var created = new ChatRequestRecord
            {
                requestId = requestId,
                fingerprint = fingerprint,
                status = ChatRequestStatuses.Processing,
                createdUtc = DateTime.UtcNow
            };
            session.RecentRequests.Add(created);
            PruneRequests(session);
            return created;
        }

        public static void CompleteRequest(ChatRequestRecord record, IEnumerable<string> events, bool failed = false)
        {
            if (record == null) return;
            record.status = failed ? ChatRequestStatuses.Failed : ChatRequestStatuses.Completed;
            record.events = events == null ? new List<string>() : events.ToList();
            record.completedUtc = DateTime.UtcNow;
        }

        private static void PruneRequests(SessionState session)
        {
            const int maxRequests = 20;
            if (session.RecentRequests.Count <= maxRequests) return;
            session.RecentRequests = session.RecentRequests
                .OrderByDescending(item => item.completedUtc == default(DateTime) ? item.createdUtc : item.completedUtc)
                .Take(maxRequests)
                .ToList();
        }

        private static List<ChatRequestRecord> CopyRequests(IEnumerable<ChatRequestRecord> source)
        {
            return (source ?? Enumerable.Empty<ChatRequestRecord>())
                .Where(item => item != null && !string.IsNullOrEmpty(item.requestId))
                .Select(item => new ChatRequestRecord
                {
                    requestId = item.requestId,
                    fingerprint = item.fingerprint,
                    status = item.status,
                    events = item.events == null ? new List<string>() : new List<string>(item.events),
                    createdUtc = item.createdUtc,
                    completedUtc = item.completedUtc
                })
                .ToList();
        }

        private static int ToMessageCapacity(int turns)
        {
            return Math.Max(2, turns * 2);
        }
    }
}
