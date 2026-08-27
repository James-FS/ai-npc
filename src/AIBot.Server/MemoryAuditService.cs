using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Data;
using AIBot.Core.Logging;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    public sealed class MemoryAuditWriteException : IOException
    {
        public MemoryAuditWriteException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public sealed class MemoryAuditEntry
    {
        public string id;
        public string ts;
        public string gameId;
        public string npcId;
        public string playerId;
        public string actor;
        public string action;
        public JToken before;
        public JToken after;
        public JObject metadata;
    }

    /// <summary>记忆与策略人工变更审计：按游戏、按日 JSONL 保存，查询时最新在前。</summary>
    public sealed class MemoryAuditService
    {
        private static readonly object FileLock = new object();
        private readonly Func<string> _dataRoot;
        private readonly MySqlConnectionFactory _mysql;
        private readonly ILogSink _log = new ConsoleLogSink();

        public MemoryAuditService() : this(DataStore.FindDataRoot, null) { }

        public MemoryAuditService(MySqlConnectionFactory mysql) : this(null, mysql) { }

        public MemoryAuditService(Func<string> dataRoot)
            : this(dataRoot, null) { }

        private MemoryAuditService(Func<string> dataRoot, MySqlConnectionFactory mysql)
        {
            if (dataRoot == null && mysql == null) throw new ArgumentNullException(nameof(dataRoot));
            _dataRoot = dataRoot;
            _mysql = mysql;
        }

        public bool Record(MemoryAuditEntry entry)
        {
            try
            {
                Write(entry);
                return true;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "记忆审计写入失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>关键修改使用的必写审计；短暂 I/O 故障会重试，最终失败则由 API/队列显式处理。</summary>
        public void RecordRequired(MemoryAuditEntry entry)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Write(entry);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 2) Thread.Sleep(25 * (attempt + 1));
                }
            }
            _log.Log(LogLevel.Error, "记忆审计写入重试耗尽: " + last?.Message);
            throw new MemoryAuditWriteException("记忆审计写入失败，操作结果不能被视为已完整提交", last);
        }

        private void Write(MemoryAuditEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!DataStore.IsValidId(entry.gameId)) throw new ArgumentException("非法 gameId", nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.action))
                throw new ArgumentException("审计 action 不能为空", nameof(entry));

            entry.id = string.IsNullOrEmpty(entry.id) ? "audit-" + Guid.NewGuid().ToString("N") : entry.id;
            entry.ts = string.IsNullOrEmpty(entry.ts) ? DateTime.UtcNow.ToString("o") : entry.ts;
            entry.actor = string.IsNullOrWhiteSpace(entry.actor) ? "admin" : entry.actor;
            if (_mysql != null)
            {
                WriteMySql(entry);
                return;
            }
            string dir = AuditDirectory(entry.gameId);
            if (dir == null) throw new IOException("data/ 根目录未找到");
            DateTimeOffset stamp;
            if (!DateTimeOffset.TryParse(entry.ts, out stamp)) stamp = DateTimeOffset.UtcNow;
            string file = Path.Combine(dir, stamp.UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl");
            string line = JsonConvert.SerializeObject(entry, Formatting.None);
            lock (FileLock)
            {
                Directory.CreateDirectory(dir);
                // RecordRequired 的同一次重试保持 id 不变，避免“已写入但调用方重试”产生重复记录。
                if (File.Exists(file) && File.ReadLines(file).Any(existing => HasId(existing, entry.id)))
                    return;
                File.AppendAllText(file, line + "\n");
            }
        }

        private static bool HasId(string line, string id)
        {
            try
            {
                return string.Equals(JObject.Parse(line)["id"]?.ToString(), id,
                    StringComparison.Ordinal);
            }
            catch (JsonException) { return false; }
        }

        public JObject Query(string gameId, string npcId, string playerId, string action,
            string date, int limit, int offset)
        {
            DateTime day;
            if (string.IsNullOrWhiteSpace(date) || !DateTime.TryParse(date, out day)) day = DateTime.UtcNow;
            if (_mysql != null) return QueryMySql(gameId, npcId, playerId, action, day, limit, offset);
            string dir = AuditDirectory(gameId);
            string file = dir == null ? null : Path.Combine(dir, day.ToString("yyyy-MM-dd") + ".jsonl");
            var matches = new List<JObject>();
            if (file != null && File.Exists(file))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        JObject item = JObject.Parse(line);
                        if (npcId != null && item["npcId"]?.ToString() != npcId) continue;
                        if (playerId != null && item["playerId"]?.ToString() != playerId) continue;
                        if (action != null && item["action"]?.ToString() != action) continue;
                        matches.Add(item);
                    }
                    catch (JsonException) { }
                }
            }
            matches.Reverse();
            int safeOffset = Math.Max(0, offset);
            int safeLimit = Math.Max(1, Math.Min(200, limit));
            return new JObject
            {
                ["date"] = day.ToString("yyyy-MM-dd"),
                ["total"] = matches.Count,
                ["limit"] = safeLimit,
                ["offset"] = safeOffset,
                ["items"] = new JArray(matches.Skip(safeOffset).Take(safeLimit))
            };
        }

        private string AuditDirectory(string gameId)
        {
            if (!DataStore.IsValidId(gameId)) return null;
            string root = _dataRoot();
            return root == null ? null : Path.Combine(root, "logs", gameId, "memory-audit");
        }

        private void WriteMySql(MemoryAuditEntry entry)
        {
            using (IDbConnection connection = _mysql.OpenConnection())
            {
                if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM memory_audits WHERE id=@Id",
                    new { Id = entry.id }) > 0) return;
                connection.Execute(@"
INSERT INTO memory_audits
 (id,ts,game_id,npc_id,player_id,actor,action,before_json,after_json,metadata_json)
VALUES (@Id,@Ts,@GameId,@NpcId,@PlayerId,@Actor,@Action,@Before,@After,@Metadata)", new
                {
                    Id = entry.id, Ts = ParseUtc(entry.ts), GameId = entry.gameId, NpcId = entry.npcId,
                    PlayerId = entry.playerId, Actor = entry.actor, Action = entry.action,
                    Before = entry.before?.ToString(Formatting.None), After = entry.after?.ToString(Formatting.None),
                    Metadata = entry.metadata?.ToString(Formatting.None)
                });
            }
        }

        private JObject QueryMySql(string gameId, string npcId, string playerId, string action,
            DateTime day, int limit, int offset)
        {
            var parameters = new
            {
                GameId = gameId, NpcId = npcId, PlayerId = playerId, Action = action,
                StartUtc = day.Date.ToUniversalTime(), EndUtc = day.Date.AddDays(1).ToUniversalTime(),
                Limit = Math.Max(1, Math.Min(200, limit)), Offset = Math.Max(0, offset)
            };
            using (IDbConnection connection = _mysql.OpenConnection())
            {
                string where = @"game_id=@GameId AND ts>=@StartUtc AND ts<@EndUtc
 AND (@NpcId IS NULL OR npc_id=@NpcId) AND (@PlayerId IS NULL OR player_id=@PlayerId)
 AND (@Action IS NULL OR action=@Action)";
                int total = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM memory_audits WHERE " + where, parameters);
                IEnumerable<AuditRow> rows = connection.Query<AuditRow>(@"
SELECT id AS Id, ts AS Ts, game_id AS GameId, npc_id AS NpcId, player_id AS PlayerId,
 actor AS Actor, action AS Action, before_json AS BeforeJson, after_json AS AfterJson, metadata_json AS MetadataJson
FROM memory_audits WHERE " + where + " ORDER BY ts DESC LIMIT @Limit OFFSET @Offset", parameters);
                return new JObject
                {
                    ["date"] = day.ToString("yyyy-MM-dd"), ["total"] = total,
                    ["limit"] = parameters.Limit, ["offset"] = parameters.Offset,
                    ["items"] = new JArray(rows.Select(ToJson))
                };
            }
        }

        private static DateTime ParseUtc(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime() : DateTime.UtcNow;
        }

        private static JObject ToJson(AuditRow row)
        {
            return new JObject
            {
                ["id"] = row.Id, ["ts"] = row.Ts, ["gameId"] = row.GameId,
                ["npcId"] = row.NpcId, ["playerId"] = row.PlayerId, ["actor"] = row.Actor,
                ["action"] = row.Action, ["before"] = ParseToken(row.BeforeJson),
                ["after"] = ParseToken(row.AfterJson), ["metadata"] = ParseObject(row.MetadataJson)
            };
        }

        private static JToken ParseToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return JValue.CreateNull();
            try { return JToken.Parse(value); } catch { return new JValue(value); }
        }

        private static JObject ParseObject(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try { return JObject.Parse(value); } catch { return new JObject { ["raw"] = value }; }
        }

        private sealed class AuditRow
        {
            public string Id { get; set; }
            public DateTime Ts { get; set; }
            public string GameId { get; set; }
            public string NpcId { get; set; }
            public string PlayerId { get; set; }
            public string Actor { get; set; }
            public string Action { get; set; }
            public string BeforeJson { get; set; }
            public string AfterJson { get; set; }
            public string MetadataJson { get; set; }
        }
    }
}
