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
        public string RawText;                   // 模型最终原文（解析失败时的排查依据）
        public string MemorySummary;             // 本轮触发了记忆摘要时非空：宿主写回会话状态
        public List<string> MemoryFacts;
        public bool FlaggedInjection;            // 玩家输入命中注入检测（供日志/统计）
    }

    public sealed class AgentLoopOptions
    {
        public int MaxToolRounds = 4;
        public int TokenBudget = Context.TokenBudget.DefaultBudget;
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

        public AgentLoop(ILlmBackend backend, ILogSink log = null, IClock clock = null, AgentLoopOptions options = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _log = log ?? NullLogSink.Instance;
            _clock = clock ?? new SystemClock();
            _options = options ?? new AgentLoopOptions();
        }

        public async Task<AgentLoopResult> RunAsync(AgentRunInput input, ILlmStreamSink sink, CancellationToken ct)
        {
            long started = _clock.TimestampMilliseconds;
            AgentConfigDto cfg = input.Config;

            try
            {
                AgentLoopResult result = await RunInternalAsync(input, sink, cfg, ct, started);
                await MaybeSummarizeAsync(input, result);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Error, "AgentLoop failed, using fallback. npc=" + cfg.npcId, ex);
                return BuildFallback(cfg, started);
            }
        }

        private async Task<AgentLoopResult> RunInternalAsync(AgentRunInput input, ILlmStreamSink sink,
            AgentConfigDto cfg, CancellationToken ct, long started)
        {
            SanitizeResult sanitized = InputSanitizer.Sanitize(input.UserMessage);
            if (sanitized.Flagged) _log.Log(LogLevel.Warning, "Input flagged as possible injection. npc=" + cfg.npcId);

            string systemPrompt = new ContextBuilder().BuildSystemPrompt(
                cfg, input.World, input.Game, input.MemorySummary, input.MemoryFacts);

            var messages = new List<LlmMessage> { LlmMessage.System(systemPrompt) };
            if (input.Memory != null) messages.AddRange(TrimmedHistory(input.Memory.Messages, systemPrompt));
            LlmMessage userMessage = LlmMessage.User(sanitized.Wrapped);
            messages.Add(userMessage);

            List<ToolSchema> schemas = input.Tools != null
                ? input.Tools.BuildSchemas(cfg.enabledToolIds)
                : new List<ToolSchema>();

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
                    ResponseFormat = schemas.Count == 0 ? ResponseFormat.JsonObject() : null
                };

                var collector = new RoundCollector(sink);
                await _backend.ChatStreamAsync(request, collector, ct);
                if (collector.LastError != null) throw collector.LastError;

                totalUsage.PromptTokens += collector.Usage?.PromptTokens ?? 0;
                totalUsage.CompletionTokens += collector.Usage?.CompletionTokens ?? 0;

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
                            Content = result.MessageForModel
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
                fallback.FlaggedInjection = sanitized.Flagged;
                Remember(input, userMessage, finalText);
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
        private async Task MaybeSummarizeAsync(AgentRunInput input, AgentLoopResult result)
        {
            ShortTermMemory memory = input.Memory;
            AgentConfigDto cfg = input.Config;
            if (memory == null || cfg.memory == null || cfg.memory.summaryThreshold <= 0) return;
            if (memory.EvictedCount < cfg.memory.summaryThreshold) return;

            List<LlmMessage> evicted = memory.TakeEvicted();
            if (evicted.Count == 0) return;
            try
            {
                MemorySummaryResult summary = await MemorySummarizer.RunAsync(
                    _backend, cfg, input.MemorySummary, input.MemoryFacts, evicted, _log, CancellationToken.None);
                result.MemorySummary = summary.Summary;
                result.MemoryFacts = summary.Facts;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "Memory summarize failed (dropped this batch): " + ex.Message);
            }
        }

        private static IEnumerable<LlmMessage> TrimmedHistory(IReadOnlyList<LlmMessage> history, string systemPrompt)
        {
            // 按预算从最旧裁剪；轮内 user/assistant/tool 组可能被切半，M1 接受（M2 摘要上线后缓解）
            var kept = new List<LlmMessage>(history);
            int used = TokenBudget.Estimate(systemPrompt);
            foreach (LlmMessage m in kept) used += TokenBudget.Estimate(m.Content ?? string.Empty);
            int drop = 0;
            while (used > TokenBudget.DefaultBudget && drop < kept.Count)
            {
                used -= TokenBudget.Estimate(kept[drop].Content ?? string.Empty);
                drop++;
            }
            return kept.GetRange(drop, kept.Count - drop);
        }

        private static void Remember(AgentRunInput input, LlmMessage userMessage, string finalText)
        {
            if (input.Memory == null) return;
            input.Memory.Add(userMessage);
            input.Memory.Add(LlmMessage.Assistant(finalText));
        }

        private AgentLoopResult BuildFallback(AgentConfigDto cfg, long started)
        {
            string say = cfg.fallbackReplies != null && cfg.fallbackReplies.Count > 0
                ? cfg.fallbackReplies[0]
                : "（沉默片刻）……";
            return new AgentLoopResult
            {
                Reply = new StructuredReply { say = say, emotion = "neutral", action = "idle" },
                ElapsedMs = _clock.TimestampMilliseconds - started,
                UsedFallback = true
            };
        }

        /// <summary>单轮收集器：token 实时透传外层 sink；工具调用与全文在轮末可用；思考过程按需转发。</summary>
        private sealed class RoundCollector : ILlmStreamSink, IReasoningSink
        {
            private readonly ILlmStreamSink _outer;
            public readonly List<ToolCallDto> ToolCalls = new List<ToolCallDto>();
            public string Text;
            public Usage Usage;
            public Exception LastError;

            public RoundCollector(ILlmStreamSink outer) { _outer = outer; }

            public void OnToken(string delta) { if (_outer != null) _outer.OnToken(delta); }
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
