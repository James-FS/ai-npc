using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Llm;
using AIBot.Core;
using AIBot.Core.Output;
using AIBot.Core.Protocol;
using AIBot.Core.Tools;
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
        private readonly string _authToken;
        private readonly Func<string> _stateSnapshotProvider;
        private readonly bool _enableSimulatedTools;
        private readonly bool _enableGameTools;
        private readonly Func<List<ClientToolDescriptor>> _toolSchemaProvider;

        public string LastRequestId { get; private set; }

        public UnityServerBackend(string baseUrl, string gameId, string npcId,
            string playerId, string sessionId, int timeoutMs,
            Func<string> stateSnapshotProvider = null, bool enableSimulatedTools = false,
            string authToken = null,
            bool enableGameTools = false, Func<List<ClientToolDescriptor>> toolSchemaProvider = null)
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
            _authToken = authToken ?? string.Empty;
            _stateSnapshotProvider = stateSnapshotProvider;
            _enableSimulatedTools = enableSimulatedTools;
            _enableGameTools = enableGameTools;
            _toolSchemaProvider = toolSchemaProvider;
        }

        /// <summary>调用 Server 聊天接口并消费其 SSE 事件。</summary>
        public async Task<ServerChatResult> ChatAsync(string message, ILlmStreamSink sink, CancellationToken ct,
            string requestId = null)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("消息不能为空", nameof(message));
            requestId = NormalizeRequestId(requestId);
            LastRequestId = requestId;
            var gate = new GateSink(sink);
            return await SendWithRetryAsync(BuildChatBody(message, requestId), requestId, gate, sink, ct);
        }

        /// <summary>
        /// game 模式续跑：本地工具执行完毕后，携带挂起轮令牌与结果发起第二个请求。
        /// 协议契约见主方案 §8.4：Server 从挂起态恢复 AgentLoop，本轮可能返回 reply 或再次 tool_pending。
        /// </summary>
        public async Task<ServerChatResult> ChatResumeAsync(string roundToken, List<ClientToolResult> toolResults,
            ILlmStreamSink sink, CancellationToken ct, string requestId = null)
        {
            if (string.IsNullOrWhiteSpace(roundToken)) throw new ArgumentException("roundToken 不能为空", nameof(roundToken));
            requestId = NormalizeRequestId(requestId);
            LastRequestId = requestId;
            var gate = new GateSink(sink);
            return await SendWithRetryAsync(BuildResumeBody(roundToken, toolResults, requestId), requestId, gate, sink, ct);
        }

        private string NormalizeRequestId(string requestId)
        {
            requestId = string.IsNullOrWhiteSpace(requestId) ? ChatRequestIds.NewId() : requestId;
            if (!ChatRequestIds.IsValid(requestId)) throw new ArgumentException("requestId 格式无效", nameof(requestId));
            return requestId;
        }

        private async Task<ServerChatResult> SendWithRetryAsync(JObject body, string requestId,
            GateSink gate, ILlmStreamSink sink, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                gate.BeginAttempt();
                try
                {
                    return await OnceAsync(body, requestId, gate, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (ServerTransportException ex)
                {
                    bool retryable = ex.StatusCode == 0 || ex.StatusCode == 408
                        || ex.StatusCode == 429 || ex.StatusCode >= 500;
                    if (retryable && attempt < 2 && !ex.CompletedResponse)
                    {
                        await Task.Delay(500 * (attempt + 1), ct);
                        continue;
                    }
                    var fallback = new LlmFallbackException(ex.Message, ex);
                    sink?.OnError(fallback);
                    throw fallback;
                }
            }
        }

        /// <summary>主动检查 Server 健康状态；不会触发模型请求，也不会影响聊天重试策略。</summary>
        public async Task<ServerHealthResult> CheckHealthAsync(CancellationToken ct)
        {
            string url = _baseUrl + "/api/health";
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = Math.Max(1, (_timeoutMs + 999) / 1000);
                AsyncOperation operation = req.SendWebRequest();
                using (ct.Register(() => req.Abort()))
                {
                    while (!operation.isDone) await Task.Yield();
                }

                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (req.result != UnityWebRequest.Result.Success)
                {
                    string detail = req.downloadHandler == null ? req.error : req.downloadHandler.text;
                    throw new ServerTransportException((int)req.responseCode,
                        string.IsNullOrEmpty(detail) ? req.error : Truncate(detail, 500));
                }

                try
                {
                    JObject payload = JObject.Parse(req.downloadHandler.text);
                    return new ServerHealthResult
                    {
                        Ok = (bool?)payload["ok"] ?? false,
                        Version = (string)payload["version"],
                        Storage = (string)payload["storage"],
                        Message = "AIBot.Server 连接正常"
                    };
                }
                catch (Exception ex)
                {
                    throw new ServerTransportException(502, "Server 健康检查返回格式无效：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 检查 Server 是否可用：网络可达、依赖已就绪，并确认当前 Game/NPC 存在。
        /// 这是显式诊断接口，会产生少量管理请求，不会自动参与聊天链路。
        /// </summary>
        public async Task<ServerConnectionResult> CheckConnectionAsync(CancellationToken ct)
        {
            ServerHealthResult health = await CheckHealthAsync(ct);
            JsonProbe ready = await GetJsonAsync("/api/ready", ct);
            JsonProbe npcs = await GetJsonAsync(
                "/api/games/" + Uri.EscapeDataString(_gameId) + "/npcs", ct);

            bool readyOk = (bool?)ready.Payload?["ready"] ?? false;
            bool npcFound = false;
            JArray npcIds = npcs.Payload?["npcs"] as JArray;
            if (npcIds != null)
            {
                foreach (JToken value in npcIds)
                {
                    if (string.Equals((string)value, _npcId, StringComparison.OrdinalIgnoreCase))
                    {
                        npcFound = true;
                        break;
                    }
                }
            }

            string message;
            if (!readyOk)
                message = "AIBot.Server 可访问，但尚未就绪（请检查模型配置、存储和默认 NPC）";
            else if (!npcFound)
                message = "AIBot.Server 已就绪，但找不到 NPC：" + _npcId;
            else
                message = "AIBot.Server 已就绪，NPC 可用";

            return new ServerConnectionResult
            {
                Reachable = health.Ok,
                Ready = readyOk,
                NpcFound = npcFound,
                Version = health.Version,
                Storage = health.Storage,
                Message = message
            };
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

        private JObject BuildChatBody(string message, string requestId)
        {
            var body = new JObject
            {
                ["npcId"] = _npcId,
                ["message"] = message,
                ["sessionId"] = _sessionId,
                ["requestId"] = requestId,
                ["toolMode"] = _enableSimulatedTools ? ServerToolModes.Simulated
                    : _enableGameTools ? ServerToolModes.Game : ServerToolModes.None
            };
            if (!string.IsNullOrEmpty(_playerId)) body["playerId"] = _playerId;
            ApplyStateSnapshot(body, includeSimState: true);
            if (_enableGameTools && _toolSchemaProvider != null)
            {
                List<ClientToolDescriptor> descriptors = _toolSchemaProvider.Invoke();
                if (descriptors != null && descriptors.Count > 0) body["tools"] = JArray.FromObject(descriptors);
            }
            return body;
        }

        private JObject BuildResumeBody(string roundToken, List<ClientToolResult> toolResults, string requestId)
        {
            var body = new JObject
            {
                ["npcId"] = _npcId,
                ["sessionId"] = _sessionId,
                ["requestId"] = requestId,
                ["toolMode"] = ServerToolModes.Game,
                ["roundToken"] = roundToken
            };
            if (!string.IsNullOrEmpty(_playerId)) body["playerId"] = _playerId;
            // 续跑不带 simState：其值已随首段合并进会话，且在请求指纹内——重试时游戏状态若已
            // 变化会触发 409。gameContext 不进指纹，可安全携带最新状态供 prompt 使用。
            ApplyStateSnapshot(body, includeSimState: false);
            var results = new JArray();
            foreach (ClientToolResult item in toolResults ?? new List<ClientToolResult>())
            {
                results.Add(new JObject
                {
                    ["callId"] = item?.CallId,
                    ["success"] = item?.Success ?? false,
                    ["message"] = item?.Message
                });
            }
            body["toolResults"] = results;
            return body;
        }

        private void ApplyStateSnapshot(JObject body, bool includeSimState)
        {
            string stateSnapshot = _stateSnapshotProvider == null ? null : _stateSnapshotProvider();
            if (string.IsNullOrWhiteSpace(stateSnapshot)) return;
            if (_enableGameTools)
            {
                // game 模式：快照原文经 gameContext 直达 prompt（CompositeGameContext），
                // 不会被 SimState 的固定字段结构吞掉（如任务状态、关键道具等富状态）。
                body["gameContext"] = stateSnapshot;
            }
            if (!includeSimState) return;
            try
            {
                // Server 端只接收 SimGameState 的已知字段；额外字段会被忽略，
                // 因此可以安全地把游戏上下文快照作为可选扩展传过去。
                JToken state = JToken.Parse(stateSnapshot);
                if (state.Type == JTokenType.Object) body["simState"] = state;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AIBot] 游戏状态快照不是有效 JSON，忽略本次同步：" + ex.Message);
            }
        }

        private async Task<ServerChatResult> OnceAsync(JObject body, string requestId,
            GateSink sink, CancellationToken ct)
        {
            string url = _baseUrl + "/api/games/" + Uri.EscapeDataString(_gameId) + "/chat/stream";

            var parser = new SseLineParser(sink.HandleDataLine);
            var handler = new SseDownloadHandler(parser);
            using (var req = new UnityWebRequest(url, "POST", handler, null))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                req.uploadHandler = new UploadHandlerRaw(bytes);
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "text/event-stream");
                req.SetRequestHeader("X-Request-Id", requestId);
                if (!string.IsNullOrWhiteSpace(_authToken))
                    req.SetRequestHeader("Authorization", "Bearer " + _authToken);
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
                sink.Replayed = sink.Replayed || string.Equals(req.GetResponseHeader("X-AIBot-Replayed"),
                    "true", StringComparison.OrdinalIgnoreCase);
            }

            parser.Flush();
            // Server 可能把上游模型故障降级为 fallback reply；只要收到有效 reply，就应优先交付。
            ServerChatResult completed = sink.Result;
            if (completed != null) return completed;
            if (sink.ErrorMessage != null)
                throw new ServerTransportException(sink.ErrorStatus, sink.ErrorMessage, completedResponse: true);
            throw new ServerTransportException(502, "AIBot.Server 流结束但未返回 reply");
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? string.Empty;
            return value.Substring(0, max) + "…";
        }

        private async Task<JsonProbe> GetJsonAsync(string path, CancellationToken ct)
        {
            using (var req = UnityWebRequest.Get(_baseUrl + path))
            {
                if (!string.IsNullOrWhiteSpace(_authToken))
                    req.SetRequestHeader("Authorization", "Bearer " + _authToken);
                req.timeout = Math.Max(1, (_timeoutMs + 999) / 1000);
                AsyncOperation operation = req.SendWebRequest();
                using (ct.Register(() => req.Abort()))
                {
                    while (!operation.isDone) await Task.Yield();
                }

                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                string text = req.downloadHandler == null ? string.Empty : req.downloadHandler.text;
                JObject payload = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { payload = JObject.Parse(text); }
                    catch (Exception ex)
                    {
                        throw new ServerTransportException(502, "Server 返回格式无效：" + ex.Message);
                    }
                }
                if (req.result != UnityWebRequest.Result.Success && payload == null)
                {
                    throw new ServerTransportException((int)req.responseCode,
                        string.IsNullOrEmpty(text) ? req.error : Truncate(text, 500));
                }
                return new JsonProbe { StatusCode = (int)req.responseCode, Payload = payload };
            }
        }

        private sealed class JsonProbe
        {
            public int StatusCode;
            public JObject Payload;
        }

        private sealed class GateSink : ILlmStreamSink, IReasoningSink
        {
            private readonly ILlmStreamSink _inner;
            private readonly ServerChatResponseState _state = new ServerChatResponseState();
            private readonly StringBuilder _deliveredTokens = new StringBuilder();
            private readonly StringBuilder _attemptTokens = new StringBuilder();
            private readonly StringBuilder _deliveredReasoning = new StringBuilder();
            private readonly StringBuilder _attemptReasoning = new StringBuilder();
            private readonly HashSet<string> _deliveredTools = new HashSet<string>(StringComparer.Ordinal);
            private string _transportErrorMessage;

            public bool Replayed { get; set; }

            public ServerChatResult Result
            {
                get
                {
                    ServerChatEvent reply = _state.ReplyEvent;
                    if (reply != null)
                    {
                        return new ServerChatResult
                        {
                            Reply = reply.Reply,
                            Usage = reply.Usage,
                            UsedFallback = reply.Fallback,
                            ElapsedMs = reply.ElapsedMs,
                            Diagnostic = reply.Diagnostic,
                            RequestId = _state.RequestId,
                            Replayed = Replayed
                        };
                    }
                    // game 模式：tool_pending 是合法终态，本轮没有 reply，等待宿主执行工具后续跑。
                    ServerChatEvent pending = _state.PendingEvent;
                    if (pending != null)
                    {
                        return new ServerChatResult
                        {
                            PendingToolCalls = pending.ToolCalls,
                            RoundToken = pending.RoundToken,
                            Usage = pending.Usage,
                            RequestId = _state.RequestId,
                            Replayed = Replayed
                        };
                    }
                    return null;
                }
            }

            public string ErrorMessage
            {
                get { return _transportErrorMessage ?? _state.CompletionError?.Message; }
            }

            public int ErrorStatus
            {
                get { return _state.CompletionError?.Status ?? 500; }
            }

            public GateSink(ILlmStreamSink inner)
            {
                _inner = inner;
            }

            public void BeginAttempt()
            {
                _attemptTokens.Clear();
                _attemptReasoning.Clear();
                _transportErrorMessage = null;
            }

            public void HandleDataLine(string data)
            {
                if (!ServerChatEventParser.TryParse(data, out ServerChatEvent parsed)) return;
                _state.Apply(parsed);
                if (parsed.Kind == ServerChatEventKind.Token)
                {
                    string delta = parsed.Delta ?? string.Empty;
                    // Server token 已经是 AgentLoop 提取后的纯台词，不能再次按 {"say":...} 解析。
                    ForwardReplaySafe(delta, _attemptTokens, _deliveredTokens, value => _inner?.OnToken(value));
                }
                else if (parsed.Kind == ServerChatEventKind.Reasoning)
                {
                    var reasoning = _inner as IReasoningSink;
                    ForwardReplaySafe(parsed.Delta ?? string.Empty, _attemptReasoning, _deliveredReasoning,
                        value => reasoning?.OnReasoningToken(value));
                }
                else if (parsed.Kind == ServerChatEventKind.ToolCall)
                {
                    string toolKey = !string.IsNullOrEmpty(parsed.ToolCallId)
                        ? parsed.ToolCallId
                        : (parsed.ToolName ?? "") + "|" + (parsed.ToolArgumentsJson ?? "{}") + "|" + (parsed.ToolResult ?? "");
                    if (!_deliveredTools.Add(toolKey)) return;
                    var toolSink = _inner as IToolExecutionSink;
                    if (toolSink != null)
                    {
                        toolSink.OnToolExecuted(new ToolExecution
                        {
                            Call = new ToolCallDto
                            {
                                Id = parsed.ToolCallId,
                                Function = new FunctionCall
                                {
                                    Name = parsed.ToolName,
                                    Arguments = parsed.ToolArgumentsJson ?? "{}"
                                }
                            },
                            Result = new ToolResult
                            {
                                Success = parsed.ToolSuccess,
                                MessageForModel = parsed.ToolResult
                            }
                        });
                    }
                }
            }

            private static void ForwardReplaySafe(string delta, StringBuilder attempt,
                StringBuilder delivered, Action<string> forward)
            {
                if (string.IsNullOrEmpty(delta)) return;
                attempt.Append(delta);
                string current = attempt.ToString();
                if (current.Length <= delivered.Length)
                {
                    if (!delivered.ToString().StartsWith(current, StringComparison.Ordinal))
                        throw new InvalidOperationException("Server 对同一 requestId 返回了不一致的重放内容");
                    return;
                }
                if (delivered.Length > 0
                    && !current.StartsWith(delivered.ToString(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Server 对同一 requestId 返回了不一致的重放内容");
                string suffix = current.Substring(delivered.Length);
                delivered.Append(suffix);
                forward?.Invoke(suffix);
            }

            public void OnToken(string delta) { _inner?.OnToken(delta); }
            public void OnToolCall(ToolCallDto call) { _inner?.OnToolCall(call); }
            public void OnCompleted(string fullText, Usage usage) { _inner?.OnCompleted(fullText, usage); }
            public void OnError(Exception ex) { _transportErrorMessage = ex?.Message ?? "Server 流错误"; _inner?.OnError(ex); }
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
            public readonly bool CompletedResponse;

            public ServerTransportException(int statusCode, string message, bool completedResponse = false)
                : base("AIBot.Server HTTP " + statusCode + (string.IsNullOrEmpty(message) ? "" : ": " + message))
            {
                StatusCode = statusCode;
                CompletedResponse = completedResponse;
            }
        }
    }

    public sealed class ServerChatResult
    {
        public StructuredReply Reply;
        public Usage Usage = new Usage();
        public bool UsedFallback;
        public long ElapsedMs;
        public ServerModelDiagnostic Diagnostic;
        public string RequestId;
        public bool Replayed;
        public List<ToolCallDto> PendingToolCalls; // game 模式：非空表示本轮挂起等待本地工具执行
        public string RoundToken;                  // game 模式：续跑请求必须携带的挂起轮令牌
    }

    public sealed class ServerHealthResult
    {
        public bool Ok;
        public string Version;
        public string Storage;
        public string Message;
    }

    public sealed class ServerConnectionResult
    {
        public bool Reachable;
        public bool Ready;
        public bool NpcFound;
        public string Version;
        public string Storage;
        public string Message;
    }
}

