using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AIBot.Core.Logging;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>对话日志门面：落盘委托给 <see cref="IChatLogStore"/>，内存聚合用量统计。</summary>
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
                _store.Write(gameId, entry);
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Warning, "日志写入失败: " + ex.Message);
                RuntimeLogs?.Write(LogLevel.Warning, "ChatLog", "write_failed",
                    "对话日志写入失败: " + ex.Message, null, ex);
            }
            Aggregate(gameId, entry);
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
            return _store.Query(gameId, date, npcId, limit, offset);
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
                ["note"] = "统计为本次启动以来累计"
            };
        }
    }
}
