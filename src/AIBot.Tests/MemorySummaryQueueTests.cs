using System;
using Microsoft.Extensions.Configuration;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    public sealed class MemorySummaryQueueTests
    {
        [Fact]
        public void Enqueue_IsIdempotentForSameSession()
        {
            string root = TestRoot();
            try
            {
                MemorySummaryQueue queue = CreateQueue(root);

                Assert.True(queue.Enqueue("game", "npc", "player", "session"));
                Assert.True(queue.Enqueue("game", "npc", "player", "session"));
                Assert.Equal(1, queue.PendingCount);
                Assert.Equal("pending", queue.GetSessionStatus("game", "npc", "player", "session", 0).Status);
            }
            finally { DeleteRoot(root); }
        }

        [Fact]
        public void InvalidatePlayer_RemovesQueuedGenerationAndAllowsNewOne()
        {
            string root = TestRoot();
            try
            {
                MemorySummaryQueue queue = CreateQueue(root);
                Assert.True(queue.Enqueue("game", "npc", "player", "session"));

                queue.InvalidatePlayer("game", "npc", "player");
                Assert.Equal(0, queue.PendingCount);
                Assert.Equal("waiting", queue.GetSessionStatus("game", "npc", "player", "session", 1).Status);

                Assert.True(queue.Enqueue("game", "npc", "player", "session"));
                Assert.Equal(1, queue.PendingCount);
            }
            finally { DeleteRoot(root); }
        }

        [Fact]
        public void InvalidIdentifiers_AreRejectedWithoutScheduling()
        {
            string root = TestRoot();
            try
            {
                MemorySummaryQueue queue = CreateQueue(root);
                Assert.False(queue.Enqueue("../game", "npc", "player", "session"));
                Assert.False(queue.Enqueue("game", "npc", "player", "bad session"));
                Assert.Equal(0, queue.PendingCount);
            }
            finally { DeleteRoot(root); }
        }

        [Fact]
        public void RetryFailures_WithNoFailures_IsNoOp()
        {
            string root = TestRoot();
            try
            {
                MemorySummaryQueue queue = CreateQueue(root);
                Assert.Equal(0, queue.RetryFailures(null, null, null, null, "test"));
                Assert.Empty(queue.FailureSnapshot());
            }
            finally { DeleteRoot(root); }
        }

        private static MemorySummaryQueue CreateQueue(string root)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>(
                        "Memory:SummaryQueueCapacity", "16")
                })
                .Build();
            var repository = new JsonMemoryRepository(() => root);
            return new MemorySummaryQueue(new PlayerMemoryService(repository), config,
                new MemoryAuditService(() => root));
        }

        private static string TestRoot()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "aibot-queue-test-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteRoot(string root)
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
        }
    }
}
