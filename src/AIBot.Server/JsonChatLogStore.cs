using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>对话日志的 JSONL 落盘实现：按日分文件，写入时清理超过保留期的旧文件。</summary>
    public sealed class JsonChatLogStore : IChatLogStore
    {
        private readonly object _fileLock = new object();
        private readonly int _retentionDays;

        public JsonChatLogStore(int retentionDays)
        {
            _retentionDays = Math.Max(1, retentionDays);
        }

        public void Write(string gameId, ChatLogService.ChatLogEntry entry)
        {
            string dir = FindLogDir(gameId);
            string file = Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
            lock (_fileLock)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(file, JsonConvert.SerializeObject(entry, Formatting.None) + "\n");
                Cleanup(dir);
            }
        }

        public JObject Query(string gameId, string date, string npcId, int limit, int offset)
        {
            DateTime day;
            if (string.IsNullOrEmpty(date) || !DateTime.TryParse(date,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out day)) day = DateTime.UtcNow;
            day = day.ToUniversalTime();

            string file = Path.Combine(FindLogDir(gameId), day.ToString("yyyy-MM-dd") + ".jsonl");
            var items = new JArray();
            int total = 0;
            if (File.Exists(file))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = lines.Length - 1; i >= 0; i--)          // 最新在前
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    try
                    {
                        var obj = JObject.Parse(line);
                        if (!string.IsNullOrEmpty(npcId) && (string)obj["npcId"] != npcId) continue;
                        total++;
                        if (total <= offset || total > offset + limit) continue;
                        items.Add(obj);
                    }
                    catch (Exception) { /* 跳过坏行 */ }
                }
            }
            return new JObject { ["total"] = total, ["items"] = items, ["date"] = day.ToString("yyyy-MM-dd") };
        }

        private static string FindLogDir(string gameId)
        {
            string root = DataStore.FindDataRoot();
            if (root == null) return Path.Combine(Path.GetTempPath(), "aibot-logs", gameId);
            return Path.Combine(root, "logs", gameId);
        }

        /// <summary>按日分文件即天然轮转；写时顺手清理超过保留期的旧文件。</summary>
        private void Cleanup(string dir)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (string file in Directory.GetFiles(dir, "*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); } catch { /* 占用则下次再清 */ }
                }
            }
        }
    }
}
