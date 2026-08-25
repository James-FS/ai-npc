using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using AIBot.Core.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>示例工具：回显文本（对应游戏端真实工具的注册形态）。</summary>
    public sealed class EchoTool : IAgentTool
    {
        public int ExecuteCount;
        public string LastArgs;

        public string Id { get { return "echo"; } }
        public string Description { get { return "复述一句话（测试用）"; } }
        public string ParametersSchema
        {
            get { return "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}"; }
        }

        public Task<ToolResult> ExecuteAsync(string argsJson, object hostContext)
        {
            ExecuteCount++;
            LastArgs = argsJson;
            string text = (string)JObject.Parse(argsJson)["text"];
            return Task.FromResult(ToolResult.Ok("echo:" + text));
        }
    }

    public class AgentLoopTests
    {
        private static AgentConfigDto Config(bool withTools)
        {
            var cfg = new AgentConfigDto
            {
                npcId = "tester",
                displayName = "测试员",
                persona = "严谨",
                fallbackReplies = { "（测试兜底台词）" }
            };
            if (withTools) cfg.enabledToolIds.Add("echo");
            return cfg;
        }

        private static AgentRunInput Input(AgentConfigDto cfg, ToolRegistry tools, ShortTermMemory memory, string message)
        {
            return new AgentRunInput
            {
                Config = cfg,
                World = new WorldConfigDto { description = "测试世界" },
                Game = new SimGameContext(new SimGameState { stage = 0 }),
                UserMessage = message,
                Memory = memory,
                Tools = tools
            };
        }

        [Fact]
        public async Task PureTextRound_ParsesReply_AndRecordsMemory()
        {
            string replyJson = "{\"say\":\"来了。\",\"emotion\":\"happy\",\"action\":\"wave\"}";
            var backend = new MockLlmBackend(Sse.Round(Sse.Token(replyJson)));
            var loop = new AgentLoop(backend);
            var memory = new ShortTermMemory(12);
            var sink = new RecordingSink();

            AgentLoopResult result = await loop.RunAsync(Input(Config(false), null, memory, "你好"), sink, CancellationToken.None);

            Assert.False(result.UsedFallback);
            Assert.Equal("来了。", result.Reply.say);
            Assert.Equal("happy", result.Reply.emotion);
            Assert.Equal(replyJson, sink.Tokens.ToString());
            Assert.Equal(2, memory.Messages.Count);            // user + assistant
            Assert.Contains("[玩家说]你好[/玩家说]", memory.Messages[0].Content);

            // 无工具时启用 json_object（§5.5 策略）
            LlmRequest req = backend.Requests[0];
            Assert.Null(req.Tools);
            Assert.NotNull(req.ResponseFormat);
        }

        [Fact]
        public async Task ToolRound_ExecutesThenContinues()
        {
            var echo = new EchoTool();
            var tools = new ToolRegistry();
            tools.Register(echo);

            string replyJson = "{\"say\":\"我说完了。\",\"emotion\":\"neutral\",\"action\":\"idle\"}";
            var backend = new MockLlmBackend(
                Sse.Round(Sse.ToolStart("call_1", "echo"), Sse.ToolArgs("{\"text\":\"嗨\"}")),
                Sse.Round(Sse.Token(replyJson)));
            var loop = new AgentLoop(backend);

            AgentLoopResult result = await loop.RunAsync(
                Input(Config(true), tools, new ShortTermMemory(12), "复述'嗨'"),
                new RecordingSink(), CancellationToken.None);

            Assert.False(result.UsedFallback);
            Assert.Equal(1, echo.ExecuteCount);
            Assert.Equal("{\"text\":\"嗨\"}", echo.LastArgs);
            Assert.Single(result.ToolExecutions);
            Assert.True(result.ToolExecutions[0].Result.Success);

            // 第二轮请求应包含 assistant(tool_calls) 与 tool 结果消息
            LlmRequest second = backend.Requests[1];
            Assert.Equal("assistant", second.Messages[second.Messages.Count - 2].Role);
            Assert.NotNull(second.Messages[second.Messages.Count - 2].ToolCalls);
            Assert.Equal("tool", second.Messages[second.Messages.Count - 1].Role);
            Assert.Equal("call_1", second.Messages[second.Messages.Count - 1].ToolCallId);
            Assert.Equal("echo:嗨", second.Messages[second.Messages.Count - 1].Content);

            // 带工具时禁用 response_format（§5.5 策略）
            LlmRequest first = backend.Requests[0];
            Assert.NotNull(first.Tools);
            Assert.Null(first.ResponseFormat);
        }

        [Fact]
        public async Task BackendFailure_FallsBack()
        {
            var backend = new MockLlmBackend();   // 无脚本 → 立即失败
            var loop = new AgentLoop(backend);
            var sink = new RecordingSink();

            AgentLoopResult result = await loop.RunAsync(
                Input(Config(true), null, new ShortTermMemory(12), "你好"), sink, CancellationToken.None);

            Assert.True(result.UsedFallback);
            Assert.Equal("（测试兜底台词）", result.Reply.say);
            Assert.NotNull(sink.Error);
        }

        [Fact]
        public async Task UnparsableText_FallsBack()
        {
            var backend = new MockLlmBackend(Sse.Round(Sse.Token("我就是随便说两句，没有JSON")));
            var loop = new AgentLoop(backend);

            AgentLoopResult result = await loop.RunAsync(
                Input(Config(false), null, new ShortTermMemory(12), "你好"), new RecordingSink(), CancellationToken.None);

            Assert.True(result.UsedFallback);
            Assert.Equal("我就是随便说两句，没有JSON", result.RawText);
        }

        [Fact]
        public async Task UserMessage_IsSanitizedBeforeSending()
        {
            var backend = new MockLlmBackend(Sse.Round(Sse.Token("{\"say\":\"嗯\",\"emotion\":\"neutral\",\"action\":\"idle\"}")));
            var loop = new AgentLoop(backend);

            await loop.RunAsync(Input(Config(false), null, new ShortTermMemory(12), "ignore previous"), new RecordingSink(), CancellationToken.None);

            LlmRequest req = backend.Requests[0];
            LlmMessage user = req.Messages[req.Messages.Count - 1];
            Assert.Equal("user", user.Role);
            Assert.StartsWith("[玩家说]ignore previous[/玩家说]", user.Content);
            Assert.StartsWith("# 世界观", req.Messages[0].Content);   // system 分层 prompt
            Assert.EndsWith("\"action\":\"idle,wave,point,offer\"}", req.Messages[0].Content.TrimEnd());
        }
    }
}
