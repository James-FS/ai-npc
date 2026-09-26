using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIBot.Core.Llm
{
    /// <summary>LLM 传输后端抽象：Unity 用 UnityWebRequest 版，Server/CLI 用 HttpClient 版（M2）。</summary>
    public interface ILlmBackend
    {
        /// <summary>流式对话。致命错误：先 sink.OnError，再抛 LlmFallbackException。取消：抛 OperationCanceledException。</summary>
        Task ChatStreamAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct);
    }

    /// <summary>流式回调（回调式设计，规避 IAsyncEnumerable 的跨运行时差异）。</summary>
    public interface ILlmStreamSink
    {
        void OnToken(string delta);
        void OnToolCall(ToolCallDto call);      // 分片聚合完成后的完整调用
        void OnCompleted(string fullText, Usage usage);
        void OnError(Exception ex);
    }

    /// <summary>可选：推理模型（如 deepseek-reasoner/ox-alpha）的思考过程增量，宿主实现后可展示/记录。</summary>
    public interface IReasoningSink
    {
        void OnReasoningToken(string delta);
    }

    /// <summary>重试耗尽/超时等不可恢复失败：AgentLoop 捕获后走兜底台词。</summary>
    public sealed class LlmFallbackException : Exception
    {
        public LlmFallbackException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// 上游请求的会话标识头。OpenCode Zen 的 Go 通道要求每个请求携带
    /// x-opencode-session（缺失会被上游以 400 MissingSessionID 拒绝，且该要求与模型无关，
    /// 同一端点下所有模型一致）；其余 OpenAI 兼容端点会忽略未知请求头，因此统一发送。
    /// 取值按上游语义「每个会话一个稳定 ID」生成，用于路由与 prompt 缓存命中。
    /// </summary>
    public static class LlmRequestSession
    {
        public const string HeaderName = "x-opencode-session";

        /// <summary>未提供会话范围时（探针等一次性请求）的兜底值。</summary>
        public static readonly string Default = "aibot-" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// 由会话身份（game/npc/player/session）派生的稳定 ID：同一会话的每轮请求取到同一个值，
        /// 不同会话互不相同；对同一范围可重现，Server 重启后仍能命中既有路由与缓存。
        /// </summary>
        public static string For(params string[] parts)
        {
            if (parts == null || parts.Length == 0) return Default;
            string scope = string.Join("|", parts);
            if (string.IsNullOrWhiteSpace(scope.Replace("|", string.Empty))) return Default;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(scope));
                var sb = new System.Text.StringBuilder("aibot-", 38);
                for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
