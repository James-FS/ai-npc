using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace AIBot.Server
{
    /// <summary>轻量数据库迁移入口。生产可先执行 schema.sql；AutoMigrate 适合本地开发。</summary>
    public static class DatabaseMigrator
    {
        public static async Task ApplyAsync(MySqlConnectionFactory factory, CancellationToken ct)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var builder = new MySqlConnectionStringBuilder(factory.ConnectionString);
            string database = builder.Database;
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("MySQL connection string must include Database");
            foreach (char c in database)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                    throw new InvalidOperationException("MySQL database name may only contain letters, digits and underscore");
            }
            builder.Database = string.Empty;
            using (var serverConnection = new MySqlConnection(builder.ConnectionString))
            {
                await serverConnection.OpenAsync(ct);
                await serverConnection.ExecuteAsync(new CommandDefinition(
                    "CREATE DATABASE IF NOT EXISTS `" + database + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
            }
            using (var connection = factory.OpenConnection())
            {
                foreach (string statement in MySqlSchema.Statements)
                    await connection.ExecuteAsync(new CommandDefinition(statement, cancellationToken: ct));
            }
        }
    }
}
