using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using AIBot.Core.Logging;
using Microsoft.Extensions.Configuration;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>对话日志：按日 jsonl 落盘（保留 30 天），内存聚合用量统计。</summary>
    public static class ChatLogService
    {
        private static int RetentionDays = 30;
        private static readonly object FileLock = new object();
        private static readonly ILogSink Log = new ConsoleLogSink();
        private static MySqlConnectionFactory MySqlStorage;
        private static RuntimeLogService RuntimeLogs;

        public static void UseMySql(MySqlConnectionFactory factory) { MySqlStorage = factory; }

        public static void Configure(IConfiguration configuration, RuntimeLogService runtimeLogs)
        {
            RetentionDays = Math.Max(1, configuration.GetValue<int?>("Logging:ChatRetentionDays") ?? 30);
            RuntimeLogs = runtimeLogs;
        }

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
            public string playerId;
            public string sessionId;
            public bool legacyMemoryScope;
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
                RuntimeLogs?.Write(LogLevel.Warning, "ChatLog", "write_failed",
                    "对话日志写入失败: " + ex.Message, null, ex);
            }
            Aggregate(gameId, entry);
        }

        private static void WriteFile(string gameId, ChatLogEntry entry)
        {
            if (MySqlStorage != null)
            {
                using (IDbConnection connection = MySqlStorage.OpenConnection())
                {
                    connection.Execute(@"
INSERT INTO chat_logs
 (game_id,ts,npc_id,player_id,session_id,legacy_memory_scope,user_message,say,emotion,action,
  fallback,prompt_tokens,completion_tokens,elapsed_ms,tools_json,injection)
VALUES (@GameId,@Ts,@NpcId,@PlayerId,@SessionId,@Legacy,@UserMessage,@Say,@Emotion,@Action,
        @Fallback,@PromptTokens,@CompletionTokens,@ElapsedMs,@Tools,@Injection)",
                        new
                        {
                            GameId = gameId, Ts = ParseUtc(entry.ts), NpcId = entry.npcId,
                            PlayerId = entry.playerId, SessionId = entry.sessionId, Legacy = entry.legacyMemoryScope,
                            UserMessage = entry.userMessage, Say = entry.say, Emotion = entry.emotion, Action = entry.action,
                            Fallback = entry.fallback, PromptTokens = entry.promptTokens,
                            CompletionTokens = entry.completionTokens, ElapsedMs = entry.elapsedMs,
                            Tools = JsonConvert.SerializeObject(entry.tools ?? new List<string>()), Injection = entry.injection
                        });
                }
                return;
            }
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
            lock (agg)
            {
                agg.Requests++;
                if (e.fallback) agg.Fallbacks++;
                if (e.injection) agg.InjectionAttempts++;
                agg.PromptTokens += e.promptTokens;
                agg.CompletionTokens += e.completionTokens;
                agg.TotalMs += e.elapsedMs;
            }
        }

        /// <summary>查询某日对话日志（最新在前，分页；供 /api/games/{gid}/logs）。</summary>
        public static JObject Query(string gameId, string date, string npcId, int limit, int offset)
        {
            DateTime day;
            if (string.IsNullOrEmpty(date) || !DateTime.TryParse(date, out day)) day = DateTime.Now;
            if (MySqlStorage != null)
            {
                DateTime startUtc = day.Date.ToUniversalTime();
                DateTime endUtc = day.Date.AddDays(1).ToUniversalTime();
                string filteredNpc = string.IsNullOrEmpty(npcId) ? null : npcId;
                using (IDbConnection connection = MySqlStorage.OpenConnection())
                {
                    var parameters = new
                    {
                        GameId = gameId, NpcId = filteredNpc, StartUtc = startUtc, EndUtc = endUtc,
                        Limit = Math.Max(1, Math.Min(200, limit)), Offset = Math.Max(0, offset)
                    };
                    int totalCount = connection.ExecuteScalar<int>(@"
SELECT COUNT(*) FROM chat_logs WHERE game_id=@GameId AND ts>=@StartUtc AND ts<@EndUtc
 AND (@NpcId IS NULL OR npc_id=@NpcId)", parameters);
                    IEnumerable<ChatLogRow> rows = connection.Query<ChatLogRow>(@"
SELECT ts AS Ts, npc_id AS NpcId, player_id AS PlayerId, session_id AS SessionId,
 legacy_memory_scope AS LegacyMemoryScope, user_message AS UserMessage, say AS Say,
 emotion AS Emotion, action AS Action, fallback AS Fallback, prompt_tokens AS PromptTokens,
 completion_tokens AS CompletionTokens, elapsed_ms AS ElapsedMs, tools_json AS ToolsJson, injection AS Injection
FROM chat_logs WHERE game_id=@GameId AND ts>=@StartUtc AND ts<@EndUtc
 AND (@NpcId IS NULL OR npc_id=@NpcId) ORDER BY ts DESC LIMIT @Limit OFFSET @Offset", parameters);
                    return new JObject
                    {
                        ["total"] = totalCount,
                        ["items"] = new JArray(rows.Select(ToJson)),
                        ["date"] = day.ToString("yyyy-MM-dd")
                    };
                }
            }
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

        private static DateTime ParseUtc(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime() : DateTime.UtcNow;
        }

        private static JObject ToJson(ChatLogRow row)
        {
            JArray tools;
            try { tools = JArray.Parse(row.ToolsJson ?? "[]"); } catch { tools = new JArray(); }
            return new JObject
            {
                ["ts"] = row.Ts, ["npcId"] = row.NpcId, ["playerId"] = row.PlayerId,
                ["sessionId"] = row.SessionId, ["legacyMemoryScope"] = row.LegacyMemoryScope,
                ["userMessage"] = row.UserMessage, ["say"] = row.Say, ["emotion"] = row.Emotion,
                ["action"] = row.Action, ["fallback"] = row.Fallback, ["promptTokens"] = row.PromptTokens,
                ["completionTokens"] = row.CompletionTokens, ["elapsedMs"] = row.ElapsedMs,
                ["tools"] = tools, ["injection"] = row.Injection
            };
        }

        private sealed class ChatLogRow
        {
            public DateTime Ts { get; set; }
            public string NpcId { get; set; }
            public string PlayerId { get; set; }
            public string SessionId { get; set; }
            public bool LegacyMemoryScope { get; set; }
            public string UserMessage { get; set; }
            public string Say { get; set; }
            public string Emotion { get; set; }
            public string Action { get; set; }
            public bool Fallback { get; set; }
            public int PromptTokens { get; set; }
            public int CompletionTokens { get; set; }
            public long ElapsedMs { get; set; }
            public string ToolsJson { get; set; }
            public bool Injection { get; set; }
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
                    lock (kv.Value)
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
