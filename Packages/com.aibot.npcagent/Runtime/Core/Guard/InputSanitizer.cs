using System.Linq;

namespace AIBot.Core.Guard
{
    public sealed class SanitizeResult
    {
        public string Wrapped;      // 包裹后的 user 消息
        public bool Flagged;        // 命中高危句式（供日志与后续策略）
    }

    /// <summary>玩家输入包裹与注入检测（主方案 §5.8/附录A）。</summary>
    public static class InputSanitizer
    {
        private static readonly string[] InjectionPatterns =
        {
            "ignore previous", "ignore all previous", "ignore above",
            "system prompt", "忽略之前", "忽略以上", "忽略前面", "忽略所有",
            "系统提示", "跳出角色", "扮演另一个", "揭示你的提示", "显示你的设定"
        };

        public static SanitizeResult Sanitize(string message)
        {
            message = message ?? string.Empty;
            string lowered = message.ToLowerInvariant();
            bool flagged = InjectionPatterns.Any(p => lowered.Contains(p.ToLowerInvariant()));
            return new SanitizeResult
            {
                Wrapped = "[玩家说]" + message + "[/玩家说]",
                Flagged = flagged
            };
        }
    }
}
