using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace AIBot.Server
{
    /// <summary>
    /// 输出启动期可操作诊断。诊断不会记录密钥或完整连接字符串，也不会因为可恢复的
    /// 配置问题阻止本地 Server 启动；数据库连接失败仍会抛出，让 MySQL 模式尽早失败。
    /// </summary>
    public static class StartupDiagnostics
    {
        private static readonly string[] RequiredTables =
        {
            "schema_migrations", "player_memories", "memory_facts", "memory_audits", "chat_logs", "sessions",
            "memory_summary_jobs"
        };

        /// <summary>上次运行使用的存储模式（读自 data/.last-storage-mode 标记；null 表示无历史记录）。</summary>
        public static string PreviousStorageMode { get; private set; }

        public static async Task RunAsync(StorageOptions storage, MySqlConnectionFactory mysql,
            IConfiguration configuration, CancellationToken ct)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            Console.WriteLine("[startup] storage provider: " + storage.Provider);
            RememberStorageMode(storage);

            string dataRoot = DataStore.FindDataRoot();
            if (storage.IsMySql)
            {
                await CheckMySqlAsync(mysql, ct);
            }
            else if (string.IsNullOrWhiteSpace(dataRoot))
            {
                Console.WriteLine("[startup][warning] JSON 存储未找到 data/ 根目录，请设置 AIBOT_DATA_ROOT");
            }
            else
            {
                Console.WriteLine("[startup] json data root: " + dataRoot);
            }

            MemoryPolicyLimits limits = MemoryPolicyService.LoadLimits(configuration);
            Console.WriteLine("[startup] memory limits: shortTerm<=" + limits.maxShortTermTurns
                + ", summaryThreshold<=" + limits.maxSummaryThreshold
                + ", maxFacts<=" + limits.maxFacts
                + ", background=" + (limits.allowBackgroundSummarization ? "enabled" : "disabled"));
            Console.WriteLine("[startup] memory capabilities: triggers="
                + string.Join(",", limits.supportedSummaryTriggers)
                + "; scopes=" + string.Join(",", limits.supportedMemoryScopes));

            string configuredKey = Environment.GetEnvironmentVariable("AIBOT_LLM_KEY")
                ?? configuration["Llm:ApiKey"];
            if (string.IsNullOrWhiteSpace(configuredKey))
            {
                Console.WriteLine("[startup][warning] 未配置全局 LLM API Key；如果 NPC 配置未提供独立 key，对话和摘要请求会失败");
            }
            else
            {
                Console.WriteLine("[startup] global LLM API key: configured");
            }

            List<string> npcIds = DataStore.ListNpcIds("default");
            if (npcIds.Count == 0)
                Console.WriteLine("[startup][warning] default Game 未发现可用 NPC 配置");
            else
                Console.WriteLine("[startup] default NPCs: " + string.Join(",", npcIds));
        }

        /// <summary>记录本次存储模式到 data/ 标记文件，供控制台提示“上次运行模式”；失败不影响启动。</summary>
        private static void RememberStorageMode(StorageOptions storage)
        {
            try
            {
                string dataRoot = DataStore.FindDataRoot();
                if (string.IsNullOrEmpty(dataRoot)) return;
                string markerPath = Path.Combine(dataRoot, ".last-storage-mode");
                if (File.Exists(markerPath)) PreviousStorageMode = File.ReadAllText(markerPath).Trim();
                File.WriteAllText(markerPath, storage.IsMySql ? "MySql" : "Json");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[startup][warning] 存储模式标记读写失败: " + ex.Message);
            }
        }

        private static async Task CheckMySqlAsync(MySqlConnectionFactory mysql, CancellationToken ct)
        {
            if (mysql == null) throw new InvalidOperationException("MySQL 模式未注册连接工厂");
            MySqlConnectionStringBuilder cs = new MySqlConnectionStringBuilder(mysql.ConnectionString);
            string database = cs.Database;
            string server = string.IsNullOrWhiteSpace(cs.Server) ? "localhost" : cs.Server;
            Console.WriteLine("[startup] mysql target: " + server + ":" + cs.Port
                + ", database=" + (string.IsNullOrWhiteSpace(database) ? "<none>" : database));

            using (var connection = new MySqlConnection(mysql.ConnectionString))
            {
                await connection.OpenAsync(ct);
                await connection.ExecuteScalarAsync(new CommandDefinition("SELECT 1", cancellationToken: ct));
                Console.WriteLine("[startup] mysql connection: healthy");

                if (string.IsNullOrWhiteSpace(database)) return;
                try
                {
                    IEnumerable<string> tables = await connection.QueryAsync<string>(new CommandDefinition(
                        "SELECT TABLE_NAME FROM information_schema.tables WHERE table_schema=@Database",
                        new { Database = database }, cancellationToken: ct));
                    var existing = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
                    string[] missing = RequiredTables.Where(table => !existing.Contains(table)).ToArray();
                    if (missing.Length > 0)
                        Console.WriteLine("[startup][warning] MySQL 缺少表: " + string.Join(",", missing)
                            + "；请启用 Storage:MySql:AutoMigrate 或执行 database/mysql/schema.sql");
                    else
                        Console.WriteLine("[startup] mysql schema: all required tables present");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[startup][warning] 无法检查 MySQL 表结构: " + ex.Message);
                }
            }
        }
    }
}

