using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace AIBot.Server
{
    public static class ReadinessService
    {
        /// <summary>测试缝：覆盖「任一 NPC 自带 key」判定，避免就绪探针用例依赖真实 data 目录。为 null 时走真实扫描。</summary>
        internal static Func<bool> HasAnyNpcKeyOverride = null;

        private static bool HasAnyNpcKey(List<string> npcIds)
        {
            if (HasAnyNpcKeyOverride != null) return HasAnyNpcKeyOverride();
            return npcIds
                .Select(id => DataStore.LoadNpc("default", id)?.model?.apiKey)
                .Any(value => !string.IsNullOrWhiteSpace(value));
        }

        public static async Task<(bool Ready, object Body)> CheckAsync(StorageOptions storage,
            MemorySummaryQueue queue, Microsoft.Extensions.Configuration.IConfiguration config,
            CancellationToken ct, MongoConnectionFactory mongo = null)
        {
            bool storageReady = true;
            string storageError = null;
            var missing = new List<string>();
            if (storage.IsMongo)
            {
                try
                {
                    IMongoDatabase db = mongo.Database;
                    await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(
                        new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: ct);
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    using (var cursor = await db.ListCollectionNamesAsync(cancellationToken: ct))
                    {
                        foreach (string name in await cursor.ToListAsync(ct)) set.Add(name);
                    }
                    missing.AddRange(MongoInitializer.RequiredCollections.Where(x => !set.Contains(x)));
                    storageReady = missing.Count == 0;
                }
                catch (Exception)
                {
                    storageReady = false;
                    // 就绪探针可能暴露在负载均衡器/公网，不回传连接串、主机名或数据库异常细节。
                    storageError = "database_unavailable";
                }
            }

            // NPC 专用连接优先于控制台默认连接；旧 NPC 文件里的 Key 仍可兼容读取。
            List<string> npcIds = DataStore.ListNpcIds("default");
            bool hasConnection = false;
            if (HasAnyNpcKeyOverride == null && DataStore.FindDataRoot() != null)
            {
                try
                {
                    ModelConnectionDocument connections = ModelConnectionStore.Snapshot();
                    hasConnection = npcIds.Any(id =>
                    {
                        string profileId = connections.npcMain.TryGetValue("default/" + id, out string assigned)
                            ? assigned : connections.globalMain;
                        return connections.connections.Any(profile => profile.id == profileId
                            && !string.IsNullOrWhiteSpace(profile.apiKey));
                    });
                }
                catch { hasConnection = false; }
            }
            bool hasLlmKey = hasConnection || HasAnyNpcKey(npcIds);
            int npcCount = npcIds.Count;
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
