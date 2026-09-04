using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace AIBot.Server
{
    public static class ReadinessService
    {
        private static readonly string[] RequiredTables =
        {
            "schema_migrations", "player_memories", "memory_facts", "memory_audits",
            "chat_logs", "sessions", "memory_summary_jobs"
        };

        public static async Task<(bool Ready, object Body)> CheckAsync(StorageOptions storage,
            MySqlConnectionFactory mysql, MemorySummaryQueue queue, Microsoft.Extensions.Configuration.IConfiguration config,
            CancellationToken ct)
        {
            bool storageReady = true;
            string storageError = null;
            var missing = new List<string>();
            if (storage.IsMySql)
            {
                try
                {
                    using (var connection = new MySqlConnector.MySqlConnection(mysql.ConnectionString))
                    {
                        await connection.OpenAsync(ct);
                        await connection.ExecuteScalarAsync(new CommandDefinition("SELECT 1", cancellationToken: ct));
                        IEnumerable<string> tables = await connection.QueryAsync<string>(new CommandDefinition(
                            "SELECT TABLE_NAME FROM information_schema.tables WHERE table_schema=DATABASE()",
                            cancellationToken: ct));
                        var set = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
                        missing.AddRange(RequiredTables.Where(x => !set.Contains(x)));
                        storageReady = missing.Count == 0;
                    }
                }
                catch (Exception)
                {
                    storageReady = false;
                    // 就绪探针可能暴露在负载均衡器/公网，不回传连接串、主机名或数据库异常细节。
                    storageError = "database_unavailable";
                }
            }

            string key = Environment.GetEnvironmentVariable("AIBOT_LLM_KEY") ?? config["Llm:ApiKey"];
            bool hasLlmKey = !string.IsNullOrWhiteSpace(key) || DataStore.ListNpcIds("default")
                .Select(id => DataStore.LoadNpc("default", id)?.model?.apiKey)
                .Any(value => !string.IsNullOrWhiteSpace(value));
            int npcCount = DataStore.ListNpcIds("default").Count;
            bool queueReady = queue != null;
            bool ready = storageReady && hasLlmKey && npcCount > 0 && queueReady;
            return (ready, new
            {
                ready,
                status = ready ? "ready" : "not_ready",
                checks = new
                {
                    storage = new { ok = storageReady, provider = storage.Provider, error = storageError, missingTables = missing },
                    llm = new { ok = hasLlmKey },
                    npc = new { ok = npcCount > 0, count = npcCount },
                    summaryQueue = new { ok = queueReady, pending = queue?.PendingCount ?? 0, failed = queue?.CurrentFailureCount ?? 0 }
                }
            });
        }
    }
}
