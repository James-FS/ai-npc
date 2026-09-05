using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Guard;
using AIBot.Core.Logging;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using AIBot.Core.Output;
using AIBot.Core.Tools;

namespace AIBot.Core
{
    /// <summary>一轮对话的全部输入。</summary>
    public sealed class AgentRunInput
    {
        public AgentConfigDto Config;
        public WorldConfigDto World;
        public IGameContext Game;
        public string UserMessage;
        public ShortTermMemory Memory;           // 可空（Server 无状态预览时）
        public ToolRegistry Tools;               // 可空（纯聊天 NPC）
        public object HostContext;               // 透传给工具执行（游戏对象/模拟状态）
        public string MemorySummary;             // M2 摘要注入
        public List<string> MemoryFacts;
        public MemoryPolicy ResolvedMemoryPolicy; // 宿主完成四级配置合并后传入；为空时使用 Core+NPC 兼容解析
        public bool DeferMemorySummarizationToHost; // Server 玩家后台队列为 true；Unity/Session 同步路径为 false
        public bool NotifyReplyBeforeSummary; // Unity UI 可在摘要 LLM 调用前先收到最终台词
        public bool DeferToolsToHost;            // game 模式：工具调用不执行，挂起后交回宿主（如 Unity 本地工具）
        public List<ToolSchema> DeferredTools;   // DeferToolsToHost 时作为工具 schema 来源（由客户端上传，Server 已过滤）
        public List<LlmMessage> ResumeMessages;  // 非空时从该消息列表续跑（挂起-恢复协议第二段）；旧 system 会在入口被重建
    }

    public sealed class ToolExecution
    {
        public ToolCallDto Call;
        public ToolResult Result;
    }

    /// <summary>可选回调：宿主需要实时感知工具执行时实现（Server 下发 SSE tool_call 事件用）。</summary>
    public interface IToolExecutionSink
    {
        void OnToolExecuted(ToolExecution execution);
    }

    public sealed class AgentLoopResult
    {
        public StructuredReply Reply;
        public List<ToolExecution> ToolExecutions = new List<ToolExecution>();
        public Usage Usage = new Usage();
        public long ElapsedMs;
        public bool UsedFallback;
        /// <summary>当 UsedFallback=true 时提供给宿主的可诊断原因；不包含敏感请求内容。</summary>
        public string FallbackReason;
        public string RawText;                   // 模型最终原文（解析失败时的排查依据）
        public string MemorySummary;             // 本轮触发了记忆摘要时非空：宿主写回会话状态
        public List<string> MemoryFacts;
        public List<LlmMessage> MemorySummarizedMessages; // 宿主保存失败时用于恢复待摘要批次
        public bool FlaggedInjection;            // 玩家输入命中注入检测（供日志/统计）
        public List<ToolCallDto> PendingToolCalls; // DeferToolsToHost 下非空：等待宿主执行的工具调用，本轮无 Reply
        public List<LlmMessage> PendingMessages;   // 与 PendingToolCalls 配套：宿主持久化后用于续跑的完整消息列表
    }

    /// <summary>宿主可选实现：在记忆摘要前收到最终回复，避免 UI 等待第二次 LLM 调用。</summary>
    public interface IReplyReadySink
    {
        void OnReplyReady(StructuredReply reply);
    }

    public sealed class AgentLoopOptions
    {
        public int MaxToolRounds = 4;
        public int TokenBudget = Context.TokenBudget.DefaultBudget;
        public int MaxUserMessageChars = 8000;
        public int MaxToolResultChars = 12000;
    }

    /// <summary>
    /// 会话主循环：组装 → 请求 → 工具循环（≤MaxToolRounds，耗尽后强制纯文本收敛）→ 结构化解析 → 兜底。
    /// </summary>
    public sealed class AgentLoop
    {
        private readonly ILlmBackend _backend;
        private readonly ILogSink _log;
        private readonly IClock _clock;
        private readonly AgentLoopOptions _options;
        private readonly Func<ModelSettings, ILlmBackend> _backendFactory;

        public AgentLoop(ILlmBackend backend, ILogSink log = null, IClock clock = null,
            AgentLoopOptions options = null, Func<ModelSettings, ILlmBackend> backendFactory = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _log = log ?? NullLogSink.Instance;
            _clock = clock ?? new SystemClock();
            _options = options ?? new AgentLoopOptions();
            _backendFactory = backendFactory;
        }

        public async Task<AgentLoopResult> RunAsync(AgentRunInput input, ILlmStreamSink sink, CancellationToken ct)
        {
            long started = _clock.TimestampMilliseconds;
            if (input == null) throw new ArgumentNullException(nameof(input));
            AgentConfigDto cfg = input.Config;
            if (cfg == null) throw new ArgumentException("Agent config is required", nameof(input));
            cfg.model = cfg.model ?? new ModelSettings();
            cfg.memory = cfg.memory ?? new MemorySettings();
            cfg.output = cfg.output ?? new OutputSettings();
            input.ResolvedMemoryPolicy = input.ResolvedMemoryPolicy
                ?? MemoryPolicyResolver.Resolve(null, cfg.memory, null).policy;
            string boundedMessage = Limit(input.UserMessage, _options.MaxUserMessageChars);
            SanitizeResult sanitized = InputSanitizer.Sanitize(boundedMessage);

            try
            {
                AgentLoopResult result = await RunInternalAsync(input, sink, cfg, sanitized, ct, started);
                if (result.PendingToolCalls != null) return result; // 挂起轮：无记忆变化，无需摘要
                if (input.NotifyReplyBeforeSummary)
                {
                    var ready = sink as IReplyReadySink;
                    if (ready != null && result.Reply != null) ready.OnReplyReady(result.Reply);
                }
                await MaybeSummarizeAsync(input, result, ct);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Error, "AgentLoop failed, using fallback. npc=" + cfg.npcId, ex);
                AgentLoopResult fallback = BuildFallback(cfg, started);
                // 只向游戏层暴露稳定诊断码，避免把上游响应正文或其他敏感信息带进 UI。
                fallback.FallbackReason = ex is LlmFallbackException
                    ? "model_request_failed" : "agent_failed";
                fallback.FlaggedInjection = sanitized.Flagged;
                Remember(input, ResolveEntryUserMessage(input, sanitized), SerializeReply(fallback.Reply));
                if (input.NotifyReplyBeforeSummary)
                {
                    var ready = sink as IReplyReadySink;
                    if (ready != null) ready.OnReplyReady(fallback.Reply);
                }
                return fallback;
            }
        }

        private async Task<AgentLoopResult> RunInternalAsync(AgentRunInput input, ILlmStreamSink sink,
            AgentConfigDto cfg, SanitizeResult sanitized, CancellationToken ct, long started)
        {
            if (sanitized.Flagged) _log.Log(LogLevel.Warning, "Input flagged as possible injection. npc=" + cfg.npcId);

            bool resuming = input.ResumeMessages != null && input.ResumeMessages.Count > 0;
            List<LlmMessage> messages;
            LlmMessage userMessage;
            if (resuming)
            {
                // 挂起-恢复第二段：历史、user 与 assistant(tool_calls) 都在挂起态里；
                // 入口重建 system，让续跑第一轮拿到工具执行后的最新游戏快照。
                messages = new List<LlmMessage>(input.ResumeMessages);
                userMessage = ResolveEntryUserMessage(input, sanitized);
                if (messages.Count > 0 && string.Equals(messages[0].Role, "system", StringComparison.Ordinal))
                {
                    messages[0] = LlmMessage.System(new ContextBuilder().BuildSystemPrompt(
                        cfg, input.World, input.Game, input.MemorySummary, input.MemoryFacts));
                }
            }
            else
            {
                string systemPrompt = new ContextBuilder().BuildSystemPrompt(
                    cfg, input.World, input.Game, input.MemorySummary, input.MemoryFacts);

                messages = new List<LlmMessage> { LlmMessage.System(systemPrompt) };
                if (input.Memory != null) messages.AddRange(TrimmedHistory(
                    input.Memory.Messages, systemPrompt, cfg.npcId));
                userMessage = LlmMessage.User(sanitized.Wrapped);
                messages.Add(userMessage);
            }

            List<ToolSchema> schemas = input.DeferredTools
                ?? (input.Tools != null ? input.Tools.BuildSchemas(cfg.enabledToolIds) : new List<ToolSchema>());

            var executions = new List<ToolExecution>();
            var totalUsage = new Usage();
            string finalText = null;
            int rounds = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool forceText = rounds >= _options.MaxToolRounds;

                if (rounds > 0)
                {
                    // 工具可能改了游戏状态：重建 system，让下一轮"当前状况"反映最新快照
                    messages[0] = LlmMessage.System(new ContextBuilder().BuildSystemPrompt(
                        cfg, input.World, input.Game, input.MemorySummary, input.MemoryFacts));
                }

                var request = new LlmRequest
                {
                    Model = cfg.model.model,
                    Messages = messages,
                    Tools = forceText ? null : (schemas.Count > 0 ? schemas : null),
                    Temperature = cfg.model.temperature,
                    MaxTokens = cfg.model.maxTokens,
                    ResponseFormat = (forceText || schemas.Count == 0) ? ResponseFormat.JsonObject() : null
                };

                var collector = new RoundCollector(sink);
                await _backend.ChatStreamAsync(request, collector, ct);
                if (collector.LastError != null) throw collector.LastError;

                totalUsage.PromptTokens += collector.Usage?.PromptTokens ?? 0;
                totalUsage.CompletionTokens += collector.Usage?.CompletionTokens ?? 0;
                totalUsage.TotalTokens = totalUsage.PromptTokens + totalUsage.CompletionTokens;

                // 用真实 usage 校准该 NPC 的 token 估算系数（裁剪决策越来越准）
                int estimatedRound = 0;
                foreach (LlmMessage m in messages)
                {
                    estimatedRound += TokenBudget.Estimate(m.Content ?? string.Empty);
                }
                if (collector.Usage != null)
                {
                    TokenBudget.Calibration.Update(cfg.npcId, collector.Usage.PromptTokens, estimatedRound);
                }

                if (collector.ToolCalls.Count > 0 && !forceText)
                {
                    messages.Add(new LlmMessage
                    {
                        Role = "assistant",
                        Content = collector.Text ?? string.Empty,
                        ToolCalls = collector.ToolCalls
                    });
                    if (input.DeferToolsToHost)
                    {
                        // game 模式：工具由宿主（Unity）执行。挂起当前消息列表，
                        // 宿主持久化后携带工具结果走 ResumeMessages 续跑；此时不写记忆、不触发摘要。
                        return new AgentLoopResult
                        {
                            PendingToolCalls = collector.ToolCalls,
                            PendingMessages = messages,
                            Usage = totalUsage,
                            ElapsedMs = _clock.TimestampMilliseconds - started
                        };
                    }
                    foreach (ToolCallDto call in collector.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        ToolResult result = input.Tools != null
                            ? await input.Tools.ExecuteAsync(call.Function.Name, call.Function.Arguments, input.HostContext)
                            : ToolResult.Fail("no tool registry");
                        executions.Add(new ToolExecution { Call = call, Result = result });
                        var toolSink = sink as IToolExecutionSink;
                        if (toolSink != null) toolSink.OnToolExecuted(executions[executions.Count - 1]);
                        messages.Add(new LlmMessage
                        {
                            Role = "tool",
                            ToolCallId = call.Id,
                            Content = Limit(result == null ? null : result.MessageForModel,
                                _options.MaxToolResultChars)
                        });
                    }
                    rounds++;
                    continue;
                }

                finalText = collector.Text ?? string.Empty;
                break;
            }

            StructuredReply reply;
            if (!StructuredReplyParser.TryParse(finalText, cfg.output, out reply))
            {
                _log.Log(LogLevel.Warning, "Structured reply parse failed, using fallback. npc=" + cfg.npcId + " raw=" + finalText);
                AgentLoopResult fallback = BuildFallback(cfg, started);
                fallback.ToolExecutions = executions;
                fallback.Usage = totalUsage;
                fallback.RawText = finalText;
                fallback.FallbackReason = "structured_reply_invalid";
                fallback.FlaggedInjection = sanitized.Flagged;
                Remember(input, userMessage, SerializeReply(fallback.Reply));
                return fallback;
            }

            if (input.Memory != null)
            {
                input.Memory.Add(userMessage);
                input.Memory.Add(LlmMessage.Assistant(finalText));
            }

            return new AgentLoopResult
            {
                Reply = reply,
                ToolExecutions = executions,
                Usage = totalUsage,
                ElapsedMs = _clock.TimestampMilliseconds - started,
                UsedFallback = false,
                RawText = finalText,
                FlaggedInjection = sanitized.Flagged
            };
        }

        /// <summary>淘汰消息达到阈值时，压缩为「摘要+关键事实」（失败仅告警，不影响主流程）。</summary>
        private async Task MaybeSummarizeAsync(AgentRunInput input, AgentLoopResult result, CancellationToken ct)
        {
            ShortTermMemory memory = input.Memory;
            AgentConfigDto cfg = input.Config;
            MemoryPolicy policy = input.ResolvedMemoryPolicy ?? MemoryPolicy.Defaults();
            if (memory == null) return;
            if (policy.summaryThreshold <= 0)
            {
                // 0 表示关闭自动摘要。Session 没有人工入口，直接丢弃；玩家范围保留最近一个窗口供人工摘要。
                if (policy.memoryScope == MemoryPolicyValues.ScopeSession) memory.TakeEvicted();
                else memory.TrimEvictedToLast(Math.Max(2, policy.shortTermTurns * 2));
                return;
            }
            if (policy.backgroundSummarization && input.DeferMemorySummarizationToHost) return;
            if (memory.EvictedCount < policy.summaryThreshold) return;

            List<LlmMessage> evicted = memory.TakeEvicted();
            if (evicted.Count == 0) return;
            try
            {
                ModelSettings summarySettings = policy.summaryModel ?? cfg.model;
                ILlmBackend summaryBackend = _backendFactory != null
                    ? _backendFactory(summarySettings)
                    : _backend;
                MemorySummaryResult summary = await MemorySummarizer.RunAsync(
                    summaryBackend, summarySettings, input.MemorySummary, input.MemoryFacts, evicted,
                    policy.maxFacts, _log, ct, policy);
                result.MemorySummary = summary.Summary;
                result.MemoryFacts = summary.Facts;
                result.MemorySummarizedMessages = evicted;
            }
            catch (OperationCanceledException)
            {
                memory.RestoreEvicted(evicted);
                throw;
            }
            catch (Exception ex)
            {
                memory.RestoreEvicted(evicted);
                _log.Log(LogLevel.Warning, "Memory summarize failed (batch restored): " + ex.Message);
            }
        }

        private IEnumerable<LlmMessage> TrimmedHistory(IReadOnlyList<LlmMessage> history,
            string systemPrompt, string npcId)
        {
            var kept = new List<LlmMessage>(history);
            int used = TokenBudget.Calibration.EstimateCalibrated(npcId, systemPrompt);
            foreach (LlmMessage m in kept)
                used += TokenBudget.Calibration.EstimateCalibrated(npcId, m.Content ?? string.Empty);
            int drop = 0;
            while (used > _options.TokenBudget && drop < kept.Count)
            {
                used -= TokenBudget.Calibration.EstimateCalibrated(npcId, kept[drop].Content ?? string.Empty);
                drop++;
            }
            // 不让历史从 assistant/tool 半轮开始；继续丢到下一个 user 边界。
            while (drop < kept.Count && kept[drop].Role != "user") drop++;
            return kept.GetRange(drop, kept.Count - drop);
        }

        private static void Remember(AgentRunInput input, LlmMessage userMessage, string finalText)
        {
            if (input.Memory == null) return;
            input.Memory.Add(userMessage);
            input.Memory.Add(LlmMessage.Assistant(finalText));
        }

        /// <summary>入账用的本轮 user 消息：resume 时取挂起态里的原始 user，避免把空消息写进记忆。</summary>
        private static LlmMessage ResolveEntryUserMessage(AgentRunInput input, SanitizeResult sanitized)
        {
            if (input.ResumeMessages != null)
            {
                for (int i = input.ResumeMessages.Count - 1; i >= 0; i--)
                {
                    LlmMessage candidate = input.ResumeMessages[i];
                    if (candidate != null && string.Equals(candidate.Role, "user", StringComparison.Ordinal))
                        return candidate;
                }
            }
            return LlmMessage.User(sanitized.Wrapped);
        }

        private AgentLoopResult BuildFallback(AgentConfigDto cfg, long started)
        {
            string say = "（沉默片刻）……";
            if (cfg.fallbackReplies != null && cfg.fallbackReplies.Count > 0)
            {
                int index = (int)((ulong)_clock.TimestampMilliseconds % (ulong)cfg.fallbackReplies.Count);
                say = cfg.fallbackReplies[index];
            }
            return new AgentLoopResult
            {
                Reply = new StructuredReply { say = say, emotion = "neutral", action = "idle" },
                ElapsedMs = _clock.TimestampMilliseconds - started,
                UsedFallback = true
            };
        }

        private static string SerializeReply(StructuredReply reply)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(reply);
        }

        private static string Limit(string value, int maxChars)
        {
            value = value ?? string.Empty;
            if (maxChars <= 0 || value.Length <= maxChars) return value;
            return value.Substring(0, maxChars) + "…[已截断]";
        }

        /// <summary>单轮收集器：token 实时透传外层 sink；工具调用与全文在轮末可用；思考过程按需转发。</summary>
        private sealed class RoundCollector : ILlmStreamSink, IReasoningSink
        {
            private readonly ILlmStreamSink _outer;
            public readonly List<ToolCallDto> ToolCalls = new List<ToolCallDto>();
            public string Text;
            public Usage Usage;
            public Exception LastError;
            private readonly StructuredReplyStreamExtractor _speech = new StructuredReplyStreamExtractor();

            public RoundCollector(ILlmStreamSink outer) { _outer = outer; }

            public void OnToken(string delta)
            {
                string speechDelta = _speech.Push(delta);
                if (_outer != null && !string.IsNullOrEmpty(speechDelta)) _outer.OnToken(speechDelta);
            }
            public void OnToolCall(ToolCallDto call) { ToolCalls.Add(call); }
            public void OnCompleted(string fullText, Usage usage) { Text = fullText; Usage = usage; }
            public void OnError(Exception ex) { LastError = ex; if (_outer != null) _outer.OnError(ex); }
            public void OnReasoningToken(string delta)
            {
                var rs = _outer as IReasoningSink;
                if (rs != null) rs.OnReasoningToken(delta);
            }
        }
    }
}