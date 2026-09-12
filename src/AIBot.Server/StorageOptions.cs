using System;
using Microsoft.Extensions.Configuration;

namespace AIBot.Server
{
    /// <summary>Server 持久化选择。默认 JSON（开发/单机）；Provider=Mongo 时使用 MongoDB（正式）。</summary>
    public sealed class StorageOptions
    {
        public string Provider { get; set; } = "Json";

        public string MongoConnectionString { get; set; }
        public string MongoDatabase { get; set; }
        public bool MongoAutoMigrate { get; set; }

        public bool IsMongo
        {
            get { return string.Equals(Provider, "mongo", StringComparison.OrdinalIgnoreCase); }
        }

        public static StorageOptions From(IConfiguration configuration)
        {
            // 空/空白视为未设置（避免 stale .env 里的 AIBOT_STORAGE_PROVIDER= 触发误判）。
            string providerEnv = Environment.GetEnvironmentVariable("AIBOT_STORAGE_PROVIDER");
            string provider = !string.IsNullOrWhiteSpace(providerEnv) ? providerEnv.Trim()
                : (!string.IsNullOrWhiteSpace(configuration["Storage:Provider"]) ? configuration["Storage:Provider"].Trim() : "Json");
            string mongoConnection = Environment.GetEnvironmentVariable("AIBOT_MONGO_CONNECTION_STRING")
                ?? configuration["Storage:Mongo:ConnectionString"];
            string mongoDatabase = Environment.GetEnvironmentVariable("AIBOT_MONGO_DATABASE")
                ?? configuration["Storage:Mongo:Database"] ?? "ai_npc";
            // 显式设置 AIBOT_MONGO_AUTOMIGRATE 时优先于 appsettings（后者默认 false，会短路 ?? 链）
            string mongoAutoMigrateEnv = Environment.GetEnvironmentVariable("AIBOT_MONGO_AUTOMIGRATE");
            bool mongoAutoMigrate = mongoAutoMigrateEnv != null
                ? string.Equals(mongoAutoMigrateEnv, "true", StringComparison.OrdinalIgnoreCase)
                : (configuration.GetValue<bool?>("Storage:Mongo:AutoMigrate") ?? false);

            return new StorageOptions
            {
                Provider = provider,
                MongoConnectionString = mongoConnection,
                MongoDatabase = mongoDatabase,
                MongoAutoMigrate = mongoAutoMigrate
            };
        }

        public void Validate()
        {
            if (!IsJson && !IsMongo)
                throw new InvalidOperationException(
                    "Storage:Provider 只支持 Json 或 Mongo（当前值: " + Provider + "）。MySQL 后端已下线，请改用 Mongo。");
            if (!IsMongo) return;

            if (string.IsNullOrWhiteSpace(MongoConnectionString))
                throw new InvalidOperationException("Storage=Mongo 时必须配置 Storage:Mongo:ConnectionString 或 AIBOT_MONGO_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(MongoDatabase))
                throw new InvalidOperationException("Storage=Mongo 时必须配置 Storage:Mongo:Database 或 AIBOT_MONGO_DATABASE");
            if (!IsValidMongoDatabaseName(MongoDatabase))
                throw new InvalidOperationException("MongoDB 库名非法：不得包含 / \\ . \" $ * < > : | ? 或空格，长度需在 1~63 之间");
            // Mongo 集合惰性创建；不初始化就没有集合，就绪探针会恒 503。此约束避免“连得上但永远不就绪”。
            if (!MongoAutoMigrate)
                throw new InvalidOperationException("Storage=Mongo 需要 Storage:Mongo:AutoMigrate=true（或 AIBOT_MONGO_AUTOMIGRATE=true）以创建集合与索引");
        }

        /// <summary>Provider=Json（忽略大小写）。</summary>
        public bool IsJson
        {
            get { return string.Equals(Provider, "json", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>MongoDB 库名限制：非空、长度 &lt;64、不含 / \ . " $ * &lt; &gt; : | ? 与空格及控制字符。</summary>
        public static bool IsValidMongoDatabaseName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 63) return false;
            const string forbidden = "/\\.\"$*<>:|?";
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || forbidden.IndexOf(c) >= 0) return false;
            }
            return true;
        }
    }
}
