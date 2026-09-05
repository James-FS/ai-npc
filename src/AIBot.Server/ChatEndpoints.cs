using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
        public string ToolMode { get; set; } = ServerToolModes.None; // none（正式默认）/ simulated（仅调试）/ game（工具回传）
        public List<ClientToolDescriptor> Tools { get; set; } // game：客户端上传的工具描述（首段必带）
        public string RoundToken { get; set; }             // game 续跑：待续挂起轮令牌
        public List<ClientToolResult> ToolResults { get; set; } // game 续跑：工具执行结果
        public string GameContext { get; set; }            // game：原始游戏状态快照（仅作 prompt 上下文，不进指纹）
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
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status413PayloadTooLarge,
                        "payload_too_large", "请求体过大");
                    return;
                }
                ChatRequestBody body;
                try { body = await http.Request.ReadFromJsonAsync<ChatRequestBody>(http.RequestAborted); }
                catch { body = null; }
                if (body == null || string.IsNullOrEmpty(body.NpcId))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "invalid_request", "npcId 与 message 必填");
                    return;
                }
                if (!ServerToolModes.TryNormalize(body.ToolMode, out string toolMode))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "invalid_tool_mode", "toolMode 仅支持 none、simulated 或 game");
                    return;
                }
                bool useSimulatedTools = toolMode == ServerToolModes.Simulated;
                bool useGameTools = toolMode == ServerToolModes.Game;
                bool isToolResume = useGameTools && !string.IsNullOrWhiteSpace(body.RoundToken);
                if (isToolResume) body.Message = null; // 续跑轮不携带新消息，避免污染指纹与语义
                if (useSimulatedTools && !CanUseAdminOverrides(http, config))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status403Forbidden,
                        "admin_auth_required", "simulated 工具仅允许授权的调试请求使用");
                    return;
                }
                if (!isToolResume && string.IsNullOrEmpty(body.Message))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "invalid_request", "npcId 与 message 必填");
                    return;
                }
                string headerRequestId = http.Request.Headers["X-Request-Id"].ToString();
                if (!string.IsNullOrWhiteSpace(headerRequestId) && !ChatRequestIds.IsValid(headerRequestId))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "invalid_request_id", "X-Request-Id 格式无效");
                    return;
                }
                if (!string.IsNullOrWhiteSpace(headerRequestId)
                    && !string.IsNullOrWhiteSpace(body.RequestId)
                    && !string.Equals(headerRequestId, body.RequestId, StringComparison.Ordinal))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "request_id_mismatch", "X-Request-Id 与请求体 requestId 必须一致");
                    return;
                }
                string requestId = !string.IsNullOrWhiteSpace(body.RequestId)
                    ? body.RequestId
                    : !string.IsNullOrWhiteSpace(headerRequestId) ? headerRequestId : http.TraceIdentifier;
                if (!ChatRequestIds.IsValid(requestId))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "invalid_request_id", "requestId 非法（仅允许字母数字及 _ . : -，最长80位）");
                    return;
                }
                body.RequestId = requestId;
                http.TraceIdentifier = requestId;
                http.Response.Headers["X-Request-Id"] = requestId;
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(body.NpcId))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "invalid_id", "gameId 或 npcId 非法");
                    return;
                }
                if (!DataStore.IsValidSessionId(body.SessionId))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "invalid_session_id", "sessionId 非法（仅允许字母数字及 _ . : -，最长128位）");
                    return;
                }
                if (!string.IsNullOrEmpty(body.PlayerId) && !DataStore.IsValidPlayerId(body.PlayerId))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                        "invalid_player_id", "playerId 非法（仅允许字母数字及 _ . : -，最长128位）");
                    return;
                }
                if (!isToolResume && body.Message.Length > 4000)
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status413PayloadTooLarge,
                        "message_too_large", "message 最长 4000 字符");
                    return;
                }
                if (useGameTools && body.GameContext != null && body.GameContext.Length > 8192)
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status413PayloadTooLarge,
                        "game_context_too_large", "gameContext 最长 8192 字符");
                    return;
                }
                if (body.MemoryOverride != null && !CanUseAdminOverrides(http, config))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status403Forbidden,
                        "admin_auth_required", "memoryOverride 仅允许管理端调试请求使用");
                    return;
                }
                if (body.Override != null && !CanUseAdminOverrides(http, config))
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status403Forbidden,
                        "admin_auth_required", "model override 仅允许管理端调试请求使用");
                    return;
                }

                AgentConfigDto cfg = DataStore.LoadNpc(gid, body.NpcId);
                if (cfg == null)
                {
                    await ApiErrorWriter.WriteAsync(http, StatusCodes.Status404NotFound,
                        "npc_not_found", "npc not found: " + body.NpcId + " (game=" + gid + ")");
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
                NormalizeModelSettings(cfg.model);

                // game 模式首段：校验客户端上传的工具 schema，并与 NPC 配置的 enabledToolIds 求交。
                List<ToolSchema> gameSchemas = null;
                if (useGameTools && !isToolResume)
                {
                    gameSchemas = ValidateGameTools(body.Tools, cfg.enabledToolIds);
                    if (gameSchemas == null)
                    {
                        await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                            "invalid_tool_schema", "tools 非法：最多 16 个，id 需合法且唯一，单条 schema 最长 4KB");
                        return;
                    }
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

                    // game 模式挂起轮状态机：非续跑请求撞上未消费挂起轮要拒绝；
                    // 续跑请求校验令牌、链上轮数上限与工具结果完整性。通过后才允许 BeginRequest。
                    PendingToolRound pendingRound = null;
                    int gameRoundIndex = 0;
                    string logUserMessage = body.Message;
                    List<LlmMessage> resumeToolMessages = null;
                    if (useGameTools)
                    {
                        pendingRound = SessionStore.TakeLivePendingToolRound(session);
                        if (!isToolResume)
                        {
                            if (pendingRound != null)
                            {
                                await ApiErrorWriter.WriteAsync(http, StatusCodes.Status409Conflict,
                                    "tool_round_pending", "会话存在未完成的工具轮，请先携带 roundToken 续跑或等待其超时");
                                return;
                            }
                        }
                        else
                        {
                            if (pendingRound == null)
                            {
                                await ApiErrorWriter.WriteAsync(http, StatusCodes.Status409Conflict,
                                    "tool_round_unknown", "工具挂起轮不存在、已消费或已过期，请用新的 requestId 重新对话");
                                return;
                            }
                            if (!string.Equals(pendingRound.roundToken, body.RoundToken, StringComparison.Ordinal))
                            {
                                await ApiErrorWriter.WriteAsync(http, StatusCodes.Status409Conflict,
                                    "tool_round_mismatch", "roundToken 与当前挂起轮不一致");
                                return;
                            }
                            if (pendingRound.roundIndex >= MaxGameToolRounds)
                            {
                                await ApiErrorWriter.WriteAsync(http, StatusCodes.Status409Conflict,
                                    "tool_round_limit", "工具回传轮数超过上限，请开启新的对话");
                                return;
                            }
                            if (!TryBuildResumeToolMessages(pendingRound, body.ToolResults,
                                out resumeToolMessages, out string toolResultsError))
                            {
                                await ApiErrorWriter.WriteAsync(http, StatusCodes.Status400BadRequest,
                                    "invalid_tool_results", toolResultsError);
                                return;
                            }
                            gameRoundIndex = pendingRound.roundIndex;
                            logUserMessage = FindLastUserContent(pendingRound.messages);
                        }
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

                    channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        // 中间 token 可在写满时丢弃，但终态 reply/done/error 由 SseEventWriter
                        // 使用等待写入，不能因慢客户端丢失结束信号。
                        FullMode = BoundedChannelFullMode.Wait
                    });
                    sse = new SseEventWriter(channel.Writer, http.RequestAborted);
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
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                        catch (Exception ex)
                        {
                            // 客户端断线/响应流关闭不应把已经成功完成的业务请求改写为 failed。
                            runtimeLogs.Write(LogLevel.Warning, "Chat", "response_stream_failed",
                                "SSE 响应写出失败，业务结果仍按原状态保留: " + ex.Message, null, ex);
                        }
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
                    Game = useGameTools
                        ? new CompositeGameContext(session.SimState, body.GameContext)
                        : new SimGameContext(session.SimState),
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
                    HostContext = session.SimState,
                    DeferToolsToHost = useGameTools,
                    DeferredTools = useGameTools
                        ? (isToolResume
                            ? (pendingRound.schemas ?? new List<ToolSchema>())
                            : (gameSchemas ?? new List<ToolSchema>()))
                        : null,
                    ResumeMessages = isToolResume
                        ? BuildResumeMessages(pendingRound.messages, resumeToolMessages)
                        : null
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

                if (result.PendingToolCalls != null)
                {
                    // game 模式挂起轮：先写挂起态（与幂等记录同一次 Save 落盘，崩溃窗口内状态一致），
                    // 再下发 tool_pending 终态事件；工具执行结果由客户端携带 roundToken 发起续跑请求。
                    string roundToken = requestId + "#" + gameRoundIndex;
                    session.PendingToolRound = new PendingToolRound
                    {
                        roundToken = roundToken,
                        roundIndex = gameRoundIndex + 1,
                        calls = CloneJson(result.PendingToolCalls) ?? new List<ToolCallDto>(),
                        messages = CloneJson(result.PendingMessages) ?? new List<LlmMessage>(),
                        schemas = CloneJson(isToolResume ? pendingRound.schemas : gameSchemas)
                            ?? new List<ToolSchema>(),
                        createdUtc = DateTime.UtcNow
                    };
                    var pendingCalls = new JArray();
                    foreach (ToolCallDto call in result.PendingToolCalls)
                    {
                        string args = call.Function?.Arguments;
                        JToken argsToken;
                        try { argsToken = string.IsNullOrEmpty(args) ? new JObject() : JToken.Parse(args); }
                        catch { argsToken = args ?? "{}"; }
                        pendingCalls.Add(new JObject
                        {
                            ["callId"] = call.Id,
                            ["name"] = call.Function?.Name,
                            ["args"] = argsToken
                        });
                    }
                    sse.Write(new JObject
                    {
                        ["type"] = "tool_pending",
                        ["requestId"] = requestId,
                        ["roundToken"] = roundToken,
                        ["calls"] = pendingCalls
                    });
                    SessionStore.CompleteRequest(requestRecord, sse.SnapshotReplayEvents());
                    if (!SessionStore.Save(session))
                    {
                        runtimeLogs.Write(LogLevel.Warning, "Chat", "tool_round_save_failed",
                            "工具挂起轮已下发，但持久化失败；客户端续跑可能得到 tool_round_unknown",
                            new RuntimeLogContext
                            {
                                RequestId = requestId,
                                GameId = gid,
                                NpcId = body.NpcId,
                                PlayerId = body.PlayerId,
                                SessionId = body.SessionId
                            });
                    }
                    else
                    {
                        runtimeLogs.Write(LogLevel.Info, "Chat", "tool_round_pending",
                            "工具调用已下发客户端执行: " + string.Join(",",
                                result.PendingToolCalls.Select(c => c.Function?.Name ?? c.Id)),
                            new RuntimeLogContext
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
                    return;
                }

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
                    userMessage = logUserMessage,
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
                if (isToolResume) session.PendingToolRound = null; // 续跑完成：消费挂起轮
                SessionStore.CompleteRequest(requestRecord, sse.SnapshotReplayEvents());
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
                    SessionStore.CompleteRequest(requestRecord, sse.SnapshotReplayEvents(), failed: true);
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

        private static void NormalizeModelSettings(ModelSettings model)
        {
            if (model == null) return;
            model.model = string.IsNullOrWhiteSpace(model.model) ? "deepseek-chat" : model.model.Trim();
            if (model.model.Length > 128) model.model = model.model.Substring(0, 128);
            if (float.IsNaN(model.temperature) || float.IsInfinity(model.temperature)) model.temperature = 0.8f;
            model.temperature = Math.Max(0f, Math.Min(2f, model.temperature));
            model.maxTokens = Math.Max(1, Math.Min(8192, model.maxTokens));
            model.timeoutMs = Math.Max(1000, Math.Min(120000, model.timeoutMs));
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
                body.MemoryOverride,
                body.Tools,
                body.RoundToken,
                body.ToolResults
                // GameContext 故意不进指纹：它是纯 prompt 上下文（无副作用），续跑重试时游戏
                // 状态可能已变化，纳入会把同 requestId 的重试误判为 request_id_conflict。
            }, Formatting.None);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>game 模式挂起-续跑链的最大轮数（跨请求计数，独立于单次 AgentLoop 的 MaxToolRounds）。</summary>
        private const int MaxGameToolRounds = 8;
        private const int MaxGameTools = 16;
        private const int MaxToolSchemaChars = 4 * 1024;
        private const int MaxToolResultContentChars = 12000;

        /// <summary>
        /// 校验 game 模式首段上传的工具描述：数量/长度/合法 id，schema 必须是合法 JSON，
        /// 并与 NPC 配置的 enabledToolIds 求交。返回 null 表示存在非法项（调用方返回 400）；
        /// enabledToolIds 之外的条目按 Core 语义静默跳过。
        /// </summary>
        private static List<ToolSchema> ValidateGameTools(List<ClientToolDescriptor> uploaded, List<string> enabledToolIds)
        {
            if (uploaded == null || uploaded.Count == 0) return new List<ToolSchema>();
            if (uploaded.Count > MaxGameTools) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var enabled = new HashSet<string>(enabledToolIds ?? new List<string>(), StringComparer.Ordinal);
            var result = new List<ToolSchema>();
            foreach (ClientToolDescriptor descriptor in uploaded)
            {
                if (descriptor == null || string.IsNullOrEmpty(descriptor.Id)
                    || !DataStore.IsValidId(descriptor.Id) || !seen.Add(descriptor.Id)) return null;
                if (!enabled.Contains(descriptor.Id)) continue; // NPC 配置未启用的工具不下发
                if (!string.IsNullOrEmpty(descriptor.Description)
                    && descriptor.Description.Length > 2000) return null;
                JObject parameters;
                try
                {
                    parameters = string.IsNullOrEmpty(descriptor.ParametersSchema)
                        ? JObject.Parse("{\"type\":\"object\",\"properties\":{}}")
                        : JObject.Parse(descriptor.ParametersSchema);
                }
                catch (JsonException)
                {
                    return null; // schema 非法 JSON
                }
                if (parameters.ToString(Formatting.None).Length > MaxToolSchemaChars) return null;
                result.Add(new ToolSchema
                {
                    Function = new FunctionDef
                    {
                        Name = descriptor.Id,
                        Description = descriptor.Description,
                        Parameters = parameters
                    }
                });
            }
            return result;
        }

        /// <summary>
        /// 把客户端工具执行结果按挂起 calls 的顺序配对成 tool 消息。
        /// 每个 callId 必须恰好出现一次；结果文本截断到与 Core 相同的工具结果上限。
        /// </summary>
        private static bool TryBuildResumeToolMessages(PendingToolRound pending, List<ClientToolResult> results,
            out List<LlmMessage> toolMessages, out string error)
        {
            toolMessages = null;
            var byCallId = new Dictionary<string, ClientToolResult>(StringComparer.Ordinal);
            foreach (ClientToolResult item in results ?? new List<ClientToolResult>())
            {
                if (item == null || string.IsNullOrEmpty(item.CallId))
                {
                    error = "toolResults 存在缺少 callId 的条目";
                    return false;
                }
                if (!byCallId.TryAdd(item.CallId, item))
                {
                    error = "toolResults 存在重复 callId: " + item.CallId;
                    return false;
                }
            }
            toolMessages = new List<LlmMessage>();
            foreach (ToolCallDto call in pending.calls ?? new List<ToolCallDto>())
            {
                if (string.IsNullOrEmpty(call?.Id) || !byCallId.TryGetValue(call.Id, out ClientToolResult matched))
                {
                    error = "缺少工具执行结果: " + (call?.Id ?? "<unknown>");
                    return false;
                }
                string content = matched.Message;
                if (string.IsNullOrWhiteSpace(content))
                    content = matched.Success ? "done" : "tool execution failed";
                if (content.Length > MaxToolResultContentChars)
                    content = content.Substring(0, MaxToolResultContentChars) + "…[已截断]";
                toolMessages.Add(new LlmMessage { Role = "tool", ToolCallId = call.Id, Content = content });
            }
            error = null;
            return true;
        }

        /// <summary>续跑消息列表 = 挂起态消息（含 assistant(tool_calls)）+ 按序配对的 tool 结果消息。</summary>
        private static List<LlmMessage> BuildResumeMessages(List<LlmMessage> pendingMessages,
            List<LlmMessage> toolMessages)
        {
            var merged = new List<LlmMessage>(pendingMessages ?? new List<LlmMessage>());
            merged.AddRange(toolMessages ?? new List<LlmMessage>());
            return merged;
        }

        private static string FindLastUserContent(List<LlmMessage> messages)
        {
            for (int i = (messages?.Count ?? 0) - 1; i >= 0; i--)
            {
                if (messages[i] != null && string.Equals(messages[i].Role, "user", StringComparison.Ordinal))
                    return messages[i].Content;
            }
            return null;
        }

        /// <summary>深拷贝挂起态，彻底脱离 AgentLoop 的局部对象图后再挂到会话上。</summary>
        private static T CloneJson<T>(T value)
        {
            if (value == null) return default;
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
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
            private int _replayBytes;
            public ModelErrorInfo LastModelError { get; private set; }

            private readonly CancellationToken _writeCancellation;

            public SseEventWriter(ChannelWriter<string> writer, CancellationToken writeCancellation)
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
            /// 不应写入 Session 文件或 MySQL payload。
            /// </summary>
            public List<string> SnapshotReplayEvents()
            {
                lock (_events) return new List<string>(_events);
            }
        }
    }
}
