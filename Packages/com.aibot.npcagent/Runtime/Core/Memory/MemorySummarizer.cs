using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Core.Llm;
using AIBot.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Core.Memory
{
    public sealed class MemorySummaryResult
    {
        public string Summary;
        public List<string> Facts = new List<string>();
    }

    /// <summary>
    /// 摘要式长期记忆：把被窗口挤出的旧对话压缩为「摘要 + 关键事实」，
    /// 与已有记忆滚动合并。使用 summaryModel（未配置则用主模型），无工具、低温度。
    /// </summary>
    public static class MemorySummarizer
    {
        private const string SystemPrompt =
            "你是游戏NPC的记忆整理器。把「已有记忆」与「旧对话」合并压缩成一段新记忆，" +
            "只保留对NPC继续与该玩家互动有用的信息（玩家称呼、承诺、好感线索、关键剧情进展）。" +
            "只输出JSON：{\"summary\":\"80字以内的滚动摘要\",\"facts\":[\"一条一个的关键事实\"]}，" +
            "facts最多8条，每条15字以内。";

        public static async Task<MemorySummaryResult> RunAsync(ILlmBackend backend, AgentConfigDto cfg,
            string existingSummary, List<string> existingFacts, List<LlmMessage> evictedMessages,
            ILogSink log, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【已有记忆】");
            sb.AppendLine(string.IsNullOrEmpty(existingSummary) ? "（无）" : existingSummary);
            if (existingFacts != null && existingFacts.Count > 0)
            {
                foreach (string fact in existingFacts) sb.AppendLine("- " + fact);
            }
            sb.AppendLine();
            sb.AppendLine("【旧对话】");
            foreach (LlmMessage m in evictedMessages)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                sb.AppendLine(m.Role + ": " + m.Content);
            }

            var request = new LlmRequest
            {
                Model = cfg.memory.summaryModel != null ? cfg.memory.summaryModel.model : cfg.model.model,
                Messages = new List<LlmMessage>
                {
                    LlmMessage.System(SystemPrompt),
                    LlmMessage.User(sb.ToString())
                },
                Temperature = 0.3f,
                MaxTokens = 400,
                ResponseFormat = ResponseFormat.JsonObject()      // 无工具，可安全启用
            };

            string raw = null;
            var collector = new CollectorSink();
            await backend.ChatStreamAsync(request, collector, ct);
            raw = collector.Text ?? string.Empty;

            MemorySummaryResult result = Parse(raw);
            if (result == null)
            {
                log.Log(LogLevel.Warning, "Memory summary parse failed, raw=" + raw);
                result = new MemorySummaryResult { Summary = existingSummary ?? "", Facts = existingFacts ?? new List<string>() };
            }
            return result;
        }

        /// <summary>容错解析：JObject 失败则正则抽取 summary/facts。</summary>
        private static MemorySummaryResult Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                int start = raw.IndexOf('{');
                int end = raw.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    var obj = JObject.Parse(raw.Substring(start, end - start + 1));
                    var result = new MemorySummaryResult
                    {
                        Summary = obj["summary"]?.ToString() ?? ""
                    };
                    foreach (var f in (obj["facts"] as JArray) ?? new JArray())
                    {
                        string s = f.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) result.Facts.Add(s);
                    }
                    if (!string.IsNullOrEmpty(result.Summary)) return result;
                }
            }
            catch (Exception) { }
            var mSummary = System.Text.RegularExpressions.Regex.Match(raw, "\"summary\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (mSummary.Success)
            {
                return new MemorySummaryResult
                {
                    Summary = JsonConvert.DeserializeObject<string>("\"" + mSummary.Groups[1].Value + "\"")
                };
            }
            return null;
        }

        private sealed class CollectorSink : ILlmStreamSink
        {
            public string Text;
            public void OnToken(string delta) { Text = (Text ?? string.Empty) + delta; }
            public void OnToolCall(ToolCallDto call) { }
            public void OnCompleted(string fullText, Usage usage) { Text = fullText; }
            public void OnError(Exception ex) { }
        }
    }
}
