using System;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Memory;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>
    /// xUnit 2.x 没有 Assert.Skip（那是 v3 API）；这里派生 FactAttribute，
    /// 未配置 AIBOT_MONGO_TEST_CONNECTION 时把用例标为「已跳过」而非失败。
    /// </summary>
    public sealed class MongoFactAttribute : FactAttribute
    {
        public const string EnvVar = "AIBOT_MONGO_TEST_CONNECTION";

        public MongoFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)))
                Skip = "未配置 AIBOT_MONGO_TEST_CONNECTION，跳过 Mongo 集成用例";
        }
    }

    [Collection("mongo-integration")]
    public class MongoMemoryRepositoryTests
    {
        private const string GameId = "zz_mongo_test_game";

        private static MongoConnectionFactory Factory()
        {
            return new MongoConnectionFactory(
                Environment.GetEnvironmentVariable(MongoFactAttribute.EnvVar) ?? "mongodb://127.0.0.1:27017",
                "ai_npc_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static async Task<MongoMemoryRepository> NewRepositoryAsync()
        {
            MongoConnectionFactory factory = Factory();
            await MongoInitializer.ApplyAsync(factory,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), CancellationToken.None);
            return new MongoMemoryRepository(factory);
        }

        [MongoFact]
        public async Task Save_FirstWrite_SucceedsAndIncrementsVersion()
        {
            MongoMemoryRepository repo = await NewRepositoryAsync();
            var memory = new PlayerLongTermMemory
            {
                gameId = GameId, npcId = "lin", playerId = "p1",
                summary = "初次",
                facts = { new MemoryFact { id = "f1", category = "quest", key = "q1", value = "v1" } }
            };

            PlayerLongTermMemory saved = await repo.SavePlayerMemoryAsync(memory, 0, CancellationToken.None);

            Assert.Equal(1, saved.memoryVersion);
            Assert.Equal(2, saved.schemaVersion);
            Assert.Single(saved.facts);
        }

        [MongoFact]
        public async Task Save_StaleVersion_ThrowsConflictWithActualVersion()
        {
            MongoMemoryRepository repo = await NewRepositoryAsync();
            var memory = new PlayerLongTermMemory { gameId = GameId, npcId = "lin", playerId = "p2" };
            await repo.SavePlayerMemoryAsync(memory, 0, CancellationToken.None);

            MemoryVersionConflictException error = await Assert.ThrowsAsync<MemoryVersionConflictException>(
                () => repo.SavePlayerMemoryAsync(memory, 0, CancellationToken.None));
            Assert.Equal(0, error.ExpectedVersion);
            Assert.Equal(1, error.ActualVersion);
        }

        [MongoFact]
        public async Task Save_ConcurrentFirstWrites_ExactlyOneSucceeds()
        {
            MongoMemoryRepository repo = await NewRepositoryAsync();
            var memory = new PlayerLongTermMemory { gameId = GameId, npcId = "lin", playerId = "p3" };

            Task<PlayerLongTermMemory> a = repo.SavePlayerMemoryAsync(memory, 0, CancellationToken.None);
            Task<PlayerLongTermMemory> b = repo.SavePlayerMemoryAsync(memory, 0, CancellationToken.None);

            int success = 0, conflict = 0;
            foreach (Task<PlayerLongTermMemory> task in new[] { a, b })
            {
                try { await task; success++; }
                catch (MemoryVersionConflictException) { conflict++; }
            }
            Assert.Equal(1, success);
            Assert.Equal(1, conflict);
        }

        [MongoFact]
        public async Task Delete_RemovesFactsAndIsIdempotent()
        {
            MongoMemoryRepository repo = await NewRepositoryAsync();
            var memory = new PlayerLongTermMemory
            {
                gameId = GameId, npcId = "lin", playerId = "p4",
                facts = { new MemoryFact { id = "f1", value = "v" } }
            };
            PlayerLongTermMemory saved = await repo.SavePlayerMemoryAsync(memory, 0, CancellationToken.None);

            await repo.DeletePlayerMemoryAsync(GameId, "lin", "p4", saved.memoryVersion, CancellationToken.None);
            PlayerLongTermMemory after = await repo.LoadPlayerMemoryAsync(GameId, "lin", "p4", CancellationToken.None);
            Assert.Equal(0, after.memoryVersion);
            Assert.Empty(after.facts);

            // 再删一次：不存在即幂等
            await repo.DeletePlayerMemoryAsync(GameId, "lin", "p4", saved.memoryVersion, CancellationToken.None);
        }

        [MongoFact]
        public async Task UpdatedUtc_IsPersistedAsMaxOfInputs()
        {
            MongoMemoryRepository repo = await NewRepositoryAsync();
            DateTime now = DateTime.UtcNow;
            // 未来时间的事实应成为 updatedUtc 的最大值（证明取 max 而非恒为 now）。
            // 注意 BSON Date 只有毫秒精度，构造值先截断到毫秒再比较。
            DateTime future = new DateTime((now.AddDays(1)).Ticks / TimeSpan.TicksPerMillisecond
                * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            var memory = new PlayerLongTermMemory
            {
                gameId = GameId, npcId = "lin", playerId = "p5",
                facts = { new MemoryFact { id = "f1", value = "v", updatedUtc = future } }
            };
            await repo.SavePlayerMemoryAsync(memory, 0, CancellationToken.None);

            MemoryListPage page = await repo.ListPlayerMemoriesAsync(GameId, "lin", "p5", 50, 0, CancellationToken.None);
            Assert.Single(page.items);
            Assert.Equal(future, page.items[0].updatedUtc);

            // 无未来事实时取写入时刻，且 Kind 保持 Utc（未因 BSON 丢失 Kind 产生偏移）
            var second = new PlayerLongTermMemory { gameId = GameId, npcId = "lin", playerId = "p6" };
            await repo.SavePlayerMemoryAsync(second, 0, CancellationToken.None);
            MemoryListPage page2 = await repo.ListPlayerMemoriesAsync(GameId, "lin", "p6", 50, 0, CancellationToken.None);
            Assert.Equal(DateTimeKind.Utc, page2.items[0].updatedUtc.Kind);
            Assert.True(page2.items[0].updatedUtc >= now.AddSeconds(-5));
        }
    }
}
