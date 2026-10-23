using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>运维 API：存储状态、摘要队列、会话、日志和统计。</summary>
    public static partial class AdminEndpoints
    {
        public class RetryMemorySummaryRequest
        {
            public string GameId { get; set; }
            public string NpcId { get; set; }
            public string PlayerId { get; set; }
            public string SessionId { get; set; }
        }

        private static void MapOperations(WebApplication app)
        {
            // 与启动参数迁移共用同一进程，防止管理台重复执行。
            SemaphoreSlim migrateGate = new SemaphoreSlim(1, 1);
            MemorySummaryQueue summaryQueue = app.Services.GetRequiredService<MemorySummaryQueue>();
            MemoryAuditService audit = app.Services.GetRequiredService<MemoryAuditService>();
            RuntimeLogService runtimeLogs = app.Services.GetRequiredService<RuntimeLogService>();

            // 存储模式只读展示：不含密码等敏感信息
            app.MapGet("/api/admin/storage", () =>
            {
                StorageOptions storage = StorageOptions.From(app.Configuration);
                JObject mongo = null;
                if (storage.IsMongo && !string.IsNullOrWhiteSpace(storage.MongoConnectionString))
                {
                    // 只解析 host/库名，绝不回传连接串或凭据。
                    var url = new MongoDB.Driver.MongoUrl(storage.MongoConnectionString);
                    MongoDB.Driver.MongoServerAddress first = url.Servers == null
                        ? null : url.Servers.FirstOrDefault();
                    string host = first != null ? first.ToString() : "localhost";
                    mongo = new JObject
                    {
                        ["host"] = host,
                        ["database"] = string.IsNullOrWhiteSpace(storage.MongoDatabase) ? "<none>" : storage.MongoDatabase,
                        ["autoMigrate"] = storage.MongoAutoMigrate
                    };
                }
                return JsonNet(new JObject
                {
                    ["provider"] = storage.IsMongo ? "Mongo" : "Json",
                    ["mongo"] = mongo,
                    ["previousProvider"] = StartupDiagnostics.PreviousStorageMode,
                    ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            });
            // JSON→Mongo 记忆迁移：幂等，与启动参数 --migrate-json 等效；仅 Mongo 模式可用
            app.MapPost("/api/admin/storage/migrate-json", async (HttpContext http) =>
            {
                StorageOptions storage = StorageOptions.From(app.Configuration);
                if (!storage.IsMongo)
                    return Results.Conflict("当前为 JSON 模式；请以 Mongo 模式启动 Server 后再执行迁移");
                MongoConnectionFactory mongoFactory = app.Services.GetService<MongoConnectionFactory>();
                if (mongoFactory == null)
                    return Results.Conflict("Mongo 连接未初始化，无法执行迁移");
                if (!await migrateGate.WaitAsync(0, http.RequestAborted))
                    return Results.Conflict("已有一次迁移正在执行，请稍后再试");
                try
                {
                    var source = new JsonMemoryRepository();
                    var target = new MongoMemoryRepository(mongoFactory);
                    string gameId = app.Configuration["Storage:MigrationGameId"] ?? "default";
                    MigrationResult result = await new JsonToMemoryRepositoryMigrator(source, target)
                        .RunAsync(gameId, http.RequestAborted);
                    Console.WriteLine("JSON→Mongo memory migration (console): scanned=" + result.Scanned
                        + ", migrated=" + result.Migrated + ", skipped=" + result.Skipped);
                    return JsonNet(new JObject
                    {
                        ["gameId"] = gameId,
                        ["scanned"] = result.Scanned,
                        ["migrated"] = result.Migrated,
                        ["skipped"] = result.Skipped
                    });
                }
                finally { migrateGate.Release(); }
            });
            app.MapGet("/api/admin/memory-summary-queue", () => JsonNet(new
            {
                pending = summaryQueue.PendingCount,
                failed = summaryQueue.FailedJobs,
                failedCurrent = summaryQueue.CurrentFailureCount,
                failedTotal = summaryQueue.FailedJobs,
                failures = summaryQueue.FailureSnapshot()
            }));
            app.MapGet("/api/admin/runtime-logs", (string date, string level, string category,
                string requestId, int? limit, int? offset) => JsonNet(runtimeLogs.Query(
                    date, level, category, requestId,
                    limit.HasValue && limit > 0 && limit <= 200 ? limit.Value : 50,
                    offset.HasValue && offset > 0 ? offset.Value : 0)));
            app.MapPost("/api/admin/memory-summary-queue/retry",
                (RetryMemorySummaryRequest body, HttpContext http) =>
            {
                if (body?.GameId != null && !DataStore.IsValidId(body.GameId))
                    return Results.BadRequest("非法 gameId");
                if (body?.NpcId != null && !DataStore.IsValidId(body.NpcId))
                    return Results.BadRequest("非法 npcId");
                if (body?.PlayerId != null && !DataStore.IsValidPlayerId(body.PlayerId))
                    return Results.BadRequest("非法 playerId");
                if (body?.SessionId != null && !DataStore.IsValidSessionId(body.SessionId))
                    return Results.BadRequest("非法 sessionId");
                int retried = summaryQueue.RetryFailures(body?.GameId, body?.NpcId,
                    body?.PlayerId, body?.SessionId, AuditActor(http));
                return Results.Accepted(value: new
                {
                    retried,
                    pending = summaryQueue.PendingCount,
                    failedCurrent = summaryQueue.CurrentFailureCount
                });
            });
            app.MapGet("/api/admin/memory-retention", () => JsonNet(new
            {
                retentionDays = app.Configuration.GetValue<int?>("Memory:RetentionDays") ?? 90,
                cleanupRequiresExplicitDryRunFalse = true,
                scope = "player_long_term_memory_and_related_sessions",
                clearsRelatedSessions = true
            }));

            // ---- 会话管理 ----
            app.MapGet("/api/games/{gid}/sessions", (string gid, string npcId, string playerId) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                npcId = OptionalNpcId(npcId);
                if (npcId != null && !DataStore.IsValidId(npcId)) return Results.BadRequest("非法 npcId");
                if (playerId != null && !DataStore.IsValidPlayerId(playerId)) return Results.BadRequest("非法 playerId");
                var array = new JArray();
                foreach (SessionState s in SessionStore.ListByGame(gid, npcId, playerId))
                {
                    MemorySummarySessionStatus summaryStatus = summaryQueue.GetSessionStatus(
                        gid, s.NpcId, s.PlayerId, s.SessionId, s.Memory.EvictedCount);
                    array.Add(new JObject
                    {
                        ["sessionId"] = s.SessionId,
                        ["npcId"] = s.NpcId,
                        ["playerId"] = s.PlayerId,
                        ["messageCount"] = s.Memory.Messages.Count,
                        ["pendingSummaryMessages"] = s.Memory.EvictedCount,
                        ["hasSummary"] = !string.IsNullOrEmpty(s.Summary),
                        ["factCount"] = s.Facts.Count,
                        ["lastActiveUtc"] = s.LastActiveUtc,
                        ["summaryStatus"] = summaryStatus.Status,
                        ["summaryError"] = summaryStatus.Error,
                        ["summaryFailedUtc"] = summaryStatus.FailedUtc
                    });
                }
                return JsonNet(new { sessions = array });
            });

            app.MapGet("/api/games/{gid}/sessions/{sid}", (string gid, string sid, string npcId, string playerId) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidSessionId(sid)) return Results.BadRequest("参数非法");
                if (npcId == null || !DataStore.IsValidId(npcId)) return Results.BadRequest("需要 npcId 查询参数");
                if (playerId != null && !DataStore.IsValidPlayerId(playerId)) return Results.BadRequest("非法 playerId");
                // 走 GetOrCreate：仅存于磁盘的会话被真实加载（列表里的磁盘条目只是占位）
                AgentConfigDto cfg = DataStore.LoadNpc(gid, npcId);
                int turns = cfg != null
                    ? MemoryPolicyService.Resolve(gid, cfg, null, app.Configuration).policy.shortTermTurns
                    : 12;
                SessionState s = SessionStore.GetOrCreate(gid, npcId, playerId, sid, turns);
                var messages = new JArray();
                foreach (var m in s.Memory.Messages)
                {
                    messages.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });
                }
                var facts = new JArray();
                foreach (string f in s.Facts) facts.Add(f);
                return JsonNet(new JObject
                {
                    ["sessionId"] = s.SessionId,
                    ["npcId"] = s.NpcId,
                    ["playerId"] = s.PlayerId,
                    ["messages"] = messages,
                    ["pendingSummaryMessages"] = s.Memory.EvictedCount,
                    ["summary"] = s.Summary,
                    ["facts"] = facts
                });
            });
            app.MapDelete("/api/games/{gid}/sessions/{sid}", (string gid, string sid,
                string npcId, string playerId, HttpContext http) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidSessionId(sid)) return Results.BadRequest("参数非法");
                if (npcId == null || !DataStore.IsValidId(npcId)) return Results.BadRequest("需要 npcId 查询参数");
                if (playerId != null && !DataStore.IsValidPlayerId(playerId)) return Results.BadRequest("非法 playerId");
                SessionState existing = SessionStore.ListByGame(gid, npcId, playerId)
                    .FirstOrDefault(s => s.SessionId == sid && s.NpcId == npcId
                        && s.PlayerId == playerId);
                try
                {
                    if (!SessionStore.Delete(gid, npcId, playerId, sid))
                        return Results.NotFound("session not found");
                }
                catch (IOException ex)
                {
                    return Results.Problem(ex.Message,
                        statusCode: StatusCodes.Status500InternalServerError);
                }
                try
                {
                    audit.RecordRequired(new MemoryAuditEntry
                    {
                        gameId = gid,
                        npcId = npcId,
                        playerId = playerId,
                        actor = AuditActor(http),
                        action = "session.delete",
                        before = existing == null ? JValue.CreateNull() : new JObject
                        {
                            ["sessionId"] = sid,
                            ["messageCount"] = existing.Memory.Messages.Count,
                            ["pendingSummaryMessages"] = existing.Memory.EvictedCount,
                            ["summary"] = existing.Summary,
                            ["facts"] = JArray.FromObject(existing.Facts ?? new List<string>())
                        },
                        after = JValue.CreateNull()
                    });
                }
                catch (Exception ex) { return MemoryError(ex); }
                return Results.Json(new { ok = true });
            });

            // ---- 日志查询 ----
            app.MapGet("/api/games/{gid}/logs", (string gid, string date, string npcId, int? limit, int? offset) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                npcId = OptionalNpcId(npcId);
                if (npcId != null && !DataStore.IsValidId(npcId)) return Results.BadRequest("非法 npcId");
                return JsonNet(ChatLogService.Query(gid, date, npcId,
                    limit.HasValue && limit > 0 && limit <= 200 ? limit.Value : 50,
                    offset.HasValue && offset > 0 ? offset.Value : 0));
            });

            // ---- 用量统计 ----
            app.MapGet("/api/games/{gid}/stats", (string gid) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                return JsonNet(ChatLogService.Snapshot(gid));
            });
        }

        /// <summary>可选 npcId 过滤参数：空串与缺省等价于"不过滤"。归一化为 null，
        /// 服务层（ListByGame/ListAsync/Query）只认 null=不过滤，空串会被误当成过滤值。</summary>
        private static string OptionalNpcId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

    }
}
