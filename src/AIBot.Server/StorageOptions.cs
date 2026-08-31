using System;
using Microsoft.Extensions.Configuration;

namespace AIBot.Server
{
    /// <summary>Server 持久化选择。默认 JSON，设置 Storage:Provider=MySql 后启用 Dapper。</summary>
    public sealed class StorageOptions
    {
        public string Provider { get; set; } = "Json";
        public string MySqlConnectionString { get; set; }
        public bool AutoMigrate { get; set; }

        public bool IsMySql
        {
            get { return string.Equals(Provider, "mysql", StringComparison.OrdinalIgnoreCase); }
        }

        public static StorageOptions From(IConfiguration configuration)
        {
            string provider = Environment.GetEnvironmentVariable("AIBOT_STORAGE_PROVIDER")
                ?? configuration["Storage:Provider"] ?? "Json";
            string connection = Environment.GetEnvironmentVariable("AIBOT_MYSQL_CONNECTION_STRING")
                ?? configuration["Storage:MySql:ConnectionString"]
                ?? configuration["Storage:ConnectionString"];
            // 显式设置 AIBOT_MYSQL_AUTOMIGRATE 时优先于 appsettings（后者默认 false，会短路 ?? 链）
            string autoMigrateEnv = Environment.GetEnvironmentVariable("AIBOT_MYSQL_AUTOMIGRATE");
            bool autoMigrate = autoMigrateEnv != null
                ? string.Equals(autoMigrateEnv, "true", StringComparison.OrdinalIgnoreCase)
                : (configuration.GetValue<bool?>("Storage:MySql:AutoMigrate")
                    ?? configuration.GetValue<bool?>("Storage:AutoMigrate") ?? false);
            return new StorageOptions
            {
                Provider = provider,
                MySqlConnectionString = connection,
                AutoMigrate = autoMigrate
            };
        }

        public void Validate()
        {
            if (!IsMySql) return;
            if (string.IsNullOrWhiteSpace(MySqlConnectionString))
                throw new InvalidOperationException("Storage=MySql 时必须配置 Storage:MySql:ConnectionString 或 AIBOT_MYSQL_CONNECTION_STRING");
        }
    }
}

