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
}
