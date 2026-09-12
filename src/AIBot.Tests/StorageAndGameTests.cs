using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIBot.Core.Config;
using AIBot.Server;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>存储模式选项与 Game 管理逻辑（对应 /api/admin/storage 与 /api/games 接口背后的行为）。</summary>
    public class StorageAndGameTests : IDisposable
    {
        public StorageAndGameTests()
        {
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", null);
            Environment.SetEnvironmentVariable("AIBOT_MONGO_CONNECTION_STRING", null);
            Environment.SetEnvironmentVariable("AIBOT_MONGO_DATABASE", null);
            Environment.SetEnvironmentVariable("AIBOT_MONGO_AUTOMIGRATE", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", null);
            Environment.SetEnvironmentVariable("AIBOT_MONGO_CONNECTION_STRING", null);
            Environment.SetEnvironmentVariable("AIBOT_MONGO_DATABASE", null);
            Environment.SetEnvironmentVariable("AIBOT_MONGO_AUTOMIGRATE", null);
        }

        [Fact]
        public void StorageOptions_DefaultsToJson()
        {
            StorageOptions options = StorageOptions.From(new ConfigurationBuilder().Build());

            Assert.Equal("Json", options.Provider);
            Assert.False(options.IsMongo);
            Assert.False(options.MongoAutoMigrate);
        }

        [Fact]
        public void StorageOptions_MongoEnvWinsOverAppsettingsFalse()
        {
            // appsettings 显式写 false 时，环境变量仍应生效（不因 ?? 链短路）
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", "Mongo");
            Environment.SetEnvironmentVariable("AIBOT_MONGO_AUTOMIGRATE", "true");
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string> { ["Storage:Mongo:AutoMigrate"] = "false" }).Build();

            StorageOptions options = StorageOptions.From(config);

            Assert.Equal("Mongo", options.Provider);
            Assert.True(options.IsMongo);
            Assert.True(options.MongoAutoMigrate);
        }

        [Fact]
        public void StorageOptions_MongoAutomigrateRequiresExplicitEnable()
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string> { ["Storage:Mongo:AutoMigrate"] = "false" }).Build();

            Assert.False(StorageOptions.From(config).MongoAutoMigrate);

            Environment.SetEnvironmentVariable("AIBOT_MONGO_AUTOMIGRATE", "TRUE");
            Assert.True(StorageOptions.From(config).MongoAutoMigrate);
        }

        [Fact]
        public void StorageOptions_MongoRequiresAutoMigrateAndConnection()
        {
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", "Mongo");
            Environment.SetEnvironmentVariable("AIBOT_MONGO_CONNECTION_STRING", "mongodb://127.0.0.1:27017");
            Environment.SetEnvironmentVariable("AIBOT_MONGO_AUTOMIGRATE", "true");
            Environment.SetEnvironmentVariable("AIBOT_MONGO_DATABASE", "ai_npc");

            StorageOptions missingAutoMigrate = StorageOptions.From(new ConfigurationBuilder().Build());
            missingAutoMigrate.MongoAutoMigrate = false;
            Assert.IsType<InvalidOperationException>(Record.Exception(() => missingAutoMigrate.Validate()));

            StorageOptions missingConnection = StorageOptions.From(new ConfigurationBuilder().Build());
            missingConnection.MongoConnectionString = null;
            Assert.IsType<InvalidOperationException>(Record.Exception(() => missingConnection.Validate()));

            StorageOptions ok = StorageOptions.From(new ConfigurationBuilder().Build());
            Assert.Null(Record.Exception(() => ok.Validate()));
        }

        [Fact]
        public void StorageOptions_MongoDatabaseNameValidation()
        {
            Assert.True(StorageOptions.IsValidMongoDatabaseName("ai_npc"));
            Assert.False(StorageOptions.IsValidMongoDatabaseName(""));
            Assert.False(StorageOptions.IsValidMongoDatabaseName("has space"));
            Assert.False(StorageOptions.IsValidMongoDatabaseName("has/slash"));
            Assert.False(StorageOptions.IsValidMongoDatabaseName("has$dollar"));
            Assert.False(StorageOptions.IsValidMongoDatabaseName(new string('a', 64)));
        }

        [Fact]
        public void StorageOptions_RetiredMySqlProvider_FailsLoudly()
        {
            // MySQL 已下线：旧的 Provider=MySql 不能被静默当作 JSON，否则用户数据会“凭空消失”。
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", "MySql");
            StorageOptions options = StorageOptions.From(new ConfigurationBuilder().Build());

            Assert.False(options.IsMongo);
            Assert.False(options.IsJson);
            Assert.IsType<InvalidOperationException>(Record.Exception(() => options.Validate()));
        }

        [Fact]
        public void StorageOptions_JsonProvider_Validates()
        {
            StorageOptions options = StorageOptions.From(new ConfigurationBuilder().Build());
            Assert.True(options.IsJson);
            Assert.Null(Record.Exception(() => options.Validate()));
        }

        [Fact]
        public void StorageOptions_BlankProviderEnv_DefaultsToJson()
        {
            // stale .env 里的 AIBOT_STORAGE_PROVIDER= 不应被当成未知 Provider 而启动失败。
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", "  ");
            StorageOptions options = StorageOptions.From(new ConfigurationBuilder().Build());

            Assert.Equal("Json", options.Provider);
            Assert.True(options.IsJson);
            Assert.Null(Record.Exception(() => options.Validate()));
        }

        [Fact]
        public void ListGameIds_ContainsRealGames_WithValidIds()
        {
            List<string> ids = DataStore.ListGameIds();

            Assert.Contains("default", ids);
            Assert.All(ids, id => Assert.True(DataStore.IsValidId(id)));
            Assert.Equal(ids, new List<string>(ids).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void SaveNpc_ToNewGame_ImplicitlyCreatesGame_AndTemplateFallsBack()
        {
            string testGame = "zz_aibot_test_game";
            try
            {
                Assert.True(DataStore.SaveNpc(testGame, new AgentConfigDto { npcId = "probe_npc" }));
                Assert.Contains(testGame, DataStore.ListGameIds());
                Assert.NotNull(DataStore.LoadNpc(testGame, "probe_npc"));

                // 新 Game 没有模板文件：LoadTemplate 回退到内置默认模板
                Assert.Equal("new_npc", DataStore.LoadTemplate(testGame).npcId);
            }
            finally
            {
                DataStore.DeleteNpc(testGame, "probe_npc");
                string root = DataStore.FindDataRoot();
                if (root != null)
                    Directory.Delete(Path.Combine(root, "games", testGame), true);
            }
        }
    }
}

