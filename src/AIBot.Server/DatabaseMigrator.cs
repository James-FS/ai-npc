using System;
using System.Linq;
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
                await connection.ExecuteAsync(new CommandDefinition(@"
CREATE TABLE IF NOT EXISTS schema_migrations (
  version INT NOT NULL,
  name VARCHAR(128) NOT NULL,
  applied_utc DATETIME(6) NOT NULL,
  PRIMARY KEY (version)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci", cancellationToken: ct));
                var applied = (await connection.QueryAsync<int>(new CommandDefinition(
                    "SELECT version FROM schema_migrations", cancellationToken: ct))).ToHashSet();
                foreach (MySqlSchema.Migration migration in MySqlSchema.Migrations)
                {
                    if (applied.Contains(migration.Version)) continue;
                    // MySQL DDL 会隐式提交，迁移语句保持幂等，完成后再记录版本。
                    if (migration.Version == 1)
                    {
                        foreach (string statement in MySqlSchema.Statements)
                            await connection.ExecuteAsync(new CommandDefinition(statement,
                                cancellationToken: ct));
                    }
                    else
                    {
                        await connection.ExecuteAsync(new CommandDefinition(migration.Sql,
                            cancellationToken: ct));
                    }
                    await connection.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO schema_migrations(version,name,applied_utc) VALUES (@Version,@Name,UTC_TIMESTAMP(6))",
                        new { Version = migration.Version, Name = migration.Name }, cancellationToken: ct));
                }
            }
        }
    }
}
