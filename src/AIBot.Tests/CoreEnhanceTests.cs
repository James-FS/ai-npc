using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using AIBot.Core.Output;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>R3 增强：reasoning 采集 / 截断 JSON 挽救 / token 校准 / 摘要记忆。</summary>
    public class CoreEnhanceTests
    {
        // ---- reasoning 采集 ----
        public sealed class ReasoningRecorder : ILlmStreamSink, IReasoningSink
        {
            public readonly System.Text.StringBuilder Reasoning = new System.Text.StringBuilder();
            public readonly System.Text.StringBuilder Tokens = new System.Text.StringBuilder();
            public string Completed;
            public void OnReasoningToken(string delta) { Reasoning.Append(delta); }
            public void OnToken(string delta) { Tokens.Append(delta); }
            public void OnToolCall(ToolCallDto call) { }
            public void OnCompleted(string fullText, Usage usage) { Completed = fullText; }
            public void OnError(System.Exception ex) { }
        }

        [Fact]
        public void ReasoningContent_IsForwardedToReasoningSink()
        {
            var sink = new ReasoningRecorder();
            var aggregator = new OpenAiStreamAggregator(sink);
            string reasoningChunk = new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["index"] = 0,
                    ["delta"] = new JObject { ["reasoning_content"] = "先想想…" }
                })
            }.ToString(Formatting.None);
            new SseLineParser(aggregator.HandleDataLine).Feed(Sse.Round(reasoningChunk, Sse.Token("好")));

            Assert.Equal("先想想…", sink.Reasoning.ToString());
            Assert.Equal("好", sink.Tokens.ToString());       // 正文与思考分离
        }

        [Fact]
        public void ReasoningSink_NotImplemented_IsIgnored()
        {
            var sink = new RecordingSink();                    // 未实现 IReasoningSink
            var aggregator = new OpenAiStreamAggregator(sink);
            string reasoningChunk = new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["index"] = 0,
                    ["delta"] = new JObject { ["reasoning_content"] = "思考" }
                })
            }.ToString(Formatting.None);
            new SseLineParser(aggregator.HandleDataLine).Feed(Sse.Round(reasoningChunk, Sse.Token("答")));

            Assert.Equal("答", sink.CompletedText);            // 不抛异常，正文正常
        }

        // ---- 截断 JSON 挽救 ----
        [Fact]
        public void TruncatedJson_SayIsSalvaged()
        {
            string truncated = "{\"say\":\"断了的一句话\",\"emotion\":\"hap";
            bool ok = StructuredReplyParser.TryParse(truncated, new OutputSettings(), out var reply);
            Assert.True(ok);
            Assert.Equal("断了的一句话", reply.say);
            Assert.Equal("neutral", reply.emotion);            // 挽救路径用默认枚举
        }

        [Fact]
        public void NoSayField_StillFails()
        {
            Assert.False(StructuredReplyParser.TryParse("{\"emotion\":\"happy", new OutputSettings(), out _));
        }

        // ---- token 校准 ----
        [Fact]
        public void Calibration_UpdatesAndClamps()
        {
            string npc = "calib_test_" + System.Guid.NewGuid().ToString("N").Substring(0, 6);
            Assert.Equal(1.0, TokenBudget.Calibration.Factor(npc));       // 初始 1.0
            TokenBudget.Calibration.Update(npc, 2000, 1000);               // 实际是估算的2倍
            Assert.Equal(2.0, TokenBudget.Calibration.Factor(npc));
            TokenBudget.Calibration.Update(npc, 100, 10000);               // 夹到下限 0.3 → 但0.01→0.3
            Assert.Equal(0.3, TokenBudget.Calibration.Factor(npc), 3);
        }

        // ---- 摘要记忆（AgentLoop 集成）----
        [Fact]
        public async Task EvictedMessages_TriggersSummarization()
        {
            var cfg = new AgentConfigDto
            {
                npcId = "sum_test",
                displayName = "测试员",
                persona = "严谨",
                fallbackReplies = { "（兜底）" }
            };
            cfg.memory.shortTermTurns = 2;          // 窗口极小：一问一答就把旧消息挤出去
            cfg.memory.summaryThreshold = 1;        // 淘汰1条即触发摘要

            var memory = new ShortTermMemory(2);
            memory.Add(LlmMessage.User("旧问题一"));
            memory.Add(LlmMessage.Assistant("旧回答一"));

            string mainReply = "{\"say\":\"新回答\",\"emotion\":\"neutral\",\"action\":\"idle\"}";
            string summaryReply = "{\"summary\":\"玩家问过旧问题\",\"facts\":[\"玩家名叫小明\"]}";
            var backend = new MockLlmBackend(
                Sse.Round(Sse.Token(mainReply)),
                Sse.Round(Sse.Token(summaryReply)));           // 第二轮 = 摘要请求
            var loop = new AgentLoop(backend);

            AgentLoopResult result = await loop.RunAsync(new AgentRunInput
            {
                Config = cfg,
                World = new WorldConfigDto(),
                Game = new SimGameContext(new SimGameState()),
                UserMessage = "新问题",
                Memory = memory
            }, new RecordingSink(), CancellationToken.None);

            // 主对话正常
            Assert.False(result.UsedFallback);
            Assert.Equal("新回答", result.Reply.say);
            // 摘要已触发并产出
            Assert.Equal("玩家问过旧问题", result.MemorySummary);
            Assert.Contains("玩家名叫小明", result.MemoryFacts);
            // 摘要请求特征：无工具、低温度、json_object、system 为记忆整理器
            var summaryRequest = backend.Requests[1];
            Assert.Null(summaryRequest.Tools);
            Assert.Equal(0.3f, summaryRequest.Temperature);
            Assert.NotNull(summaryRequest.ResponseFormat);
            Assert.Contains("记忆整理器", summaryRequest.Messages[0].Content);
            Assert.Contains("旧问题一", summaryRequest.Messages[1].Content);
        }

        [Fact]
        public async Task BelowThreshold_NoSummarization()
        {
            var cfg = new AgentConfigDto { npcId = "sum_test2", displayName = "t", persona = "p" };
            cfg.memory.summaryThreshold = 100;      // 永不触发
            var backend = new MockLlmBackend(
                Sse.Round(Sse.Token("{\"say\":\"答\",\"emotion\":\"neutral\",\"action\":\"idle\"}")));
            var loop = new AgentLoop(backend);

            AgentLoopResult result = await loop.RunAsync(new AgentRunInput
            {
                Config = cfg,
                World = new WorldConfigDto(),
                Game = new SimGameContext(new SimGameState()),
                UserMessage = "问",
                Memory = new ShortTermMemory(12)
            }, new RecordingSink(), CancellationToken.None);

            Assert.Single(backend.Requests);                    // 只有主对话一轮
            Assert.Null(result.MemorySummary);
        }
    }
}
