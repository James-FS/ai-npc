using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Server;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>
    /// MongoInitializer 行为级用例（方案 §13）：幂等、TTL 变更重建、必需索引齐全。
    /// opt-in：需 AIBOT_MONGO_TEST_CONNECTION。
    /// </summary>
    [Collection("mongo-integration")]
    public class MongoInitializerTests
    {
        private static MongoConnectionFactory NewFactory()
        {
            return new MongoConnectionFactory(
                Environment.GetEnvironmentVariable(MongoFactAttribute.EnvVar) ?? "mongodb://127.0.0.1:27017",
                "ai_npc_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static IConfiguration ConfigWith(int chatDays, int auditDays)
        {
            return new ConfigurationBuilder().AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("Logging:ChatRetentionDays", chatDays.ToString()),
                new System.Collections.Generic.KeyValuePair<string, string>("Logging:AuditRetentionDays", auditDays.ToString())
            }).Build();
        }

        private static async Task<List<string>> IndexNamesAsync(MongoConnectionFactory factory, string collection)
        {
            var names = new List<string>();
            using (IAsyncCursor<BsonDocument> cursor = await factory.Database
                .GetCollection<BsonDocument>(collection).Indexes.ListAsync())
            {
                foreach (BsonDocument index in await cursor.ToListAsync())
                    names.Add(index["name"].AsString);
            }
            return names;
        }

        private static async Task<int?> TtlSecondsAsync(MongoConnectionFactory factory, string collection)
        {
            using (IAsyncCursor<BsonDocument> cursor = await factory.Database
                .GetCollection<BsonDocument>(collection).Indexes.ListAsync())
            {
                foreach (BsonDocument index in await cursor.ToListAsync())
                {
                    if (index["name"].AsString == "ttl_ts" && index.Contains("expireAfterSeconds"))
                        return index["expireAfterSeconds"].ToInt32();
                }
            }
            return null;
        }

        [MongoFact]
        public async Task ApplyAsync_Twice_IsIdempotent_AndCreatesRequiredIndexes()
        {
            MongoConnectionFactory factory = NewFactory();
            await MongoInitializer.ApplyAsync(factory, ConfigWith(30, 365), CancellationToken.None);
            await MongoInitializer.ApplyAsync(factory, ConfigWith(30, 365), CancellationToken.None);

            List<string> names = await IndexNamesAsync(factory, "sessions");
            Assert.Contains("gameId_1_npcId_1_playerKey_1_lastActiveUtc_-1", names);
            Assert.Contains("hasPendingMemory_1_playerKey_1", names);
            Assert.Contains("lastActiveUtc_1", names);

            names = await IndexNamesAsync(factory, "player_memories");
            Assert.Contains("gameId_1_updatedUtc_-1", names);
            Assert.Contains("gameId_1_npcId_1_playerId_1", names);

            // 幂等：重复执行后 TTL 仍为首次配置值
            Assert.Equal(30 * 86400, await TtlSecondsAsync(factory, "chat_logs"));
            Assert.Equal(365 * 86400, await TtlSecondsAsync(factory, "memory_audits"));
        }

        [MongoFact]
        public async Task ApplyAsync_RetentionChanged_RebuildsTtlIndex()
        {
            MongoConnectionFactory factory = NewFactory();
            await MongoInitializer.ApplyAsync(factory, ConfigWith(30, 365), CancellationToken.None);
            Assert.Equal(30 * 86400, await TtlSecondsAsync(factory, "chat_logs"));

            // expireAfterSeconds 不能原地改：配置变化后应 drop 重建为新秒数
            await MongoInitializer.ApplyAsync(factory, ConfigWith(7, 365), CancellationToken.None);
            Assert.Equal(7 * 86400, await TtlSecondsAsync(factory, "chat_logs"));
            Assert.Equal(365 * 86400, await TtlSecondsAsync(factory, "memory_audits"));
        }
    }
}
