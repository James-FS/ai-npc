using System;
using AIBot.Core.Config;
using Newtonsoft.Json;

namespace AIBot.Core.Output
{
    /// <summary>结构化回复：游戏侧只消费此对象（say→气泡，emotion/action→表情/动画）。</summary>
    public class StructuredReply
    {
        public string say;
        public string emotion;
        public string action;
    }

    /// <summary>三层容错解析：剥围栏 → 截取 {..} → 反序列化 + 枚举回退。</summary>
    public static class StructuredReplyParser
    {
        public static bool TryParse(string raw, OutputSettings output, out StructuredReply reply)
        {
            reply = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string json = StripFences(raw);
            int start = json.IndexOf('{');
            if (start < 0) return false;
            int end = json.LastIndexOf('}');
            string candidate = end > start
                ? json.Substring(start, end - start + 1)
                : json.Substring(start);                       // 无闭合括号 = 截断输出，仍交给挽救路径

            try
            {
                StructuredReply parsed = JsonConvert.DeserializeObject<StructuredReply>(candidate);
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.say)) return false;
                parsed.emotion = ValidateEnum(parsed.emotion, output != null ? output.emotions : null, "neutral");
                parsed.action = ValidateEnum(parsed.action, output != null ? output.actions : null, "idle");
                reply = parsed;
                return true;
            }
            catch (Exception)
            {
                return TrySalvageSay(json, output, out reply);
            }
        }

        /// <summary>挽救路径：JSON 被截断（推理模型耗尽 maxTokens）时，正则直接抽取 say 字段。</summary>
        private static bool TrySalvageSay(string text, OutputSettings output, out StructuredReply reply)
        {
            reply = null;
            var match = System.Text.RegularExpressions.Regex.Match(
                text, "\"say\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!match.Success) return false;
            try
            {
                string say = JsonConvert.DeserializeObject<string>("\"" + match.Groups[1].Value + "\"");
                if (string.IsNullOrWhiteSpace(say)) return false;
                reply = new StructuredReply
                {
                    say = say,
                    emotion = "neutral",
                    action = "idle"
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>剥 markdown 代码围栏（```json ... ``` 等）。</summary>
        public static string StripFences(string text)
        {
            if (!text.Contains("```")) return text;
            int first = text.IndexOf("```");
            int last = text.LastIndexOf("```");
            if (last <= first) return text;
            string inner = text.Substring(first + 3, last - first - 3);
            int newline = inner.IndexOf('\n');
            if (newline >= 0 && !inner.Substring(0, newline).TrimStart().StartsWith("{")) inner = inner.Substring(newline + 1);
            return inner;
        }

        private static string ValidateEnum(string value, System.Collections.Generic.List<string> allowed, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            if (allowed == null || allowed.Count == 0) return value;
            return allowed.Contains(value) ? value : fallback;
        }
    }
}
