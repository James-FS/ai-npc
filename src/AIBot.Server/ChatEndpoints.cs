using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Llm;
using AIBot.Core.Logging;
using AIBot.Core.Memory;
using AIBot.Core.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>对话请求体（对应主方案 §7.1 契约）。</summary>
    public class ChatRequestBody
    {
        public string NpcId { get; set; }
        public string PlayerId { get; set; }
        public string SessionId { get; set; } = "s-local";
        public string RequestId { get; set; }
        public string Message { get; set; }
        public SimGameState SimState { get; set; }
        public OverrideSettings Override { get; set; }
        public MemoryPolicyOverrides MemoryOverride { get; set; } // 管理端调试临时覆盖，Server 最终仍应用安全上限
        public string ToolMode { get; set; } = ServerToolModes.None; // none（正式默认）/ simulated（仅调试）
    }

    public class OverrideSettings
    {
        public string Model { get; set; }
        public float? Temperature { get; set; }
    }

    /// <summary>对话端点：SSE 下行事件遵循主方案附录B；会话经 SessionStore，结果记日志与统计。</summary>
    public static class ChatEndpoints
    {
        public static void MapAIBotChat(this WebApplication app)
        {
            IConfiguration config = app.Configuration;
            PlayerMemoryService playerMemories = app.Services.GetRequiredService<PlayerMemoryService>();
            MemorySummaryQueue summaryQueue = app.Services.GetRequiredService<MemorySummaryQueue>();
            RuntimeLogService runtimeLogs = app.Services.GetRequiredService<RuntimeLogService>();

            app.MapGet("/api/games/{gid}/npcs", (string gid) =>
                DataStore.IsValidId(gid)
                    ? Results.Json(new { gameId = gid, npcs = DataStore.ListNpcIds(gid) })
                    : Results.BadRequest("非法 gameId"));

            app.MapPost("/api/games/{gid}/chat/stream", async (string gid, HttpContext http) =>
            {
                if (http.Request.ContentLength.HasValue && http.Request.ContentLength.Value > 64 * 1024)
                {
                    http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await http.Response.WriteAsync("请求体过大");
                    return;
                }
                ChatRequestBody body;
                try { body = await http.Request.ReadFromJsonAsync<ChatRequestBody>(http.RequestAborted); }
                catch { body = null; }
                if (body == null || string.IsNullOrEmpty(body.NpcId) || string.IsNullOrEmpty(body.Message))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("npcId 与 message 必填");
                    return;
                }
                if (!ServerToolModes.TryNormalize(body.ToolMode, out string toolMode))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("toolMode 仅支持 none 或 simulated");
                    return;
                }
                bool useSimulatedTools = toolMode == ServerToolModes.Simulated;
                if (useSimulatedTools && !CanUseAdminOverrides(http, config))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsync("simulated 工具仅允许授权的调试请求使用");
                    return;
                }
                string headerRequestId = http.Request.Headers["X-Request-Id"].ToString();
                if (!string.IsNullOrWhiteSpace(headerRequestId) && !ChatRequestIds.IsValid(headerRequestId))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("X-Request-Id 格式无效");
                    return;
                }
                if (!string.IsNullOrWhiteSpace(headerRequestId)
                    && !string.IsNullOrWhiteSpace(body.RequestId)
                    && !string.Equals(headerRequestId, body.RequestId, StringComparison.Ordinal))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("X-Request-Id 与请求体 requestId 必须一致");
                    return;
                }
                string requestId = !string.IsNullOrWhiteSpace(body.RequestId)
                    ? body.RequestId
                    : !string.IsNullOrWhiteSpace(headerRequestId) ? headerRequestId : http.TraceIdentifier;
                if (!ChatRequestIds.IsValid(requestId))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("requestId 非法（仅允许字母数字及 _ . : -，最长80位）");
                    return;
                }
                body.RequestId = requestId;
                http.TraceIdentifier = requestId;
                http.Response.Headers["X-Request-Id"] = requestId;
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(body.NpcId))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("gameId 或 npcId 非法");
                    return;
                }
                if (!DataStore.IsValidSessionId(body.SessionId))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("sessionId 非法（仅允许字母数字及 _ . : -，最长128位）");
                    return;
                }
                if (!string.IsNullOrEmpty(body.PlayerId) && !DataStore.IsValidPlayerId(body.PlayerId))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("playerId 非法（仅允许字母数字及 _ . : -，最长128位）");
                    return;
                }
                if (body.Message.Length > 4000)
                {
                    http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await http.Response.WriteAsync("message 最长 4000 字符");
                    return;
                }
                if (body.MemoryOverride != null && !CanUseAdminOverrides(http, config))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsync("memoryOverride 仅允许管理端调试请求使用");
                    return;
                }

                AgentConfigDto cfg = DataStore.LoadNpc(gid, body.NpcId);
                if (cfg == null)
                {
                    http.Response.StatusCode = 404;
                    await http.Response.WriteAsync("npc not found: " + body.NpcId + " (game=" + gid + ")");
                    return;
                }
                WorldConfigDto world = DataStore.LoadWorld(gid, cfg.worldId);
                cfg.model = cfg.model ?? new ModelSettings();
                cfg.memory = cfg.memory ?? new MemorySettings();

                // key 优先级：NPC 配置 > 环境变量 AIBOT_LLM_KEY > appsettings
                if (string.IsNullOrEmpty(cfg.model.apiKey))
                {
                    cfg.model.apiKey = Environment.GetEnvironmentVariable("AIBOT_LLM_KEY")
                        ?? config["Llm:ApiKey"];
                }
                if (body.Override != null)
                {
                    if (!string.IsNullOrEmpty(body.Override.Model)) cfg.model.model = body.Override.Model;
                    if (body.Override.Temperature.HasValue) cfg.model.temperature = body.Override.Temperature.Value;
                }

                EffectiveMemoryPolicy effectiveMemory = MemoryPolicyService.Resolve(
                    gid, cfg, body.MemoryOverride, config);

                bool requestedPlayerScope = effectiveMemory.policy.memoryScope == MemoryPolicyValues.ScopePlayerNpc;
                bool playerScoped = requestedPlayerScope && !string.IsNullOrEmpty(body.PlayerId);
                bool legacyMemoryScope = requestedPlayerScope && !playerScoped;
                if (legacyMemoryScope)
                {
                    // 兼容未传 playerId 的旧客户端：仍按 session 同步摘要，不能投递玩家后台任务。
                    effectiveMemory.policy.memoryScope = MemoryPolicyValues.ScopeSession;
                    effectiveMemory.policy.backgroundSummarization = false;
                }

                SessionState session = SessionStore.GetOrCreate(gid, body.NpcId, body.PlayerId, body.SessionId,
                    effectiveMemory.policy.shortTermTurns);
                string fingerprint = BuildRequestFingerprint(body, toolMode);

                try
                {
                    // 请求一旦通过校验就由 Server 承担完成责任；客户端断线只停止响应写入，
                    // 不取消模型、记忆或工具链，重连请求会在 Gate 后读取持久化结果。
                    await session.Gate.WaitAsync(app.Lifetime.ApplicationStopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                ChatRequestRecord requestRecord = null;
                Channel<string> channel = null;
                SseEventWriter sse = null;
                Task pump = null;
                try
                {
                    requestRecord = SessionStore.FindRequest(session, requestId);
                    if (requestRecord != null)
                    {
                        if (!string.Equals(requestRecord.fingerprint, fingerprint, StringComparison.Ordinal))
                        {
                            await ApiErrorWriter.WriteAsync(http, StatusCodes.Status409Conflict,
                                "request_id_conflict", "相同 requestId 不能用于不同的聊天内容");
                            return;
                        }
                        if (requestRecord.status == ChatRequestStatuses.Completed
                            || requestRecord.status == ChatRequestStatuses.Failed)
                        {
                            await ReplayEventsAsync(http, requestRecord);
                            return;
                        }

                        // 当前进程中的并发重复请求会先等待 Gate；能走到这里的 processing
                        // 来自上次异常退出，状态无法证明是否已产生副作用，因此禁止自动重跑。
                        await ApiErrorWriter.WriteAsync(http, StatusCodes.Status409Conflict,
                            "request_in_doubt", "该请求上次执行状态不确定，请查询业务状态后使用新的 requestId");
                        return;
                    }

                    requestRecord = SessionStore.BeginRequest(session, requestId, fingerprint);
                    if (!SessionStore.Save(session))
                    {
                        session.RecentRequests.Remove(requestRecord);
                        await ApiErrorWriter.WriteAsync(http, StatusCodes.Status503ServiceUnavailable,
                            "idempotency_store_unavailable", "无法持久化请求幂等状态，请稍后重试");
                        return;
                    }

                    PlayerLongTermMemory longMemory = null;
                    if (playerScoped)
                    {
                        longMemory = await playerMemories.LoadAndMigrateAsync(session,
                            effectiveMemory.policy.maxFacts, app.Lifetime.ApplicationStopping);
                    }

                    http.Response.ContentType = "text/event-stream; charset=utf-8";
                    http.Response.Headers.CacheControl = "no-cache";

                    channel = Channel.CreateUnbounded<string>();
                    sse = new SseEventWriter(channel.Writer, body.SessionId);
                    pump = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (string line in channel.Reader.ReadAllAsync(http.RequestAborted))
                            {
                                await http.Response.WriteAsync(line, http.RequestAborted);
                                await http.Response.Body.FlushAsync(http.RequestAborted);
                            }
                        }
                        catch (OperationCanceledException) { }
                    });

                // 模拟状态合并进会话：测试台滑条覆盖 stage/favorability，extras 逐键覆盖
                if (body.SimState != null)
                {
                    session.SimState.stage = body.SimState.stage;
                    session.SimState.favorability = body.SimState.favorability;
                    if (body.SimState.extras != null)
                    {
                        if (session.SimState.extras == null)
                            session.SimState.extras = new Dictionary<string, string>();
                        foreach (var kv in body.SimState.extras) session.SimState.extras[kv.Key] = kv.Value;
                    }
                }

                // 正式聊天默认不注册模拟工具，避免把会话状态变化误当作真实游戏业务写入。
                // 调试台显式请求 simulated 后才启用 give_item/change_favor/advance_stage。
                var toolRegistry = new AIBot.Core.Tools.ToolRegistry();
                if (useSimulatedTools)
                    new AIBot.Core.Tools.SimulatedToolHost(session.SimState).RegisterAll(toolRegistry);

                var backend = new HttpLlmBackend(cfg.model);
                var loop = new AgentLoop(backend, new ServerLogSink(runtimeLogs, "Agent",
                    new RuntimeLogContext
                    {
                        RequestId = http.TraceIdentifier,
                        GameId = gid,
                        NpcId = body.NpcId,
                        PlayerId = body.PlayerId,
                        SessionId = body.SessionId
                    }),
                    backendFactory: settings => new HttpLlmBackend(settings));
                var input = new AgentRunInput
                {
                    Config = cfg,
                    World = world,
                    Game = new SimGameContext(session.SimState),
                    UserMessage = body.Message,
                    Memory = session.Memory,
                    MemorySummary = playerScoped ? longMemory?.summary : session.Summary,
                    MemoryFacts = playerScoped
                        ? PlayerMemoryService.ToPromptFacts(longMemory, effectiveMemory.policy)
                        : session.Facts,
                    ResolvedMemoryPolicy = effectiveMemory.policy,
                    DeferMemorySummarizationToHost = playerScoped
                        && effectiveMemory.policy.backgroundSummarization,
                    Tools = toolRegistry,
                    HostContext = session.SimState
                };

                AgentLoopResult result;
                try
                {
                    result = await loop.RunAsync(input, sse, app.Lifetime.ApplicationStopping);
                }
                catch (OperationCanceledException)
                {
                    channel.Writer.TryComplete();
                    await pump;
                    return;
                }

                session.LastActiveUtc = DateTime.UtcNow;

                // 同步摘要兼容路径：玩家范围写长期文件，session 范围仍写会话文件。
                if (result.MemorySummary != null)
                {
                    if (playerScoped)
                    {
                        try
                        {
                            await playerMemories.SaveLegacySummaryAsync(gid, body.NpcId, body.PlayerId,
                                body.SessionId, result.MemorySummary, result.MemoryFacts,
                                effectiveMemory.policy.maxFacts, app.Lifetime.ApplicationStopping);
                        }
                        catch
                        {
                            session.Memory.RestoreEvicted(result.MemorySummarizedMessages);
                            SessionStore.Save(session);
                            throw;
                        }
                    }
                    else
                    {
                        session.Summary = result.MemorySummary;
                        session.Facts = result.MemoryFacts ?? new List<string>();
                    }
                }

                if (!SessionStore.Save(session) && result.MemorySummarizedMessages != null)
                {
                    // 摘要虽在内存成功，但 session 未确认落盘时恢复原批次，避免静默丢失。
                    session.Memory.RestoreEvicted(result.MemorySummarizedMessages);
                    SessionStore.Save(session);
                }

                var tools = new List<string>();
                foreach (ToolExecution ex in result.ToolExecutions) tools.Add(ex.Call.Function.Name);

                ChatLogService.Record(gid, new ChatLogService.ChatLogEntry
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    npcId = cfg.npcId,
                    playerId = body.PlayerId,
                    sessionId = body.SessionId,
                    legacyMemoryScope = legacyMemoryScope,
                    userMessage = body.Message,
                    say = result.Reply.say,
                    emotion = result.Reply.emotion,
                    action = result.Reply.action,
                    fallback = result.UsedFallback,
                    promptTokens = result.Usage.PromptTokens,
                    completionTokens = result.Usage.CompletionTokens,
                    elapsedMs = result.ElapsedMs,
                    tools = tools,
                    injection = result.FlaggedInjection
                });

                var usage = new JObject
                {
                    ["promptTokens"] = result.Usage.PromptTokens,
                    ["completionTokens"] = result.Usage.CompletionTokens
                };
                var replyEvent = new JObject
                {
                    ["type"] = "reply",
                    ["say"] = result.Reply.say,
                    ["emotion"] = result.Reply.emotion,
                    ["action"] = result.Reply.action,
                    ["fallback"] = result.UsedFallback,
                    ["usage"] = usage,
                    ["elapsedMs"] = result.ElapsedMs
                };
                if (result.UsedFallback && sse.LastModelError != null)
                {
                    replyEvent["diagnostic"] = new JObject
                    {
                        ["code"] = sse.LastModelError.Code,
                        ["status"] = sse.LastModelError.Status,
                        ["message"] = sse.LastModelError.Message,
                        ["retryable"] = sse.LastModelError.Retryable
                    };
                }
                sse.Write(replyEvent);
                sse.Write(new JObject
                {
                    ["type"] = "done",
                    ["sessionId"] = body.SessionId,
                    ["requestId"] = requestId
                });
                SessionStore.CompleteRequest(requestRecord, sse.SnapshotEvents());
                if (!SessionStore.Save(session))
                {
                    runtimeLogs.Write(LogLevel.Warning, "Chat", "idempotency_result_save_failed",
                        "聊天已完成，但幂等重放结果保存失败", new RuntimeLogContext
                        {
                            RequestId = requestId,
                            GameId = gid,
                            NpcId = body.NpcId,
                            PlayerId = body.PlayerId,
                            SessionId = body.SessionId
                        });
                }
                channel.Writer.TryComplete();
                await pump;

                // done 已经发送并刷新后才投递；后台摘要不增加玩家等待时间。
                if (playerScoped && effectiveMemory.policy.backgroundSummarization
                    && effectiveMemory.policy.summaryThreshold > 0
                    && session.Memory.EvictedCount >= effectiveMemory.policy.summaryThreshold)
                {
                    summaryQueue.Enqueue(gid, body.NpcId, body.PlayerId, body.SessionId);
                }
                }
                catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    channel?.Writer.TryComplete();
                    if (pump != null) await pump;
                }
                catch (Exception ex)
                {
                    if (requestRecord == null || sse == null || channel == null) throw;
                    runtimeLogs.Write(LogLevel.Error, "Chat", "request_failed",
                        "聊天请求执行失败: " + ex.Message, new RuntimeLogContext
                        {
                            RequestId = requestId,
                            GameId = gid,
                            NpcId = body.NpcId,
                            PlayerId = body.PlayerId,
                            SessionId = body.SessionId,
                            ErrorCode = "internal_error"
                        }, ex);
                    sse.Write(new JObject
                    {
                        ["type"] = "error",
                        ["code"] = "internal_error",
                        ["status"] = 500,
                        ["message"] = "Server 处理请求时发生内部错误",
                        ["retryable"] = true,
                        ["terminal"] = true,
                        ["requestId"] = requestId
                    });
                    SessionStore.CompleteRequest(requestRecord, sse.SnapshotEvents(), failed: true);
                    SessionStore.Save(session);
                    channel.Writer.TryComplete();
                    if (pump != null) await pump;
                }
                finally
                {
                    session.Gate.Release();
                }
            }).RequireRateLimiting("chat");
        }

        private static bool CanUseAdminOverrides(HttpContext http, IConfiguration configuration)
        {
            string adminToken = Environment.GetEnvironmentVariable("AIBOT_ADMIN_TOKEN")
                ?? configuration["Security:AdminToken"];
            if (string.IsNullOrEmpty(adminToken)) return true; // 本地开发未启用鉴权
            return string.Equals(http.Request.Headers.Authorization.ToString(),
                "Bearer " + adminToken, StringComparison.Ordinal);
        }

        private static string BuildRequestFingerprint(ChatRequestBody body, string normalizedToolMode)
        {
            string canonical = JsonConvert.SerializeObject(new
            {
                body.NpcId,
                body.PlayerId,
                body.SessionId,
                body.Message,
                ToolMode = normalizedToolMode,
                body.SimState,
                body.Override,
                body.MemoryOverride
            }, Formatting.None);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        private static async Task ReplayEventsAsync(HttpContext http, ChatRequestRecord record)
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

        /// <summary>把 Core 回调转成附录B的 SSE 事件行，经 Channel 泵异步写出。</summary>
        private sealed class SseEventWriter : ILlmStreamSink, IReasoningSink, IToolExecutionSink
        {
            private readonly ChannelWriter<string> _writer;
            private readonly List<string> _events = new List<string>();
            public ModelErrorInfo LastModelError { get; private set; }

            public SseEventWriter(ChannelWriter<string> writer, string sessionId)
            {
                _writer = writer;
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
                lock (_events) _events.Add(line);
                _writer.TryWrite(line);
            }

            public List<string> SnapshotEvents()
            {
                lock (_events) return new List<string>(_events);
            }
        }
    }
}
