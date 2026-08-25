using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Llm;
using AIBot.Core.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>对话请求体（对应主方案 §7.1 契约）。</summary>
    public class ChatRequestBody
    {
        public string NpcId { get; set; }
        public string SessionId { get; set; } = "s-local";
        public string Message { get; set; }
        public SimGameState SimState { get; set; }
        public OverrideSettings Override { get; set; }
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

            app.MapGet("/api/games/{gid}/npcs", (string gid) => Results.Json(new { gameId = gid, npcs = DataStore.ListNpcIds(gid) }));

            app.MapPost("/api/games/{gid}/chat/stream", async (string gid, HttpContext http) =>
            {
                ChatRequestBody body;
                try { body = await http.Request.ReadFromJsonAsync<ChatRequestBody>(http.RequestAborted); }
                catch { body = null; }
                if (body == null || string.IsNullOrEmpty(body.NpcId) || string.IsNullOrEmpty(body.Message))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("npcId 与 message 必填");
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

                SessionState session = SessionStore.GetOrCreate(gid, body.NpcId, body.SessionId, cfg.memory.shortTermTurns);

                http.Response.ContentType = "text/event-stream; charset=utf-8";
                http.Response.Headers.CacheControl = "no-cache";

                var channel = Channel.CreateUnbounded<string>();
                var sse = new SseEventWriter(channel.Writer, body.SessionId);
                Task pump = Task.Run(async () =>
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
                        foreach (var kv in body.SimState.extras) session.SimState.extras[kv.Key] = kv.Value;
                    }
                }

                // 模拟工具：give_item/change_favor/advance_stage 真实读写会话状态（随会话持久化）
                var toolRegistry = new AIBot.Core.Tools.ToolRegistry();
                new AIBot.Core.Tools.SimulatedToolHost(session.SimState).RegisterAll(toolRegistry);

                var backend = new HttpLlmBackend(cfg.model);
                var loop = new AgentLoop(backend, new ConsoleLogSink());
                var input = new AgentRunInput
                {
                    Config = cfg,
                    World = world,
                    Game = new SimGameContext(session.SimState),
                    UserMessage = body.Message,
                    Memory = session.Memory,
                    MemorySummary = session.Summary,
                    MemoryFacts = session.Facts,
                    Tools = toolRegistry,
                    HostContext = session.SimState
                };

                AgentLoopResult result;
                try
                {
                    result = await loop.RunAsync(input, sse, http.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    channel.Writer.TryComplete();
                    await pump;
                    return;
                }

                session.LastActiveUtc = DateTime.UtcNow;

                // 本轮触发了记忆摘要 → 写回会话状态（下一轮对话即注入长期记忆）
                if (result.MemorySummary != null)
                {
                    session.Summary = result.MemorySummary;
                    session.Facts = result.MemoryFacts ?? new List<string>();
                }

                SessionStore.Save(session);                       // 每轮落盘：重启不丢记忆

                var tools = new List<string>();
                foreach (ToolExecution ex in result.ToolExecutions) tools.Add(ex.Call.Function.Name);

                ChatLogService.Record(gid, new ChatLogService.ChatLogEntry
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    npcId = cfg.npcId,
                    sessionId = body.SessionId,
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
                sse.Write(new JObject
                {
                    ["type"] = "reply",
                    ["say"] = result.Reply.say,
                    ["emotion"] = result.Reply.emotion,
                    ["action"] = result.Reply.action,
                    ["fallback"] = result.UsedFallback,
                    ["usage"] = usage,
                    ["elapsedMs"] = result.ElapsedMs
                });
                sse.Write(new JObject { ["type"] = "done", ["sessionId"] = body.SessionId });
                channel.Writer.TryComplete();
                await pump;
            });
        }

        /// <summary>把 Core 回调转成附录B的 SSE 事件行，经 Channel 泵异步写出。</summary>
        private sealed class SseEventWriter : ILlmStreamSink, IReasoningSink, IToolExecutionSink
        {
            private readonly ChannelWriter<string> _writer;

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
                    ["name"] = execution.Call.Function.Name,
                    ["args"] = ParseArgs(execution.Call.Function.Arguments),
                    ["success"] = execution.Result.Success,
                    ["result"] = execution.Result.MessageForModel
                });
            }

            public void OnError(Exception ex)
            {
                Write(new JObject { ["type"] = "error", ["message"] = ex.Message });
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
                _writer.TryWrite("data: " + payload.ToString(Formatting.None) + "\n\n");
            }
        }
    }
}
