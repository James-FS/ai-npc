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

            // Server 运行必须解析到已分配的模型连接；旧 NPC 文件中的字段不会作为运行时回退。
            var npcs = DataStore.ListGameIds().SelectMany(gameId => DataStore.ListNpcIds(gameId)
                .Select(npcId => new { gameId, npcId })).ToList();
            bool hasLlmKey = false;
            if (DataStore.FindDataRoot() != null)
            {
                try
                {
                    ModelConnectionDocument connections = ModelConnectionStore.Snapshot();
                    hasLlmKey = npcs.Any(npc =>
                    {
                        string profileId = connections.npcMain.TryGetValue(npc.gameId + "/" + npc.npcId, out string assigned)
                            ? assigned : connections.globalMain;
                        return profileId != null && connections.connections.Any(profile => profile.id == profileId
                            && !string.IsNullOrWhiteSpace(profile.apiKey));
                    });
                }
                catch { hasLlmKey = false; }
            }
            int npcCount = npcs.Count;
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
