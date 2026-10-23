using System;
using System.Collections.Generic;
using AIBot.Core.Logging;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>对话日志门面：统计从保留期内的持久化日志生成，重启不清零。</summary>
    public static class ChatLogService
    {
        private static readonly ILogSink Log = new ConsoleLogSink();
        private static IChatLogStore _store = new JsonChatLogStore(30);
        private static RuntimeLogService RuntimeLogs;

        public static void UseStore(IChatLogStore store)
        {
            _store = store ?? new JsonChatLogStore(30);
        }

        public static void Configure(IConfiguration configuration, RuntimeLogService runtimeLogs)
        {
            // 保留天数已由 JsonChatLogStore 构造注入；此处只负责运行时日志（写失败时记录）。
            RuntimeLogs = runtimeLogs;
        }

        public sealed class NpcAgg
        {
            public long Requests;
            public long Fallbacks;
            public long InjectionAttempts;
            public long PromptTokens;
            public long CompletionTokens;
            public double TotalMs;
            public long SummaryCalls;
        }

        public sealed class ChatLogEntry
        {
            public string ts;
            public string kind = "chat"; // chat / tool_pending / summary
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
                _store.Write(gameId, entry);
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "日志写入失败: " + ex.Message);
                RuntimeLogs?.Write(LogLevel.Warning, "ChatLog", "write_failed",
                    "对话日志写入失败: " + ex.Message, null, ex);
            }
        }

        /// <summary>查询某日对话日志（最新在前，分页；供 /api/games/{gid}/logs）。</summary>
        public static JObject Query(string gameId, string date, string npcId, int limit, int offset)
        {
            return _store.Query(gameId, date, npcId, limit, offset);
        }

        /// <summary>统计快照（供 /api/games/{gid}/stats）。</summary>
        public static JObject Snapshot(string gameId)
        {
            var byNpc = new JObject();
            long totalRequests = 0, totalPrompt = 0, totalCompletion = 0, totalFallbacks = 0, totalInjections = 0;
            double totalMs = 0;
            long totalSummaryCalls = 0;
            var perGame = new Dictionary<string, NpcAgg>();
            foreach (ChatLogEntry e in _store.ReadForStats(gameId))
            {
                string npc = e.npcId ?? "?";
                if (!perGame.TryGetValue(npc, out NpcAgg agg)) perGame[npc] = agg = new NpcAgg();
                bool summary = e.kind == "summary";
                if (summary) agg.SummaryCalls++;
                else
                {
                    agg.Requests++;
                    agg.TotalMs += e.elapsedMs;
                    if (e.fallback) agg.Fallbacks++;
                    if (e.injection) agg.InjectionAttempts++;
                }
                agg.PromptTokens += e.promptTokens;
                agg.CompletionTokens += e.completionTokens;
            }
            foreach (KeyValuePair<string, NpcAgg> kv in perGame)
            {
                byNpc[kv.Key] = new JObject
                {
                    ["requests"] = kv.Value.Requests,
                    ["fallbacks"] = kv.Value.Fallbacks,
                    ["injectionAttempts"] = kv.Value.InjectionAttempts,
                    ["promptTokens"] = kv.Value.PromptTokens,
                    ["completionTokens"] = kv.Value.CompletionTokens,
                    ["summaryCalls"] = kv.Value.SummaryCalls,
                    ["avgMs"] = kv.Value.Requests > 0 ? Math.Round(kv.Value.TotalMs / kv.Value.Requests) : 0
                };
                totalRequests += kv.Value.Requests;
                totalFallbacks += kv.Value.Fallbacks;
                totalInjections += kv.Value.InjectionAttempts;
                totalPrompt += kv.Value.PromptTokens;
                totalCompletion += kv.Value.CompletionTokens;
                totalMs += kv.Value.TotalMs;
                totalSummaryCalls += kv.Value.SummaryCalls;
            }
            return new JObject
            {
                ["totalRequests"] = totalRequests,
                ["totalFallbacks"] = totalFallbacks,
                ["totalInjectionAttempts"] = totalInjections,
                ["totalPromptTokens"] = totalPrompt,
                ["totalCompletionTokens"] = totalCompletion,
                ["totalSummaryCalls"] = totalSummaryCalls,
                ["avgMs"] = totalRequests > 0 ? Math.Round(totalMs / totalRequests) : 0,
                ["byNpc"] = byNpc,
                ["note"] = "统计来自当前存储保留期内的日志，重启保留；Token 包含工具挂起轮与摘要调用，仅计入上游实际返回的 usage，不等同账单。"
            };
        }
    }
}
