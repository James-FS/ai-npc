using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Logging;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using AIBot.Core.Output;
using AIBot.Core.Protocol;
using AIBot.Core.Tools;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBot.Unity
{
    /// <summary>Local/Server 共用的工具执行通知；它只报告结果，不会重复执行工具。</summary>
    [Serializable]
    public sealed class AgentToolExecutionEvent
    {
        public string toolName;
        public string argumentsJson;
        public bool success;
        public string result;
    }

    /// <summary>
    /// NPC Agent 主组件：挂到角色上，调用 Chat(message) 获得流式回复。
    /// 配置来源：Server 连接 Profile、configAsset（SO）或 npcId + gameId（data/ 下的 JSON）。
    /// </summary>
    public class NpcAgent : MonoBehaviour
    {
        public string gameId = "default";
        public string npcId = "blacksmith_wang";

        [Header("运行连接")]
        [Tooltip("Server 模式下可选的稳定玩家 ID；留空时服务端使用 session 范围记忆。")]
        public string playerId;
        [Tooltip("Server 模式下的会话 ID；同一会话持续对话即可复用短期记忆。")]
        public string sessionId = "s-unity";

        [Tooltip("Local 模式留空则从 data/games/{gameId}/npcs/{npcId}.json 加载；Server Profile 模式下可留空")]
        public AgentConfigAsset configAsset;

        [Tooltip("Local 模式可选。填写后无需从 data/ 目录加载 world.json。")]
        public WorldConfigAsset worldConfigAsset;

        [Tooltip("Server 模式推荐使用。填写后无需在 Unity 本地保存 NPC 人设、模型或 API Key。")]
        public AIBotConnectionProfile connectionProfile;

        [Tooltip("可选：挂载一个实现 AIBot.Core.Context.IGameContext 的游戏状态组件；留空则使用内置 GameContextRelay。")]
        public MonoBehaviour gameContextProvider;

        [Tooltip("仅开发期使用。留空时依次使用配置 JSON 与环境变量 AIBOT_LLM_KEY；正式发布应走服务端中转。")]
        public string apiKeyOverride;

        public GameContextRelay gameContext;

        [Header("事件")]
        public UnityEngine.Events.UnityEvent<string> onToken;
        public UnityEngine.Events.UnityEvent<string> onReasoning;    // 推理模型的思考过程增量（可接 UI/调试）
        public UnityEngine.Events.UnityEvent<AgentToolExecutionEvent> onToolExecuted;
        public UnityEngine.Events.UnityEvent<StructuredReply> onReply;
        public UnityEngine.Events.UnityEvent<string> onError;
        /// <summary>模型失败但仍交付 fallback 回复时触发；不会替代 onReply。</summary>
        public UnityEngine.Events.UnityEvent<string> onFallback;
        /// <summary>当前请求被取消时触发，供 UI 复位状态。</summary>
        public UnityEngine.Events.UnityEvent onCancelled;
        public UnityEngine.Events.UnityEvent onBusy;
        public UnityEngine.Events.UnityEvent<string> onServerStatus;

        private AgentConfigDto _config;
        private WorldConfigDto _world;
        private UnityWebRequestBackend _backend;
        private UnityServerBackend _serverBackend;
        private AgentLoop _loop;
        private ShortTermMemory _memory;
        private MemoryPolicy _memoryPolicy;
        private ToolRegistry _tools;
        private CancellationTokenSource _lifetimeCts;
        private CancellationTokenSource _requestCts;
        private bool _running;
        private bool _capabilityWarningIssued;
        private bool _serverGameTools;          // game 模式：Server 会把工具调用挂起回传本地执行
        private readonly System.Collections.Generic.HashSet<string> _executedRoundTokens =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal); // 防重放导致的工具双执行
        private string _loadedGameId;
        private string _loadedNpcId;

        /// <summary>当前是否正在处理一轮对话。</summary>
        public bool IsBusy { get { return _running; } }
        /// <summary>最近一次 Server 对话的幂等请求 ID，可用于显式断线恢复。</summary>
        public string LastServerRequestId { get { return _serverBackend?.LastRequestId; } }

        private string _sessionSummary;
        private System.Collections.Generic.List<string> _sessionFacts;

        /// <summary>游戏把真实工具注册到这里（Awake 之后、Chat 之前调用）。</summary>
        public ToolRegistry Tools
        {
            get
            {
                if (_tools == null) _tools = new ToolRegistry();
                return _tools;
            }
        }

        public AgentConfigDto Config
        {
            get
            {
                if (_config == null) LoadConfig();
                return _config;
            }
        }

        private void Awake()
        {
            if (onFallback == null) onFallback = new UnityEngine.Events.UnityEvent<string>();
            if (onCancelled == null) onCancelled = new UnityEngine.Events.UnityEvent();
            LoadConfig();
            _memoryPolicy = _memoryPolicy ?? MemoryPolicy.Defaults();
            int turns = _memoryPolicy.shortTermTurns;
            _memory = new ShortTermMemory(Math.Max(2, turns * 2));
            EnsureGameContext();
        }

        private void OnEnable()
        {
            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();
        }

        private void OnDisable()
        {
            CancelRunning();
            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }

        public void CancelRunning()
        {
            _requestCts?.Cancel();
        }

        /// <summary>
        /// 主动检查当前 Server 配置。不会自动调用，避免给 Local 模式或每次聊天增加网络开销。
        /// </summary>
        public async Task<bool> CheckServerAsync()
        {
            if (_config == null) LoadConfig();
            if (_config == null || !IsServerMode() || _serverBackend == null)
            {
                onServerStatus?.Invoke("当前不是 Server 模式");
                return false;
            }

            try
            {
                ServerConnectionResult result = await _serverBackend.CheckConnectionAsync(
                    _lifetimeCts == null ? CancellationToken.None : _lifetimeCts.Token);
                string status = result.Message
                    + (string.IsNullOrEmpty(result.Version) ? "" : "（" + result.Version + "）");
                onServerStatus?.Invoke(status);
                return result.Reachable && result.Ready && result.NpcFound;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                string status = "AIBot.Server 连接失败：" + ex.Message;
                onServerStatus?.Invoke(status);
                return false;
            }
        }

        public async void Chat(string message)
        {
            await ChatAsync(message);
        }

        /// <summary>可等待、可测试的对话入口；Chat(string) 仅作为 UnityEvent 兼容包装。</summary>
        public Task<AgentLoopResult> ChatAsync(string message)
        {
            return ChatInternalAsync(message, null);
        }

        /// <summary>使用原 requestId 恢复 Server 请求；相同 ID 必须搭配完全相同的消息与上下文。</summary>
        public Task<AgentLoopResult> RetryServerRequestAsync(string message, string requestId)
        {
            if (!ChatRequestIds.IsValid(requestId)) throw new ArgumentException("requestId 格式无效", nameof(requestId));
            return ChatInternalAsync(message, requestId);
        }

        private async Task<AgentLoopResult> ChatInternalAsync(string message, string requestId)
        {
            if (_running)
            {
                Debug.LogWarning("[AIBot] 上一轮对话未结束，忽略新输入");
                onBusy?.Invoke();
                return null;
            }
            if (_config == null) LoadConfig();
            if (_config == null) { EmitError("配置加载失败：" + gameId + "/" + npcId); return null; }
            ValidateRuntimeCapabilities();

            _running = true;
            _requestCts?.Dispose();
            _requestCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();
            try
            {
                if (IsServerMode())
                {
                    const int MaxGameToolRounds = 6;
                    UnityStreamSink sink = new UnityStreamSink(this);
                    ServerChatResult serverResult = await _serverBackend.ChatAsync(
                        message, sink, _requestCts.Token, requestId);

                    // game 模式自动续跑链：Server 挂起 → 本地工具真实执行 → 携带结果续跑，
                    // 直到模型给出 reply 或达到轮数上限。工具执行语义与 Local 模式完全一致。
                    int gameRound = 0;
                    while (serverResult != null && serverResult.PendingToolCalls != null
                        && serverResult.PendingToolCalls.Count > 0)
                    {
                        if (string.IsNullOrEmpty(serverResult.RoundToken)
                            || !_executedRoundTokens.Add(serverResult.RoundToken))
                        {
                            // 同一挂起轮只执行一次：断线重放会原样回放 tool_pending，
                            // 此时结果已在 Server 侧消费过，不能再次执行游戏工具。
                            EmitError("收到重复或无效的工具挂起轮: " + (serverResult.RoundToken ?? "<empty>"));
                            return null;
                        }
                        if (_executedRoundTokens.Count > 128)
                        {
                            // 有界防泄漏：留下最近执行过的令牌即可覆盖重放窗口
                            foreach (string token in _executedRoundTokens)
                            {
                                _executedRoundTokens.Remove(token);
                                if (_executedRoundTokens.Count <= 64) break;
                            }
                        }
                        if (++gameRound > MaxGameToolRounds)
                        {
                            EmitError("工具回传轮数超过上限（" + MaxGameToolRounds + "），已终止本轮对话");
                            return null;
                        }

                        var results = new List<ClientToolResult>();
                        foreach (ToolCallDto call in serverResult.PendingToolCalls)
                        {
                            ToolResult executed = await ExecuteLocalToolAsync(call);
                            results.Add(new ClientToolResult
                            {
                                CallId = call.Id,
                                Success = executed != null && executed.Success,
                                Message = executed == null ? null : executed.MessageForModel
                            });
                        }

                        serverResult = await _serverBackend.ChatResumeAsync(
                            serverResult.RoundToken, results, sink, _requestCts.Token);
                    }

                    if (serverResult == null || serverResult.Reply == null)
                    {
                        EmitError("AIBot.Server 未返回有效回复");
                        return null;
                    }
                    var directResult = new AgentLoopResult
                    {
                        Reply = serverResult.Reply,
                        Usage = serverResult.Usage,
                        ElapsedMs = serverResult.ElapsedMs,
                        UsedFallback = serverResult.UsedFallback
                    };
                    if (directResult.UsedFallback)
                    {
                        onFallback?.Invoke(serverResult.Diagnostic == null
                            ? "Server 返回了兜底回复"
                            : (serverResult.Diagnostic.Code ?? "Server 返回了兜底回复"));
                    }
                    onReply?.Invoke(directResult.Reply);
                    return directResult;
                }

                var input = new AgentRunInput
                {
                    Config = _config,
                    World = _world,
                    Game = GetGameContext(),
                    UserMessage = message,
                    Memory = _memory,
                    Tools = _tools,
                    HostContext = gameObject,
                    MemorySummary = _sessionSummary,
                    MemoryFacts = _sessionFacts,
                    ResolvedMemoryPolicy = _memoryPolicy,
                    NotifyReplyBeforeSummary = true
                };
                AgentLoopResult result = await _loop.RunAsync(input, new UnityStreamSink(this), _requestCts.Token);
                if (result.UsedFallback) onFallback?.Invoke(result.FallbackReason ?? "模型未返回有效回复");
                // 摘要式长期记忆写回（下一次对话注入）
                if (result.MemorySummary != null)
                {
                    _sessionSummary = result.MemorySummary;
                    _sessionFacts = result.MemoryFacts;
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                onCancelled?.Invoke();
                return null;
            }
            catch (Exception ex)
            {
                UnityLogSink.Instance.Log(LogLevel.Error, "Chat failed: " + ex.Message, ex);
                EmitError(ex.Message);
                return null;
            }
            finally
            {
                _requestCts?.Dispose();
                _requestCts = null;
                _running = false;
            }
        }

        private void LoadConfig()
        {
            bool useConnectionProfile = connectionProfile != null;
            string resolvedGameId = useConnectionProfile && !string.IsNullOrWhiteSpace(connectionProfile.gameId)
                ? connectionProfile.gameId : gameId;
            string resolvedNpcId = useConnectionProfile && !string.IsNullOrWhiteSpace(connectionProfile.npcId)
                ? connectionProfile.npcId : npcId;

            if (useConnectionProfile
                && (string.IsNullOrWhiteSpace(resolvedGameId) || string.IsNullOrWhiteSpace(resolvedNpcId)))
            {
                Debug.LogError("[AIBot] Server Connection Profile 必须填写 gameId 和 npcId");
                _config = null;
                return;
            }

            if (useConnectionProfile)
            {
                // Server 模式不需要加载完整 NPC 配置；仅构造一个本地运行时占位 DTO。
                _config = new AgentConfigDto
                {
                    npcId = resolvedNpcId,
                    worldId = resolvedGameId,
                    runtimeMode = "server",
                    serverBaseUrl = connectionProfile.serverBaseUrl,
                    model = new ModelSettings { timeoutMs = connectionProfile.timeoutMs }
                };
            }
            else
            {
                _config = configAsset != null ? configAsset.ToDto() : DevConfigStore.LoadNpc(gameId, npcId);
            }
            if (_config == null) return;
            _loadedGameId = resolvedGameId;
            _loadedNpcId = resolvedNpcId;
            if (_config.model == null) _config.model = new ModelSettings();
            if (!useConnectionProfile)
            {
                if (!string.IsNullOrEmpty(apiKeyOverride)) _config.model.apiKey = apiKeyOverride;
                if (string.IsNullOrEmpty(_config.model.apiKey))
                    _config.model.apiKey = Environment.GetEnvironmentVariable("AIBOT_LLM_KEY");
                if (configAsset != null)
                {
                    // 使用 Unity Asset 时完全脱离外部 data/ 目录；Game 策略和世界观均可选。
                    _memoryPolicy = MemoryPolicyResolver.Resolve(null, _config.memory, null).policy;
                }
                else
                {
                    MemoryPolicy gameMemoryPolicy = DevConfigStore.LoadMemoryPolicy(resolvedGameId);
                    _memoryPolicy = MemoryPolicyResolver.Resolve(gameMemoryPolicy, _config.memory, null).policy;
                }
                if (worldConfigAsset != null)
                    _world = worldConfigAsset.ToDto();
                else if (configAsset == null)
                    _world = DevConfigStore.LoadWorld(resolvedGameId, _config.worldId);
                else
                    _world = new WorldConfigDto { worldId = _config.worldId };
            }
            else
            {
                _memoryPolicy = MemoryPolicy.Defaults();
                _world = null;
            }
            if (IsServerMode())
            {
                string serverUrl = string.IsNullOrWhiteSpace(_config.serverBaseUrl)
                    ? "http://127.0.0.1:5000" : _config.serverBaseUrl;
                string resolvedPlayerId = useConnectionProfile ? connectionProfile.playerId : playerId;
                string resolvedSessionId = useConnectionProfile ? connectionProfile.sessionId : sessionId;
                int resolvedTimeoutMs = useConnectionProfile
                    ? connectionProfile.timeoutMs : _config.model.timeoutMs;
                bool enableSimulatedTools = useConnectionProfile
                    ? connectionProfile.enableSimulatedTools
                    : configAsset != null && configAsset.enableServerSimulatedTools;
                // game 模式仅支持 Connection Profile：工具 schema 与执行都在 Unity 侧，
                // 通过 Profile 显式声明后，Server 会把模型工具调用挂起回传给本地工具。
                bool enableGameTools = useConnectionProfile && connectionProfile.enableGameTools;
                _serverGameTools = enableGameTools;
                _serverBackend = new UnityServerBackend(serverUrl, resolvedGameId, resolvedNpcId,
                    resolvedPlayerId, resolvedSessionId, resolvedTimeoutMs,
                    () =>
                    {
                        IGameContext context = GetGameContext();
                        return context == null ? null : context.SnapshotJson;
                    }, enableSimulatedTools,
                    useConnectionProfile ? connectionProfile.serverAuthToken : null,
                    enableGameTools,
                    enableGameTools ? CollectLocalToolDescriptors : (Func<List<ClientToolDescriptor>>)null);
                _backend = null;
                _loop = null;
            }
            else
            {
                _backend = new UnityWebRequestBackend(_config.model);
                _serverBackend = null;
                _loop = new AgentLoop(_backend, UnityLogSink.Instance,
                    backendFactory: settings => new UnityWebRequestBackend(settings));
            }
        }

        private bool IsServerMode()
        {
            return _config != null && string.Equals(_config.runtimeMode, "server",
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>game 模式上传用：把本地已注册、且 NPC 配置启用的工具转成描述列表。</summary>
        private List<ClientToolDescriptor> CollectLocalToolDescriptors()
        {
            var descriptors = new List<ClientToolDescriptor>();
            if (_tools == null || _config == null || _config.enabledToolIds == null) return descriptors;
            foreach (string id in _config.enabledToolIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!_tools.TryGet(id, out IAgentTool tool) || tool == null) continue;
                descriptors.Add(new ClientToolDescriptor
                {
                    Id = tool.Id,
                    Description = tool.Description,
                    ParametersSchema = tool.ParametersSchema
                });
            }
            return descriptors;
        }

        /// <summary>game 模式：在本地真实执行 Server 挂起的工具调用，并触发 onToolExecuted 通知。</summary>
        private async Task<ToolResult> ExecuteLocalToolAsync(ToolCallDto call)
        {
            ToolResult result;
            try
            {
                if (_tools == null)
                    result = ToolResult.Fail("game 模式未注册本地工具");
                else
                    result = await _tools.ExecuteAsync(call?.Function?.Name, call?.Function?.Arguments, gameObject);
            }
            catch (Exception ex)
            {
                UnityLogSink.Instance.Log(LogLevel.Error, "工具执行异常: " + ex.Message, ex);
                result = ToolResult.Fail("tool error: " + ex.Message);
            }
            onToolExecuted?.Invoke(new AgentToolExecutionEvent
            {
                toolName = call?.Function?.Name,
                argumentsJson = call?.Function?.Arguments ?? "{}",
                success = result != null && result.Success,
                result = result?.MessageForModel
            });
            return result;
        }

        /// <summary>
        /// 重新读取配置并重建后端。适合开发期热切换 NPC、运行模式或模型配置。
        /// 对话进行中不会强制打断，调用方可在下一轮重试。
        /// </summary>
        public bool ReloadConfig()
        {
            if (_running)
            {
                Debug.LogWarning("[AIBot] 对话进行中，暂不能重载配置");
                return false;
            }

            string previousGameId = _loadedGameId;
            string previousNpcId = _loadedNpcId;
            _config = null;
            _world = null;
            _backend = null;
            _serverBackend = null;
            _loop = null;
            _capabilityWarningIssued = false;
            LoadConfig();

            if (_config == null) return false;
            int turns = _memoryPolicy == null ? 12 : _memoryPolicy.shortTermTurns;
            if (_memory == null) _memory = new ShortTermMemory(Math.Max(2, turns * 2));
            else _memory.Resize(Math.Max(2, turns * 2));

            // 切换到另一个 NPC 或世界时，不能沿用旧角色的会话记忆。
            if (!string.Equals(previousNpcId, _loadedNpcId, StringComparison.Ordinal)
                || !string.Equals(previousGameId, _loadedGameId, StringComparison.Ordinal))
            {
                _memory.Clear();
                _sessionSummary = null;
                _sessionFacts = null;
            }
            return true;
        }

        private void ValidateRuntimeCapabilities()
        {
            if (_capabilityWarningIssued || _config == null) return;

            bool serverMode = IsServerMode();
            if (serverMode)
            {
                if (_tools != null && _tools.Count > 0 && !_serverGameTools)
                {
                    Debug.LogWarning("[AIBot] 当前为 Server 模式，NpcAgent 上注册的 Unity 本地工具不会由 Server 执行；" +
                        "请在 Connection Profile 勾选 Enable Game Tools（game 模式回传本地执行），或将工具注册到 AIBot.Server。");
                    _capabilityWarningIssued = true;
                    return;
                }
                if (_serverGameTools && (_tools == null || _tools.Count == 0))
                {
                    Debug.LogWarning("[AIBot] 已启用 game 工具回传，但 NpcAgent 尚未注册任何本地工具；" +
                        "模型请求工具时对话将以失败结束。");
                    _capabilityWarningIssued = true;
                }
                return;
            }

            if (_config.enabledToolIds == null || _config.enabledToolIds.Count == 0) return;
            if (_tools == null)
            {
                Debug.LogWarning("[AIBot] Local 配置启用了工具，但 NpcAgent 尚未注册任何本地工具；这些工具调用将无法执行。");
                _capabilityWarningIssued = true;
                return;
            }

            foreach (string id in _config.enabledToolIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                IAgentTool tool;
                if (!_tools.TryGet(id, out tool))
                {
                    Debug.LogWarning("[AIBot] Local 配置启用了未注册的工具：" + id);
                    _capabilityWarningIssued = true;
                    return;
                }
            }
        }

        private void EnsureGameContext()
        {
            if (gameContextProvider is IGameContext) return;
            if (gameContext == null) gameContext = gameObject.AddComponent<GameContextRelay>();
        }

        private IGameContext GetGameContext()
        {
            if (gameContextProvider is IGameContext external) return external;
            EnsureGameContext();
            return gameContext;
        }

        private void EmitError(string message)
        {
            if (onError != null) onError.Invoke(message);
            else Debug.LogError("[AIBot] " + message);
        }

        private sealed class UnityStreamSink : ILlmStreamSink, IReasoningSink, IToolExecutionSink, IReplyReadySink
        {
            private readonly NpcAgent _agent;
            public UnityStreamSink(NpcAgent agent) { _agent = agent; }
            public void OnToken(string delta) { _agent.onToken?.Invoke(delta); }
            public void OnReasoningToken(string delta) { _agent.onReasoning?.Invoke(delta); }
            public void OnToolCall(ToolCallDto call) { }
            public void OnToolExecuted(ToolExecution execution)
            {
                if (execution == null) return;
                _agent.onToolExecuted?.Invoke(new AgentToolExecutionEvent
                {
                    toolName = execution.Call?.Function?.Name,
                    argumentsJson = execution.Call?.Function?.Arguments ?? "{}",
                    success = execution.Result != null && execution.Result.Success,
                    result = execution.Result?.MessageForModel
                });
            }
            public void OnCompleted(string fullText, Usage usage) { }
            public void OnReplyReady(StructuredReply reply) { _agent.onReply?.Invoke(reply); }
            public void OnError(Exception ex) { Debug.LogWarning("[AIBot] 流错误：" + ex.Message); }
        }
    }
}

