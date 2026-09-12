using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>记忆审计的 JSONL 文件实现：按游戏、按日保存，查询时最新在前。</summary>
    public sealed class JsonMemoryAuditStore : IMemoryAuditStore
    {
        private readonly object _fileLock = new object();
        private readonly Func<string> _dataRoot;

        public JsonMemoryAuditStore(Func<string> dataRoot)
        {
            _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        }

        public void Write(MemoryAuditEntry entry)
        {
            string dir = AuditDirectory(entry.gameId);
            if (dir == null) throw new IOException("data/ 根目录未找到");
            DateTimeOffset stamp;
            if (!DateTimeOffset.TryParse(entry.ts, out stamp)) stamp = DateTimeOffset.UtcNow;
            string file = Path.Combine(dir, stamp.UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl");
            string line = JsonConvert.SerializeObject(entry, Formatting.None);
            lock (_fileLock)
            {
                Directory.CreateDirectory(dir);
                // RecordRequired 的同一次重试保持 id 不变，避免“已写入但调用方重试”产生重复记录。
                if (File.Exists(file) && File.ReadLines(file).Any(existing => HasId(existing, entry.id)))
                    return;
                File.AppendAllText(file, line + "\n");
            }
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

        public int DeleteExpired(DateTime cutoffUtc)
        {
            string root = _dataRoot();
            string logsRoot = root == null ? null : Path.Combine(root, "logs");
            if (logsRoot == null || !Directory.Exists(logsRoot)) return 0;
            int deleted = 0;
            foreach (string directory in Directory.GetDirectories(logsRoot, "memory-audit",
                SearchOption.AllDirectories))
            {
                foreach (string file in Directory.GetFiles(directory, "*.jsonl"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                        {
                            File.Delete(file);
                            deleted++;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return deleted;
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

        private string AuditDirectory(string gameId)
        {
            if (!DataStore.IsValidId(gameId)) return null;
            string root = _dataRoot();
            return root == null ? null : Path.Combine(root, "logs", gameId, "memory-audit");
        }
    }
}
