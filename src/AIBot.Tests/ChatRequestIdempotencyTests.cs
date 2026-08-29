using System;
using System.Collections.Generic;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    public class ChatRequestIdempotencyTests
    {
        [Fact]
        public void BeginRequest_ReusesSameRequestId()
        {
            var session = new SessionState();
            ChatRequestRecord first = SessionStore.BeginRequest(session, "req-1", "hash-a");
            ChatRequestRecord second = SessionStore.BeginRequest(session, "req-1", "hash-a");

            Assert.Same(first, second);
            Assert.Single(session.RecentRequests);
            Assert.Equal(ChatRequestStatuses.Processing, first.status);
        }

        [Fact]
        public void CompleteRequest_CachesReplayEvents()
        {
            var session = new SessionState();
            ChatRequestRecord record = SessionStore.BeginRequest(session, "req-2", "hash-b");

            SessionStore.CompleteRequest(record, new List<string> { "data: one\n\n", "data: two\n\n" });

            Assert.Equal(ChatRequestStatuses.Completed, record.status);
            Assert.Equal(2, record.events.Count);
            Assert.NotEqual(default(DateTime), record.completedUtc);
        }

        [Fact]
        public void BeginRequest_PrunesOldCompletedEntries()
        {
            var session = new SessionState();
            for (int i = 0; i < 25; i++)
            {
                ChatRequestRecord record = SessionStore.BeginRequest(session, "req-" + i, "hash-" + i);
                SessionStore.CompleteRequest(record, Array.Empty<string>());
            }

            Assert.Equal(20, session.RecentRequests.Count);
            Assert.Null(SessionStore.FindRequest(session, "req-0"));
            Assert.NotNull(SessionStore.FindRequest(session, "req-24"));
        }
    }
}
