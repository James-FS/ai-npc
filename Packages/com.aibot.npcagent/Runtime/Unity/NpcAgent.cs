using System;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using AIBot.Core.Output;
using AIBot.Core.Tools;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBot.Unity
{
    /// <summary>
    /// NPC Agent 主组件：挂到角色上，调用 Chat(message) 获得流式回复。
    /// 配置来源二选一：configAsset（SO）或 npcId + gameId（data/ 下的 JSON）。
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

        [Tooltip("留空则从 data/games/{gameId}/npcs/{npcId}.json 加载")]
        public AgentConfigAsset configAsset;

        [Tooltip("仅开发期使用。留空时依次使用配置 JSON 与环境变量 AIBOT_LLM_KEY；正式发布应走服务端中转。")]
        public string apiKeyOverride;

        public GameContextRelay gameContext;

        [Header("事件")]
        public UnityEngine.Events.UnityEvent<string> onToken;
        public UnityEngine.Events.UnityEvent<string> onReasoning;    // 推理模型的思考过程增量（可接 UI/调试）
        public UnityEngine.Events.UnityEvent<StructuredReply> onReply;
        public UnityEngine.Events.UnityEvent<string> onError;

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
            LoadConfig();
            _memoryPolicy = _memoryPolicy ?? MemoryPolicy.Defaults();
            int turns = _memoryPolicy.shortTermTurns;
            _memory = new ShortTermMemory(Math.Max(2, turns * 2));
            if (gameContext == null) gameContext = gameObject.AddComponent<GameContextRelay>();
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

        public async void Chat(string message)
        {
            await ChatAsync(message);
        }

        /// <summary>可等待、可测试的对话入口；Chat(string) 仅作为 UnityEvent 兼容包装。</summary>
        public async Task<AgentLoopResult> ChatAsync(string message)
        {
            if (_running) { Debug.LogWarning("[AIBot] 上一轮对话未结束，忽略新输入"); return null; }
            if (_config == null) LoadConfig();
            if (_config == null) { EmitError("配置加载失败：" + gameId + "/" + npcId); return null; }

            _running = true;
            _requestCts?.Dispose();
            _requestCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();
            try
            {
                if (IsServerMode())
                {
                    ServerChatResult serverResult = await _serverBackend.ChatAsync(
                        message, new UnityStreamSink(this), _requestCts.Token);
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
                    onReply?.Invoke(directResult.Reply);
                    return directResult;
                }

                var input = new AgentRunInput
                {
                    Config = _config,
                    World = _world,
                    Game = gameContext,
                    UserMessage = message,
                    Memory = _memory,
                    Tools = _tools,
                    HostContext = gameObject,
                    MemorySummary = _sessionSummary,
                    MemoryFacts = _sessionFacts,
                    ResolvedMemoryPolicy = _memoryPolicy
                };
                AgentLoopResult result = await _loop.RunAsync(input, new UnityStreamSink(this), _requestCts.Token);
                if (result.Reply != null) onReply?.Invoke(result.Reply);
                // 摘要式长期记忆写回（下一次对话注入）
                if (result.MemorySummary != null)
                {
                    _sessionSummary = result.MemorySummary;
                    _sessionFacts = result.MemoryFacts;
                }
                return result;
            }
            catch (OperationCanceledException) { return null; /* 玩家离开/组件禁用：静默 */ }
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
            _config = configAsset != null ? configAsset.ToDto() : DevConfigStore.LoadNpc(gameId, npcId);
            if (_config == null) return;
            if (_config.model == null) _config.model = new ModelSettings();
            if (!string.IsNullOrEmpty(apiKeyOverride)) _config.model.apiKey = apiKeyOverride;
            if (string.IsNullOrEmpty(_config.model.apiKey))
                _config.model.apiKey = Environment.GetEnvironmentVariable("AIBOT_LLM_KEY");
            MemoryPolicy gameMemoryPolicy = DevConfigStore.LoadMemoryPolicy(gameId);
            _memoryPolicy = MemoryPolicyResolver.Resolve(gameMemoryPolicy, _config.memory, null).policy;
            _world = DevConfigStore.LoadWorld(gameId, _config.worldId);
            if (IsServerMode())
            {
                string serverUrl = string.IsNullOrWhiteSpace(_config.serverBaseUrl)
                    ? "http://127.0.0.1:5000" : _config.serverBaseUrl;
                _serverBackend = new UnityServerBackend(serverUrl, gameId, npcId,
                    playerId, sessionId, _config.model.timeoutMs);
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

        private void EmitError(string message)
        {
            if (onError != null) onError.Invoke(message);
            else Debug.LogError("[AIBot] " + message);
        }

        private sealed class UnityStreamSink : ILlmStreamSink, IReasoningSink
        {
            private readonly NpcAgent _agent;
            public UnityStreamSink(NpcAgent agent) { _agent = agent; }
            public void OnToken(string delta) { _agent.onToken?.Invoke(delta); }
            public void OnReasoningToken(string delta) { _agent.onReasoning?.Invoke(delta); }
            public void OnToolCall(ToolCallDto call) { }
            public void OnCompleted(string fullText, Usage usage) { }
            public void OnError(Exception ex) { Debug.LogWarning("[AIBot] 流错误：" + ex.Message); }
        }
    }
}
