using System;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Server;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace AIBot.Tests
{
    public class MongoServerLeaseTests
    {
        [MongoFact]
        public async Task CompetingServerIsRejectedAndGracefulReleaseAllowsRestart()
        {
            var factory = new MongoConnectionFactory(Environment.GetEnvironmentVariable(MongoFactAttribute.EnvVar),
                "ai_npc_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var first = new MongoServerLease(factory);
                await using var second = new MongoServerLease(factory);
                Assert.True(await first.TryAcquireAsync(CancellationToken.None));
                Assert.False(await second.TryAcquireAsync(CancellationToken.None));
                await first.DisposeAsync();
                Assert.True(await second.TryAcquireAsync(CancellationToken.None));
                Assert.True(await second.RenewAsync(CancellationToken.None));
            }
            finally { await factory.Client.DropDatabaseAsync(factory.DatabaseName); }
        }

        [MongoFact]
        public async Task ExpiredOwnerCannotRenewAfterReplacementAcquiresLease()
        {
            var factory = new MongoConnectionFactory(Environment.GetEnvironmentVariable(MongoFactAttribute.EnvVar),
                "ai_npc_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var first = new MongoServerLease(factory);
                await using var second = new MongoServerLease(factory);
                Assert.True(await first.TryAcquireAsync(CancellationToken.None));
                await factory.Collection<BsonDocument>(MongoInitializer.MetaCollection).UpdateOneAsync(
                    new BsonDocument("_id", MongoServerLease.LeaseId),
                    Builders<BsonDocument>.Update.Set("expiresUtc", DateTime.UtcNow.AddMinutes(-1)));
                Assert.True(await second.TryAcquireAsync(CancellationToken.None));
                Assert.False(await first.RenewAsync(CancellationToken.None));
                Assert.True(await second.RenewAsync(CancellationToken.None));
            }
            finally { await factory.Client.DropDatabaseAsync(factory.DatabaseName); }
        }
    }
}
