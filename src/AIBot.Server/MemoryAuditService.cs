using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AIBot.Core.Logging;
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
        private readonly ILogSink _log = new ConsoleLogSink();

        public MemoryAuditService() : this(DataStore.FindDataRoot) { }

        public MemoryAuditService(Func<string> dataRoot)
        {
            _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
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
    }
}
