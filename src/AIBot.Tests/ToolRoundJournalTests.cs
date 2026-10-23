using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AIBot.Core.Llm;
using AIBot.Core.Protocol;
using Xunit;

namespace AIBot.Tests
{
    public sealed class ToolRoundJournalTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "aibot-tool-journal-" + Guid.NewGuid().ToString("N"));
        private string JournalPath => Path.Combine(_root, "rounds.json");
        private static List<ToolCallDto> Calls => new List<ToolCallDto>
        {
            new ToolCallDto { Id = "call-1", Function = new FunctionCall { Name = "give_item", Arguments = "{}" } }
        };

        [Fact]
        public async Task RestartReusesResultAndResumeIdWithoutExecutingAgain()
        {
            int executions = 0;
            var journal = new ToolRoundJournal(JournalPath);
            var first = await journal.ExecuteAsync("req#0", Calls, call =>
            {
                executions++;
                return Task.FromResult(new ClientToolResult { Success = true, Message = "item given" });
            });
            var restarted = new ToolRoundJournal(JournalPath);
            Assert.Equal("req#0", restarted.Pending.roundToken);
            var replay = await restarted.ExecuteAsync("req#0", Calls, call => throw new Exception("must not execute"));
            Assert.Equal(first.resumeRequestId, replay.resumeRequestId);
            Assert.Equal("item given", Assert.Single(replay.calls).result.Message);
            Assert.Equal(1, executions);
            restarted.Complete("req#0");
            Assert.Null(new ToolRoundJournal(JournalPath).Pending);
        }

        [Fact]
        public async Task InterruptedSideEffectIsBlockedUntilAuthoritativeResultIsConfirmed()
        {
            var journal = new ToolRoundJournal(JournalPath);
            await Assert.ThrowsAsync<IOException>(() => journal.ExecuteAsync("req#0", Calls,
                call => throw new IOException("process crashed after giving item")));
            var restarted = new ToolRoundJournal(JournalPath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExecuteAsync("req#0", Calls,
                call => throw new Exception("must not execute")));
            restarted.ConfirmResult("req#0", "call-1", new ClientToolResult { Success = true, Message = "verified in inventory" });
            var recovered = await restarted.ExecuteAsync("req#0", Calls, call => throw new Exception("must not execute"));
            Assert.Equal("verified in inventory", recovered.calls[0].result.Message);
        }

        [Fact]
        public async Task CompetingComponentsCannotExecuteTheSameRoundConcurrently()
        {
            var first = new ToolRoundJournal(JournalPath);
            var second = new ToolRoundJournal(JournalPath);
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            Task<JournalToolRound> running = first.ExecuteAsync("req#0", Calls, async call =>
            {
                entered.SetResult(true);
                await release.Task;
                return new ClientToolResult { Success = true };
            });
            await entered.Task;
            try
            {
                await Assert.ThrowsAsync<IOException>(() => second.ExecuteAsync("req#0", Calls,
                    call => throw new Exception("must not execute")));
            }
            finally { release.SetResult(true); }
            await running;
            var reused = await second.ExecuteAsync("req#0", Calls, call => throw new Exception("must not execute"));
            Assert.True(reused.calls[0].result.Success);
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
