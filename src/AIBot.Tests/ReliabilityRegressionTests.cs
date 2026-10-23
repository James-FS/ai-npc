using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core;
using AIBot.Core.Config;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using AIBot.Core.Tools;
using Xunit;

namespace AIBot.Tests
{
    public class ReliabilityRegressionTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ModelCannotExecuteOrDeferRegisteredButDisabledTool(bool defer)
        {
            var tool = new EchoTool();
            var registry = new ToolRegistry();
            registry.Register(tool);
            var backend = new MockLlmBackend(
                Sse.Round(Sse.ToolStart("forbidden", "echo"), Sse.ToolArgs("{}")),
                Sse.Round(Sse.Token("{\"say\":\"拒绝了\"}")));
            var result = await new AgentLoop(backend).RunAsync(new AgentRunInput
            {
                Config = new AgentConfigDto { npcId = "tester" },
                UserMessage = "hello", Tools = registry, DeferToolsToHost = defer,
                DeferredTools = defer ? new List<ToolSchema>() : null
            }, new RecordingSink(), CancellationToken.None);

            Assert.Equal(0, tool.ExecuteCount);
            Assert.Null(result.PendingToolCalls);
            Assert.False(Assert.Single(result.ToolExecutions).Result.Success);
            Assert.Contains("not enabled", backend.Requests[1].Messages[3].Content);
        }

        [Theory]
        [InlineData("invalid JSON")]
        [InlineData("{\"summary\":\"partial\",\"facts\":[")]
        [InlineData("{\"summary\":\"partial\",\"facts\":\"invalid\"}")]
        public async Task InvalidSynchronousSummaryRestoresBatchAndCanBeRetried(string invalidSummary)
        {
            var memory = new ShortTermMemory(2);
            memory.Add(LlmMessage.User("older player message"));
            memory.Add(LlmMessage.Assistant("older NPC reply"));
            var policy = MemoryPolicy.Defaults();
            policy.summaryThreshold = 2;
            var input = new AgentRunInput
            {
                Config = new AgentConfigDto { npcId = "tester" }, UserMessage = "hello",
                Memory = memory, MemorySummary = "old summary", ResolvedMemoryPolicy = policy
            };
            var backend = new MockLlmBackend(
                Sse.Round(Sse.Token("{\"say\":\"你好\"}")), Sse.Round(Sse.Token(invalidSummary)),
                Sse.Round(Sse.Token("{\"say\":\"又见面了\"}")),
                Sse.Round(Sse.Token("{\"summary\":\"new summary\",\"facts\":[]}")));
            var loop = new AgentLoop(backend);
            var failed = await loop.RunAsync(input, new RecordingSink(), CancellationToken.None);
            Assert.False(failed.UsedFallback);
            Assert.Null(failed.MemorySummary);
            Assert.Equal(2, memory.EvictedCount);
            Assert.Equal("older player message", memory.SnapshotEvicted()[0].Content);

            var retried = await loop.RunAsync(input, new RecordingSink(), CancellationToken.None);
            Assert.Equal("new summary", retried.MemorySummary);
            Assert.Equal(4, retried.MemorySummarizedMessages.Count);
            Assert.Equal(0, memory.EvictedCount);
        }
    }
}
