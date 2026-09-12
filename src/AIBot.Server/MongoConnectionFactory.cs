using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace AIBot.Server
{
    /// <summary>MongoDB 连接工厂。进程内复用一个 MongoClient（驱动自带连接池与线程安全）。</summary>
    public sealed class MongoConnectionFactory
    {
        private static readonly object RegisterLock = new object();
        private static bool _dateTimeSerializerRegistered;
        private readonly IMongoClient _client;

        public string ConnectionString { get; }
        public string DatabaseName { get; }

        public MongoConnectionFactory(string connectionString, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("MongoDB connection string is required", nameof(connectionString));
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentException("MongoDB database name is required", nameof(databaseName));
            ConnectionString = connectionString;
            DatabaseName = databaseName;
            EnsureUtcDateTimeSerializer();
            _client = new MongoClient(connectionString);
        }

        public IMongoClient Client { get { return _client; } }

        public IMongoDatabase Database { get { return _client.GetDatabase(DatabaseName); } }

        public IMongoCollection<TDocument> Collection<TDocument>(string name)
        {
            return Database.GetCollection<TDocument>(name);
        }

        /// <summary>
        /// 把进程内的 DateTime 序列化固定为 UTC，避免 BSON 丢失 Kind 造成时间偏移。
        /// 驱动全局注册只允许一次；重复调用（多实例/测试）静默跳过。
        /// </summary>
        private static void EnsureUtcDateTimeSerializer()
        {
            lock (RegisterLock)
            {
                if (_dateTimeSerializerRegistered) return;
                try
                {
                    BsonSerializer.RegisterSerializer(typeof(DateTime), new DateTimeSerializer(DateTimeKind.Utc));
                }
                catch (BsonSerializationException)
                {
                    // 已有注册（例如测试并行或二次构造），保持现状即可。
                }
                _dateTimeSerializerRegistered = true;
            }
        }
    }
}
