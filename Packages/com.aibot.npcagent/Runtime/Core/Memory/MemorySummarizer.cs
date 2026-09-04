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

    public sealed class PlayerMemorySummaryResult
    {
        public string Summary;
        public List<MemoryFact> Facts = new List<MemoryFact>();
    }

    /// <summary>
    /// 摘要式长期记忆：把被窗口挤出的旧对话压缩为「摘要 + 关键事实」，
    /// 与已有记忆滚动合并。使用 summaryModel（未配置则用主模型），无工具、低温度。
    /// </summary>
    public static class MemorySummarizer
    {
        private const string SystemPromptPrefix =
            "你是游戏NPC的记忆整理器。把「已有记忆」与「旧对话」合并压缩成一段新记忆，" +
            "只保留对NPC继续与该玩家互动有用的信息（玩家称呼、承诺、好感线索、关键剧情进展）。" +
            "只输出JSON：{\"summary\":\"80字以内的滚动摘要\",\"facts\":[\"一条一个的关键事实\"]}，";

        public static async Task<MemorySummaryResult> RunAsync(ILlmBackend backend, ModelSettings settings,
            string existingSummary, List<string> existingFacts, List<LlmMessage> evictedMessages,
            int maxFacts, ILogSink log, CancellationToken ct, MemoryPolicy policy = null)
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
                Model = settings.model,
                Messages = new List<LlmMessage>
                {
                    LlmMessage.System(SystemPromptPrefix + "facts最多" + Math.Max(1, maxFacts)
                        + "条，每条15字以内。" + BuildCategoryInstruction(policy)),
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
            int factLimit = Math.Max(1, maxFacts);
            if (result.Facts != null && result.Facts.Count > factLimit)
                result.Facts.RemoveRange(factLimit, result.Facts.Count - factLimit);
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
            private readonly StringBuilder _text = new StringBuilder();
            public string Text { get { return _text.ToString(); } }
            public void OnToken(string delta) { if (!string.IsNullOrEmpty(delta)) _text.Append(delta); }
            public void OnToolCall(ToolCallDto call) { }
            public void OnCompleted(string fullText, Usage usage)
            {
                _text.Clear();
                if (!string.IsNullOrEmpty(fullText)) _text.Append(fullText);
            }
            public void OnError(Exception ex) { }
        }

        /// <summary>玩家长期记忆后台摘要：输出结构化事实；兼容 facts 字符串数组。</summary>
        public static async Task<PlayerMemorySummaryResult> RunStructuredAsync(ILlmBackend backend,
            ModelSettings settings, PlayerLongTermMemory existing, List<LlmMessage> evictedMessages,
            int maxFacts, string sourceSessionId, ILogSink log, CancellationToken ct,
            MemoryPolicy policy = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【已有长期摘要】");
            sb.AppendLine(string.IsNullOrEmpty(existing?.summary) ? "（无）" : existing.summary);
            sb.AppendLine("【已有结构化事实】");
            sb.AppendLine(JsonConvert.SerializeObject(existing?.facts ?? new List<MemoryFact>()));
            sb.AppendLine("【待整理旧对话】");
            foreach (LlmMessage message in evictedMessages ?? new List<LlmMessage>())
            {
                if (!string.IsNullOrEmpty(message.Content))
                    sb.AppendLine(message.Role + ": " + message.Content);
            }

            string system =
                "你是游戏NPC的长期记忆整理器。只保留玩家档案、承诺、关系变化和关键剧情。" +
                "只输出JSON：{\"summary\":\"80字以内摘要\",\"facts\":[{" +
                "\"category\":\"player_profile|promise|quest|relationship|casual|general\"," +
                "\"key\":\"稳定字段名\",\"value\":\"事实\",\"confidence\":0到1," +
                "\"source\":\"player_statement|npc_observation|dialogue\"}]}。" +
                "facts最多" + Math.Max(1, maxFacts) + "条。不要把背包、货币、任务状态等游戏权威状态编造成记忆。" +
                BuildCategoryInstruction(policy);

            var request = new LlmRequest
            {
                Model = settings.model,
                Messages = new List<LlmMessage>
                {
                    LlmMessage.System(system),
                    LlmMessage.User(sb.ToString())
                },
                Temperature = 0.2f,
                MaxTokens = 600,
                ResponseFormat = ResponseFormat.JsonObject()
            };
            var collector = new CollectorSink();
            await backend.ChatStreamAsync(request, collector, ct);
            PlayerMemorySummaryResult parsed = ParseStructured(collector.Text, maxFacts, sourceSessionId);
            if (parsed != null) return parsed;
            (log ?? NullLogSink.Instance).Log(LogLevel.Warning,
                "Structured player memory parse failed, raw=" + collector.Text);
            throw new InvalidOperationException("structured player memory parse failed");
        }

        private static PlayerMemorySummaryResult ParseStructured(string raw, int maxFacts, string sourceSessionId)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                int start = raw.IndexOf('{');
                int end = raw.LastIndexOf('}');
                if (start < 0 || end <= start) return null;
                JObject obj = JObject.Parse(raw.Substring(start, end - start + 1));
                string summary = obj["summary"]?.ToString();
                if (string.IsNullOrWhiteSpace(summary)) return null;
                var result = new PlayerMemorySummaryResult { Summary = summary };
                DateTime now = DateTime.UtcNow;
                foreach (JToken token in (obj["facts"] as JArray) ?? new JArray())
                {
                    MemoryFact fact;
                    if (token.Type == JTokenType.String)
                        fact = MemoryFactMerger.FromLegacyString(token.ToString(), now, sourceSessionId);
                    else
                        fact = token.ToObject<MemoryFact>();
                    if (fact == null || string.IsNullOrWhiteSpace(fact.value)) continue;
                    fact.sourceSessionId = sourceSessionId;
                    result.Facts.Add(fact);
                    if (result.Facts.Count >= Math.Max(1, maxFacts)) break;
                }
                return result;
            }
            catch (Exception) { return null; }
        }

        private static string BuildCategoryInstruction(MemoryPolicy policy)
        {
            if (policy == null) return string.Empty;
            var allowed = new List<string> { "relationship", "general" };
            var forbidden = new List<string>();
            AddCategory(policy.rememberPlayerProfile, "player_profile（姓名、职业、偏好等档案）", allowed, forbidden);
            AddCategory(policy.rememberPromises, "promise（约定与承诺）", allowed, forbidden);
            AddCategory(policy.rememberQuestEvents, "quest（任务与剧情事件）", allowed, forbidden);
            AddCategory(policy.rememberCasualChat, "casual（日常闲聊）", allowed, forbidden);
            return "允许保留的类别仅为：" + string.Join("、", allowed) + "。"
                + (forbidden.Count == 0 ? string.Empty
                    : "禁止在summary和facts中保留以下类别，即使已有记忆中存在也要移除："
                        + string.Join("、", forbidden) + "。");
        }

        private static void AddCategory(bool enabled, string category, List<string> allowed,
            List<string> forbidden)
        {
            (enabled ? allowed : forbidden).Add(category);
        }
    }
}
