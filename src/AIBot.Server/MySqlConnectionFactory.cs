using System;
using System.Data;
using MySqlConnector;

namespace AIBot.Server
{
    /// <summary>Dapper 使用的 MySQL 连接工厂。每次操作创建独立连接，连接池由 MySqlConnector 管理。</summary>
    public sealed class MySqlConnectionFactory
    {
        private readonly string _connectionString;
        public string ConnectionString { get { return _connectionString; } }

        public MySqlConnectionFactory(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("MySQL connection string is required", nameof(connectionString));
            _connectionString = connectionString;
        }

        public IDbConnection OpenConnection()
        {
            var connection = new MySqlConnection(_connectionString);
            connection.Open();
            return connection;
        }
    }
}
