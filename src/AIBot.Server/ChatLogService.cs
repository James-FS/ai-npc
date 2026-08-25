using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using AIBot.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>对话日志：按日 jsonl 落盘（保留 30 天），内存聚合用量统计。</summary>
    public static class ChatLogService
    {
        private const int RetentionDays = 30;
        private static readonly object FileLock = new object();
        private static readonly ILogSink Log = new ConsoleLogSink();

        // ---- 统计（内存聚合，重启清零；明细见 jsonl 日志）----
        public sealed class NpcAgg
        {
            public long Requests;
            public long Fallbacks;
            public long InjectionAttempts;
            public long PromptTokens;
            public long CompletionTokens;
            public double TotalMs;
        }

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, NpcAgg>> Stats =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, NpcAgg>>();

        public sealed class ChatLogEntry
        {
            public string ts;
            public string npcId;
            public string sessionId;
            public string userMessage;
            public string say;
            public string emotion;
            public string action;
            public bool fallback;
            public int promptTokens;
            public int completionTokens;
            public long elapsedMs;
            public List<string> tools;
            public bool injection;
        }

        public static void Record(string gameId, ChatLogEntry entry)
        {
            try
            {
                WriteFile(gameId, entry);
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "日志写入失败: " + ex.Message);
            }
            Aggregate(gameId, entry);
        }

        private static void WriteFile(string gameId, ChatLogEntry entry)
        {
            string dir = Path.Combine(FindLogDir(gameId));
            string file = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
            lock (FileLock)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(file, JsonConvert.SerializeObject(entry, Formatting.None) + "\n");
                Cleanup(dir);
            }
        }

        private static string FindLogDir(string gameId)
        {
            string root = DataStore.FindDataRoot();
            if (root == null) return Path.Combine(Path.GetTempPath(), "aibot-logs", gameId);
            return Path.Combine(root, "logs", gameId);
        }

        /// <summary>按日分文件即天然轮转；写时顺手清理超过保留期的旧文件。</summary>
        private static void Cleanup(string dir)
        {
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (string file in Directory.GetFiles(dir, "*.jsonl"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    try { File.Delete(file); } catch { /* 占用则下次再清 */ }
                }
            }
        }

        private static void Aggregate(string gameId, ChatLogEntry e)
        {
            var perGame = Stats.GetOrAdd(gameId, _ => new ConcurrentDictionary<string, NpcAgg>());
            NpcAgg agg = perGame.GetOrAdd(e.npcId ?? "?", _ => new NpcAgg());
            agg.Requests++;
            if (e.fallback) agg.Fallbacks++;
            if (e.injection) agg.InjectionAttempts++;
            agg.PromptTokens += e.promptTokens;
            agg.CompletionTokens += e.completionTokens;
            agg.TotalMs += e.elapsedMs;
        }

        /// <summary>查询某日对话日志（最新在前，分页；供 /api/games/{gid}/logs）。</summary>
        public static JObject Query(string gameId, string date, string npcId, int limit, int offset)
        {
            DateTime day;
            if (string.IsNullOrEmpty(date) || !DateTime.TryParse(date, out day)) day = DateTime.Now;
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

        /// <summary>统计快照（供 /api/games/{gid}/stats）。</summary>
        public static JObject Snapshot(string gameId)
        {
            var byNpc = new JObject();
            long totalRequests = 0, totalPrompt = 0, totalCompletion = 0, totalFallbacks = 0, totalInjections = 0;
            double totalMs = 0;
            if (Stats.TryGetValue(gameId, out var perGame))
            {
                foreach (KeyValuePair<string, NpcAgg> kv in perGame)
                {
                    byNpc[kv.Key] = new JObject
                    {
                        ["requests"] = kv.Value.Requests,
                        ["fallbacks"] = kv.Value.Fallbacks,
                        ["injectionAttempts"] = kv.Value.InjectionAttempts,
                        ["promptTokens"] = kv.Value.PromptTokens,
                        ["completionTokens"] = kv.Value.CompletionTokens,
                        ["avgMs"] = kv.Value.Requests > 0 ? Math.Round(kv.Value.TotalMs / kv.Value.Requests) : 0
                    };
                    totalRequests += kv.Value.Requests;
                    totalFallbacks += kv.Value.Fallbacks;
                    totalInjections += kv.Value.InjectionAttempts;
                    totalPrompt += kv.Value.PromptTokens;
                    totalCompletion += kv.Value.CompletionTokens;
                    totalMs += kv.Value.TotalMs;
                }
            }
            return new JObject
            {
                ["totalRequests"] = totalRequests,
                ["totalFallbacks"] = totalFallbacks,
                ["totalInjectionAttempts"] = totalInjections,
                ["totalPromptTokens"] = totalPrompt,
                ["totalCompletionTokens"] = totalCompletion,
                ["avgMs"] = totalRequests > 0 ? Math.Round(totalMs / totalRequests) : 0,
                ["byNpc"] = byNpc,
                ["note"] = "统计为本次启动以来累计；明细见 data/logs/" + gameId + "/ 按日 jsonl"
            };
        }
    }
}
