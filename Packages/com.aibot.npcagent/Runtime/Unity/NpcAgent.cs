using System;
using System.Threading;
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

        [Tooltip("留空则从 data/games/{gameId}/npcs/{npcId}.json 加载")]
        public AgentConfigAsset configAsset;

        public GameContextRelay gameContext;

        [Header("事件")]
        public UnityEngine.Events.UnityEvent<string> onToken;
        public UnityEngine.Events.UnityEvent<string> onReasoning;    // 推理模型的思考过程增量（可接 UI/调试）
        public UnityEngine.Events.UnityEvent<StructuredReply> onReply;
        public UnityEngine.Events.UnityEvent<string> onError;

        private AgentConfigDto _config;
        private WorldConfigDto _world;
        private UnityWebRequestBackend _backend;
        private AgentLoop _loop;
        private ShortTermMemory _memory;
        private ToolRegistry _tools;
        private CancellationTokenSource _cts;
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
            _memory = new ShortTermMemory(_config != null ? _config.memory.shortTermTurns : 12);
            if (gameContext == null) gameContext = gameObject.AddComponent<GameContextRelay>();
        }

        private void OnEnable() { _cts = new CancellationTokenSource(); }
        private void OnDisable() { CancelRunning(); }

        public void CancelRunning()
        {
            if (_cts != null) _cts.Cancel();
            _running = false;
        }

        public async void Chat(string message)
        {
            if (_running) { Debug.LogWarning("[AIBot] 上一轮对话未结束，忽略新输入"); return; }
            if (_config == null) LoadConfig();
            if (_config == null) { EmitError("配置加载失败：" + gameId + "/" + npcId); return; }

            _running = true;
            try
            {
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
                    MemoryFacts = _sessionFacts
                };
                AgentLoopResult result = await _loop.RunAsync(input, new UnityStreamSink(this), _cts.Token);
                if (result.Reply != null) onReply.Invoke(result.Reply);
                // 摘要式长期记忆写回（下一次对话注入）
                if (result.MemorySummary != null)
                {
                    _sessionSummary = result.MemorySummary;
                    _sessionFacts = result.MemoryFacts;
                }
            }
            catch (OperationCanceledException) { /* 玩家离开/组件禁用：静默 */ }
            catch (Exception ex)
            {
                UnityLogSink.Instance.Log(LogLevel.Error, "Chat failed: " + ex.Message, ex);
                EmitError(ex.Message);
            }
            finally { _running = false; }
        }

        private void LoadConfig()
        {
            _config = configAsset != null ? configAsset.ToDto() : DevConfigStore.LoadNpc(gameId, npcId);
            if (_config == null) return;
            _world = DevConfigStore.LoadWorld(gameId, _config.worldId);
            _backend = new UnityWebRequestBackend(_config.model);
            _loop = new AgentLoop(_backend, UnityLogSink.Instance);
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
            public void OnToken(string delta) { _agent.onToken.Invoke(delta); }
            public void OnReasoningToken(string delta) { _agent.onReasoning.Invoke(delta); }
            public void OnToolCall(ToolCallDto call) { }
            public void OnCompleted(string fullText, Usage usage) { }
            public void OnError(Exception ex) { Debug.LogWarning("[AIBot] 流错误：" + ex.Message); }
        }
    }
}
