using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Llm;
using AIBot.Core.Logging;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>把 Core 回调转成附录B的 SSE 事件行，经 Channel 泵异步写出。</summary>
    internal sealed class ChatSseWriter : ILlmStreamSink, IReasoningSink, IToolExecutionSink
    {
        public static async Task ReplayAsync(HttpContext http, ChatRequestRecord record)
        {
            http.Response.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-AIBot-Replayed"] = "true";
            foreach (string line in record.events ?? new List<string>())
            {
                await http.Response.WriteAsync(line, http.RequestAborted);
            }
            await http.Response.Body.FlushAsync(http.RequestAborted);
        }

        public static Task PumpAsync(HttpContext http, ChannelReader<string> reader, RuntimeLogService runtimeLogs)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await foreach (string line in reader.ReadAllAsync(http.RequestAborted))
                    {
                        await http.Response.WriteAsync(line, http.RequestAborted);
                        await http.Response.Body.FlushAsync(http.RequestAborted);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    // 客户端断线/响应流关闭不应把已经成功完成的业务请求改写为 failed。
                    runtimeLogs.Write(LogLevel.Warning, "Chat", "response_stream_failed",
                        "SSE 响应写出失败，业务结果仍按原状态保留: " + ex.Message, null, ex);
                }
            });
        }

        private readonly ChannelWriter<string> _writer;
        private readonly List<string> _events = new List<string>();
        private int _replayBytes;
        public ModelErrorInfo LastModelError { get; private set; }

        private readonly CancellationToken _writeCancellation;

        public ChatSseWriter(ChannelWriter<string> writer, CancellationToken writeCancellation)
        {
            _writer = writer;
            _writeCancellation = writeCancellation;
        }

        public void OnToken(string delta)
        {
            Write(new JObject { ["type"] = "token", ["delta"] = delta });
        }

        public void OnReasoningToken(string delta)
        {
            Write(new JObject { ["type"] = "reasoning", ["delta"] = delta });
        }

        public void OnToolExecuted(ToolExecution execution)
        {
            Write(new JObject
            {
                ["type"] = "tool_call",
                ["callId"] = execution.Call.Id,
                ["name"] = execution.Call.Function.Name,
                ["args"] = ParseArgs(execution.Call.Function.Arguments),
                ["success"] = execution.Result.Success,
                ["result"] = execution.Result.MessageForModel
            });
        }

        public void OnError(Exception ex)
        {
            // AgentLoop 会把模型故障降级为 fallback reply。这里保留诊断，随 reply 一起下发，
            // 避免客户端先收到终止 error、随后又收到可用 fallback 的矛盾事件序列。
            LastModelError = ModelErrorContract.Classify(ex);
        }

        public void OnToolCall(ToolCallDto call) { }            // 聚合回调不下发；执行事件走 OnToolExecuted
        public void OnCompleted(string fullText, Usage usage) { }

        private static JToken ParseArgs(string argsJson)
        {
            try { return string.IsNullOrEmpty(argsJson) ? new JObject() : JObject.Parse(argsJson); }
            catch { return new JRaw(argsJson ?? ""); }
        }

        public void Write(JObject payload)
        {
            string line = "data: " + payload.ToString(Formatting.None) + "\n\n";
            string type = payload["type"]?.ToString();
            bool terminal = string.Equals(type, "reply", StringComparison.Ordinal)
                || string.Equals(type, "done", StringComparison.Ordinal)
                || string.Equals(type, "error", StringComparison.Ordinal)
                || string.Equals(type, "tool_pending", StringComparison.Ordinal);
            if (terminal)
            {
                int bytes = Encoding.UTF8.GetByteCount(line);
                lock (_events)
                {
                    if (_replayBytes + bytes <= 64 * 1024)
                    {
                        _events.Add(line);
                        _replayBytes += bytes;
                    }
                }
            }
            if (terminal)
            {
                try { _writer.WriteAsync(line, _writeCancellation).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (ChannelClosedException) { }
                catch (InvalidOperationException) { }
            }
            else
            {
                // 实时 token/reasoning 不阻塞模型线程；队列满时允许丢弃中间片段。
                _writer.TryWrite(line);
            }
        }

        public List<string> SnapshotEvents()
        {
            lock (_events) return new List<string>(_events);
        }

        /// <summary>
        /// 幂等重放只需终态事件；token/reasoning 会随回复长度线性膨胀，
        /// 不应写入 Session 文件或数据库 payload。
        /// </summary>
        public List<string> SnapshotReplayEvents()
        {
            lock (_events) return new List<string>(_events);
        }
    }
}
