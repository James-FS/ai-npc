using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Llm;
using AIBot.Core.Output;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AIBot.Unity
{
    /// <summary>
    /// Unity 到 AIBot.Server 的聊天后端。
    /// Server 模式调用的是 Agent 级别聊天契约，而不是上游 OpenAI 契约，
    /// 因此 Unity 不再本地运行 AgentLoop，避免记忆、摘要和工具执行重复发生。
    /// </summary>
    public sealed class UnityServerBackend : ILlmBackend
    {
        private readonly string _baseUrl;
        private readonly string _gameId;
        private readonly string _npcId;
        private readonly string _playerId;
        private readonly string _sessionId;
        private readonly int _timeoutMs;

        public UnityServerBackend(string baseUrl, string gameId, string npcId,
            string playerId, string sessionId, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("Server 地址不能为空", nameof(baseUrl));
            if (string.IsNullOrWhiteSpace(gameId)) throw new ArgumentException("gameId 不能为空", nameof(gameId));
            if (string.IsNullOrWhiteSpace(npcId)) throw new ArgumentException("npcId 不能为空", nameof(npcId));
            _baseUrl = baseUrl.TrimEnd('/');
            _gameId = gameId;
            _npcId = npcId;
            _playerId = playerId ?? string.Empty;
            _sessionId = string.IsNullOrWhiteSpace(sessionId) ? "s-unity" : sessionId;
            _timeoutMs = Math.Max(1000, timeoutMs);
        }

        /// <summary>调用 Server 聊天接口并消费其 SSE 事件。</summary>
        public async Task<ServerChatResult> ChatAsync(string message, ILlmStreamSink sink, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("消息不能为空", nameof(message));
            bool streamed = false;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await OnceAsync(message, new GateSink(sink, () => streamed = true), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (ServerTransportException ex)
                {
                    bool retryable = ex.StatusCode == 0 || ex.StatusCode == 408
                        || ex.StatusCode == 429 || ex.StatusCode >= 500;
                    if (retryable && attempt < 1 && !streamed)
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }
                    var fallback = new LlmFallbackException(ex.Message, ex);
                    sink?.OnError(fallback);
                    throw fallback;
                }
            }
        }

        /// <summary>
        /// ILlmBackend 兼容入口：提取最后一条 user 消息，方便未来需要统一注入时复用。
        /// NpcAgent 的 Server 模式使用上面的 Agent 级 ChatAsync。
        /// </summary>
        public async Task ChatStreamAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct)
        {
            string message = null;
            if (request != null && request.Messages != null)
            {
                for (int i = request.Messages.Count - 1; i >= 0; i--)
                {
                    if (request.Messages[i] != null && request.Messages[i].Role == "user")
                    {
                        message = request.Messages[i].Content;
                        break;
                    }
                }
            }
            ServerChatResult result = await ChatAsync(message ?? string.Empty, sink, ct);
            if (result.Reply == null)
                throw new LlmFallbackException("AIBot.Server 未返回有效 reply");
            sink?.OnCompleted(JsonConvert.SerializeObject(result.Reply), result.Usage);
        }

        private async Task<ServerChatResult> OnceAsync(string message, GateSink sink, CancellationToken ct)
        {
            string url = _baseUrl + "/api/games/" + Uri.EscapeDataString(_gameId) + "/chat/stream";
            var body = new JObject
            {
                ["npcId"] = _npcId,
                ["message"] = message,
                ["sessionId"] = _sessionId
            };
            if (!string.IsNullOrEmpty(_playerId)) body["playerId"] = _playerId;

            var parser = new SseLineParser(sink.HandleDataLine);
            var handler = new SseDownloadHandler(parser);
            using (var req = new UnityWebRequest(url, "POST", handler, null))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                req.uploadHandler = new UploadHandlerRaw(bytes);
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "text/event-stream");
                req.timeout = Math.Max(1, (_timeoutMs + 999) / 1000);

                AsyncOperation operation = req.SendWebRequest();
                using (ct.Register(() => req.Abort()))
                {
                    while (!operation.isDone) await Task.Yield();
                }

                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (req.result != UnityWebRequest.Result.Success)
                {
                    string text = handler.RawText;
                    throw new ServerTransportException((int)req.responseCode,
                        string.IsNullOrEmpty(text) ? req.error : Truncate(text, 500));
                }
            }

            parser.Flush();
            if (sink.ErrorMessage != null)
                throw new ServerTransportException(500, sink.ErrorMessage);
            if (sink.Result == null)
                throw new ServerTransportException(502, "AIBot.Server 流结束但未返回 reply");
            return sink.Result;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? string.Empty;
            return value.Substring(0, max) + "…";
        }

        private sealed class GateSink : ILlmStreamSink, IReasoningSink
        {
            private readonly ILlmStreamSink _inner;
            private readonly Action _markStreamed;
            private readonly StructuredReplyStreamExtractor _speech = new StructuredReplyStreamExtractor();
            private readonly StringBuilder _raw = new StringBuilder();
            public ServerChatResult Result;
            public string ErrorMessage;

            public GateSink(ILlmStreamSink inner, Action markStreamed)
            {
                _inner = inner;
                _markStreamed = markStreamed;
            }

            public void HandleDataLine(string data)
            {
                JObject payload;
                try { payload = JObject.Parse(data); }
                catch { return; }
                string type = (string)payload["type"];
                if (type == "token")
                {
                    string delta = (string)payload["delta"] ?? string.Empty;
                    _raw.Append(delta);
                    if (!string.IsNullOrEmpty(delta)) _markStreamed();
                    string speech = _speech.Push(delta);
                    if (!string.IsNullOrEmpty(speech))
                    {
                        _inner?.OnToken(speech);
                    }
                }
                else if (type == "reasoning")
                {
                    _markStreamed();
                    var reasoning = _inner as IReasoningSink;
                    if (reasoning != null) reasoning.OnReasoningToken((string)payload["delta"] ?? string.Empty);
                }
                else if (type == "error")
                {
                    ErrorMessage = (string)payload["message"] ?? "Server 返回未知错误";
                }
                else if (type == "reply")
                {
                    JObject usage = payload["usage"] as JObject;
                    Result = new ServerChatResult
                    {
                        Reply = new StructuredReply
                        {
                            say = (string)payload["say"] ?? string.Empty,
                            emotion = (string)payload["emotion"] ?? "neutral",
                            action = (string)payload["action"] ?? "idle"
                        },
                        Usage = new Usage
                        {
                            PromptTokens = usage == null ? 0 : (int?)usage["promptTokens"] ?? 0,
                            CompletionTokens = usage == null ? 0 : (int?)usage["completionTokens"] ?? 0
                        },
                        UsedFallback = (bool?)payload["fallback"] ?? false,
                        ElapsedMs = (long?)payload["elapsedMs"] ?? 0
                    };
                    Result.Usage.TotalTokens = Result.Usage.PromptTokens + Result.Usage.CompletionTokens;
                }
            }

            public void OnToken(string delta) { _inner?.OnToken(delta); }
            public void OnToolCall(ToolCallDto call) { _inner?.OnToolCall(call); }
            public void OnCompleted(string fullText, Usage usage) { _inner?.OnCompleted(fullText, usage); }
            public void OnError(Exception ex) { ErrorMessage = ex?.Message ?? "Server 流错误"; _inner?.OnError(ex); }
            public void OnReasoningToken(string delta)
            {
                var reasoning = _inner as IReasoningSink;
                if (reasoning != null) reasoning.OnReasoningToken(delta);
            }
        }

        private sealed class SseDownloadHandler : DownloadHandlerScript
        {
            private readonly SseLineParser _parser;
            private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
            private readonly StringBuilder _raw = new StringBuilder();

            public string RawText { get { return _raw.ToString(); } }

            public SseDownloadHandler(SseLineParser parser) { _parser = parser; }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (dataLength <= 0) return true;
                char[] chars = new char[Encoding.UTF8.GetMaxCharCount(dataLength)];
                int decoded = _utf8.GetChars(data, 0, dataLength, chars, 0);
                if (decoded <= 0) return true;
                string text = new string(chars, 0, decoded);
                if (_raw.Length < 4096)
                {
                    int take = Math.Min(text.Length, 4096 - _raw.Length);
                    _raw.Append(text, 0, take);
                }
                _parser.Feed(text);
                return true;
            }
        }

        private sealed class ServerTransportException : Exception
        {
            public readonly int StatusCode;

            public ServerTransportException(int statusCode, string message)
                : base("AIBot.Server HTTP " + statusCode + (string.IsNullOrEmpty(message) ? "" : ": " + message))
            {
                StatusCode = statusCode;
            }
        }
    }

    public sealed class ServerChatResult
    {
        public StructuredReply Reply;
        public Usage Usage = new Usage();
        public bool UsedFallback;
        public long ElapsedMs;
    }
}
