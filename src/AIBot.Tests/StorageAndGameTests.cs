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
            Environment.SetEnvironmentVariable("AIBOT_MYSQL_CONNECTION_STRING", null);
            Environment.SetEnvironmentVariable("AIBOT_MYSQL_AUTOMIGRATE", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", null);
            Environment.SetEnvironmentVariable("AIBOT_MYSQL_CONNECTION_STRING", null);
            Environment.SetEnvironmentVariable("AIBOT_MYSQL_AUTOMIGRATE", null);
        }

        [Fact]
        public void StorageOptions_DefaultsToJson()
        {
            StorageOptions options = StorageOptions.From(new ConfigurationBuilder().Build());

            Assert.Equal("Json", options.Provider);
            Assert.False(options.IsMySql);
            Assert.False(options.AutoMigrate);
        }

        [Fact]
        public void StorageOptions_EnvVarsWinOverAppsettingsFalse()
        {
            // appsettings 显式写 false 时，环境变量仍应生效（不因 ?? 链短路）
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", "MySql");
            Environment.SetEnvironmentVariable("AIBOT_MYSQL_AUTOMIGRATE", "true");
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string> { ["Storage:MySql:AutoMigrate"] = "false" }).Build();

            StorageOptions options = StorageOptions.From(config);

            Assert.Equal("MySql", options.Provider);
            Assert.True(options.AutoMigrate);
        }

        [Fact]
        public void StorageOptions_AutomigrateRequiresExplicitEnable()
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string> { ["Storage:MySql:AutoMigrate"] = "false" }).Build();

            Assert.False(StorageOptions.From(config).AutoMigrate);

            Environment.SetEnvironmentVariable("AIBOT_MYSQL_AUTOMIGRATE", "TRUE");
            Assert.True(StorageOptions.From(config).AutoMigrate);
        }

        [Fact]
        public void StorageOptions_MissingMySqlConnectionString_FailsValidate()
        {
            Environment.SetEnvironmentVariable("AIBOT_STORAGE_PROVIDER", "MySql");
            Environment.SetEnvironmentVariable("AIBOT_MYSQL_CONNECTION_STRING", null);

            StorageOptions options = StorageOptions.From(new ConfigurationBuilder().Build());

            Exception caught = Record.Exception(() => options.Validate());
            Assert.IsType<InvalidOperationException>(caught);
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

