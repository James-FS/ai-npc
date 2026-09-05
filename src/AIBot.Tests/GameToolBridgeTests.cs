using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using AIBot.Core.Protocol;
using AIBot.Core.Tools;
using AIBot.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>
    /// Server game 模式（两段式工具回传）的契约测试：
    /// AgentLoop 挂起/续跑语义、tool_pending 协议解析、挂起轮生命周期。
    /// </summary>
    public class GameToolBridgeTests
    {
        private static AgentConfigDto Config()
        {
            var cfg = new AgentConfigDto
            {
                npcId = "tester",
                displayName = "测试员",
                persona = "严谨",
                fallbackReplies = { "（测试兜底台词）" }
            };
            cfg.enabledToolIds.Add("echo");
            return cfg;
        }

        private static ToolSchema EchoSchema()
        {
            return new ToolSchema
            {
                Function = new FunctionDef
                {
                    Name = "echo",
                    Description = "复述一句话（测试用）",
                    Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}")
                }
            };
        }

        private static AgentRunInput DeferInput(ShortTermMemory memory, string message)
        {
            return new AgentRunInput
            {
                Config = Config(),
                World = new WorldConfigDto { description = "测试世界" },
                Game = new SimGameContext(new SimGameState { stage = 0 }),
                UserMessage = message,
                Memory = memory,
                DeferToolsToHost = true,
                DeferredTools = new List<ToolSchema> { EchoSchema() }
            };
        }

        [Fact]
        public async Task DeferTools_PendsToolCalls_WithoutTouchingMemory()
        {
            var backend = new MockLlmBackend(Sse.Round(
                Sse.ToolStart("call_1", "echo"), Sse.ToolArgs("{\"text\":\"嗨\"}")));
            var loop = new AgentLoop(backend);
            var memory = new ShortTermMemory(12);

            AgentLoopResult result = await loop.RunAsync(
                DeferInput(memory, "复述'嗨'"), new RecordingSink(), CancellationToken.None);

            // 挂起轮：不执行、不解析回复、不写记忆
            Assert.Null(result.Reply);
            Assert.NotNull(result.PendingToolCalls);
            Assert.Equal("echo", result.PendingToolCalls[0].Function.Name);
            Assert.Equal("call_1", result.PendingToolCalls[0].Id);
            Assert.Empty(memory.Messages);

            // 模型请求仍带 tools 与 args；无强制收敛时不启用 response_format
            LlmRequest req = backend.Requests[0];
            Assert.NotNull(req.Tools);
            Assert.Null(req.ResponseFormat);
            Assert.Equal("assistant", result.PendingMessages[result.PendingMessages.Count - 1].Role);
            Assert.NotNull(result.PendingMessages[result.PendingMessages.Count - 1].ToolCalls);
        }

        [Fact]
        public async Task Resume_CompletesReply_AndRecordsMemoryOnce()
        {
            // 第一段：挂起
            var backend1 = new MockLlmBackend(Sse.Round(
                Sse.ToolStart("call_1", "echo"), Sse.ToolArgs("{\"text\":\"嗨\"}")));
            var memory = new ShortTermMemory(12);
            AgentLoopResult pending = await new AgentLoop(backend1).RunAsync(
                DeferInput(memory, "复述'嗨'"), new RecordingSink(), CancellationToken.None);

            // 模拟宿主：把工具执行结果追加为 tool 消息
            var resumeMessages = new List<LlmMessage>(pending.PendingMessages)
            {
                new LlmMessage { Role = "tool", ToolCallId = "call_1", Content = "echo:嗨" }
            };

            // 第二段：续跑到最终台词
            string finalJson = "{\"say\":\"我说完了。\",\"emotion\":\"neutral\",\"action\":\"idle\"}";
            var backend2 = new MockLlmBackend(Sse.Round(Sse.Token(finalJson)));
            AgentLoopResult result = await new AgentLoop(backend2).RunAsync(
                new AgentRunInput
                {
                    Config = Config(),
                    World = new WorldConfigDto { description = "测试世界" },
                    Game = new SimGameContext(new SimGameState { stage = 1 }),
                    Memory = memory,
                    DeferToolsToHost = true,
                    DeferredTools = new List<ToolSchema> { EchoSchema() },
                    ResumeMessages = resumeMessages
                },
                new RecordingSink(), CancellationToken.None);

            Assert.False(result.UsedFallback);
            Assert.Equal("我说完了。", result.Reply.say);

            // 续跑请求：system 被重建（拿到最新快照），tool 结果在 assistant(tool_calls) 之后
            LlmRequest req = backend2.Requests[0];
            Assert.Equal("system", req.Messages[0].Role);
            Assert.Equal("tool", req.Messages[req.Messages.Count - 1].Role);
            Assert.Equal("echo:嗨", req.Messages[req.Messages.Count - 1].Content);
            Assert.Equal("assistant", req.Messages[req.Messages.Count - 2].Role);

            // 记忆只入账一次：user + assistant，user 是原始玩家消息
            Assert.Equal(2, memory.Messages.Count);
            Assert.Contains("复述'嗨'", memory.Messages[0].Content);
        }

        [Fact]
        public async Task ResumeFailure_FallsBackWithOriginalUserMessage()
        {
            var backend1 = new MockLlmBackend(Sse.Round(
                Sse.ToolStart("call_1", "echo"), Sse.ToolArgs("{\"text\":\"嗨\"}")));
            var memory = new ShortTermMemory(12);
            AgentLoopResult pending = await new AgentLoop(backend1).RunAsync(
                DeferInput(memory, "复述'嗨'"), new RecordingSink(), CancellationToken.None);

            // 续跑时模型不可用（无脚本 → 失败）→ fallback；入账的 user 应取自挂起态而非空消息
            var backend2 = new MockLlmBackend();
            AgentLoopResult result = await new AgentLoop(backend2).RunAsync(
                new AgentRunInput
                {
                    Config = Config(),
                    World = new WorldConfigDto(),
                    Game = new SimGameContext(new SimGameState()),
                    Memory = memory,
                    UserMessage = null,
                    DeferToolsToHost = true,
                    ResumeMessages = new List<LlmMessage>(pending.PendingMessages)
                },
                new RecordingSink(), CancellationToken.None);

            Assert.True(result.UsedFallback);
            Assert.Equal(2, memory.Messages.Count);
            Assert.Contains("复述'嗨'", memory.Messages[0].Content);
        }

        [Fact]
        public void ToolPendingEvent_ParsesCallsAndToken()
        {
            string json = new JObject
            {
                ["type"] = "tool_pending",
                ["requestId"] = "req-1",
                ["roundToken"] = "req-1#0",
                ["calls"] = new JArray(new JObject
                {
                    ["callId"] = "call_1",
                    ["name"] = "accept_quest",
                    ["args"] = new JObject { ["questId"] = "main_001" }
                })
            }.ToString(Formatting.None);

            Assert.True(ServerChatEventParser.TryParse(json, out ServerChatEvent parsed));
            Assert.Equal(ServerChatEventKind.ToolPending, parsed.Kind);
            Assert.True(parsed.Terminal);
            Assert.Equal("req-1#0", parsed.RoundToken);
            Assert.Single(parsed.ToolCalls);
            Assert.Equal("accept_quest", parsed.ToolCalls[0].Function.Name);
            Assert.Equal("{\"questId\":\"main_001\"}", parsed.ToolCalls[0].Function.Arguments);
        }

        [Fact]
        public void ToolMode_Game_IsNormalized()
        {
            Assert.True(ServerToolModes.TryNormalize(" GAME ", out string normalized));
            Assert.Equal(ServerToolModes.Game, normalized);
            Assert.False(ServerToolModes.TryNormalize("bridge", out _));
        }

        [Fact]
        public void PendingToolRound_ExpiresAfterTimeout()
        {
            var session = new SessionState();
            session.PendingToolRound = new PendingToolRound
            {
                roundToken = "req-1#0",
                roundIndex = 1,
                createdUtc = DateTime.UtcNow - SessionStore.PendingToolRoundTimeout - TimeSpan.FromSeconds(1)
            };

            Assert.Null(SessionStore.TakeLivePendingToolRound(session));
            Assert.Null(session.PendingToolRound);   // 过期即清除，允许新一轮对话

            session.PendingToolRound = new PendingToolRound
            {
                roundToken = "req-2#0",
                roundIndex = 1,
                createdUtc = DateTime.UtcNow
            };
            PendingToolRound live = SessionStore.TakeLivePendingToolRound(session);
            Assert.NotNull(live);
            Assert.Equal("req-2#0", live.roundToken);
        }

        [Fact]
        public void PendingToolRound_RoundTripsThroughSessionFileDto()
        {
            // 持久化契约：SessionFileDto v4 必须无损携带挂起轮（JSON 与 MySQL payload 共用此 DTO）
            var dto = new SessionStore.SessionFileDto
            {
                npcId = "lin",
                sessionId = "s1",
                pendingToolRound = new PendingToolRound
                {
                    roundToken = "req-1#0",
                    roundIndex = 1,
                    calls = new List<ToolCallDto>
                    {
                        new ToolCallDto { Id = "call_1", Function = new FunctionCall { Name = "echo", Arguments = "{\"text\":\"嗨\"}" } }
                    },
                    messages = new List<LlmMessage>
                    {
                        LlmMessage.System("system"),
                        LlmMessage.User("你好"),
                        new LlmMessage { Role = "assistant", Content = "", ToolCalls = new List<ToolCallDto> { new ToolCallDto { Id = "call_1" } } }
                    },
                    schemas = new List<ToolSchema> { new ToolSchema { Function = new FunctionDef { Name = "echo" } } },
                    createdUtc = DateTime.UtcNow
                }
            };

            SessionStore.SessionFileDto restored = JsonConvert.DeserializeObject<SessionStore.SessionFileDto>(
                JsonConvert.SerializeObject(dto));

            Assert.NotNull(restored.pendingToolRound);
            Assert.Equal("req-1#0", restored.pendingToolRound.roundToken);
            Assert.Equal(1, restored.pendingToolRound.roundIndex);
            Assert.Single(restored.pendingToolRound.calls);
            Assert.Equal("echo", restored.pendingToolRound.calls[0].Function.Name);
            Assert.Equal(3, restored.pendingToolRound.messages.Count);
            Assert.Equal("assistant", restored.pendingToolRound.messages[2].Role);
            Assert.Single(restored.pendingToolRound.messages[2].ToolCalls);
            Assert.Single(restored.pendingToolRound.schemas);
        }
    }
}
