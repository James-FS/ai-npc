using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace AIBot.Server
{
    /// <summary>
    /// 输出启动期可操作诊断。诊断不会记录密钥或完整连接字符串，也不会因为可恢复的
    /// 配置问题阻止本地 Server 启动；Mongo 连接失败仍会抛出，让 Mongo 模式尽早失败。
    /// </summary>
    public static class StartupDiagnostics
    {
        /// <summary>上次运行使用的存储模式（读自 data/.last-storage-mode 标记；null 表示无历史记录）。</summary>
        public static string PreviousStorageMode { get; private set; }

        public static async Task RunAsync(StorageOptions storage, IConfiguration configuration,
            CancellationToken ct, MongoConnectionFactory mongo = null)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            Console.WriteLine("[startup] storage provider: " + storage.Provider);
            RememberStorageMode(storage);

            string dataRoot = DataStore.FindDataRoot();
            if (storage.IsMongo)
            {
                await CheckMongoAsync(mongo, ct);
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

            string configuredKey = ApiKeyResolver.Resolve(null, configuration);
            if (string.IsNullOrWhiteSpace(configuredKey))
            {
                Console.WriteLine("[startup][warning] 未配置全局 LLM API Key（控制台设置 / AIBOT_LLM_KEY / appsettings 均为空）；"
                    + "如果 NPC 配置未提供独立 key，对话和摘要请求会失败");
            }
            else
            {
                Console.WriteLine("[startup] global LLM API key: configured (source=" + ApiKeyResolver.SourceOf(null, configuration) + ")");
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
                if (File.Exists(markerPath))
                {
                    string previous = File.ReadAllText(markerPath).Trim();
                    // 归一化历史值：旧版本写过 "MySql"，前端类型只认 Mongo/Json。
                    PreviousStorageMode = string.Equals(previous, "Mongo", StringComparison.OrdinalIgnoreCase) ? "Mongo"
                        : string.Equals(previous, "Json", StringComparison.OrdinalIgnoreCase) ? "Json"
                        : null;
                }
                File.WriteAllText(markerPath, storage.IsMongo ? "Mongo" : "Json");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[startup][warning] 存储模式标记读写失败: " + ex.Message);
            }
        }

        /// <summary>Mongo 启动诊断：ping + 必需集合检查；失败抛出，让 Mongo 模式尽早失败。</summary>
        private static async Task CheckMongoAsync(MongoConnectionFactory mongo, CancellationToken ct)
        {
            if (mongo == null) throw new InvalidOperationException("Mongo 模式未注册连接工厂");
            Console.WriteLine("[startup] mongo target: database=" + mongo.DatabaseName);

            IMongoDatabase db = mongo.Database;
            await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(
                new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: ct);
            Console.WriteLine("[startup] mongo connection: healthy");

            try
            {
                var existing = new HashSet<string>(StringComparer.Ordinal);
                using (var cursor = await db.ListCollectionNamesAsync(cancellationToken: ct))
                {
                    foreach (string name in await cursor.ToListAsync(ct)) existing.Add(name);
                }
                string[] missing = MongoInitializer.RequiredCollections
                    .Where(collection => !existing.Contains(collection)).ToArray();
                if (missing.Length > 0)
                    Console.WriteLine("[startup][warning] Mongo 缺少集合: " + string.Join(",", missing)
                        + "；请启用 Storage:Mongo:AutoMigrate");
                else
                    Console.WriteLine("[startup] mongo schema: all required collections present");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[startup][warning] 无法检查 Mongo 集合: " + ex.Message);
            }
        }
    }
}
