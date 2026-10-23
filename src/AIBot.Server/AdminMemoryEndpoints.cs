using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>记忆管理 API：策略、长期记忆、事实、迁移和审计。</summary>
    public static partial class AdminEndpoints
    {
        public class MemoryPolicyPreviewRequest
        {
            public MemorySettings NpcOverride { get; set; }
            public MemoryPolicyOverrides SessionOverride { get; set; }
        }

        public class UpdateMemorySummaryRequest
        {
            public string Summary { get; set; }
            public int ExpectedVersion { get; set; }
        }

        public class MemoryFactWriteRequest
        {
            public MemoryFact Fact { get; set; }
            public int ExpectedVersion { get; set; }
        }

        public class ManualSummarizeRequest
        {
            public string SessionId { get; set; }
        }

        public class MemoryCleanupRequest
        {
            public int InactiveDays { get; set; }
            public bool DryRun { get; set; } = true;
            public int Limit { get; set; } = 200;
        }

        private static void MapMemory(WebApplication app)
        {
            PlayerMemoryService playerMemories = app.Services.GetRequiredService<PlayerMemoryService>();
            MemorySummaryQueue summaryQueue = app.Services.GetRequiredService<MemorySummaryQueue>();
            MemoryAuditService audit = app.Services.GetRequiredService<MemoryAuditService>();

            app.MapGet("/api/admin/memory-limits", () =>
                JsonNet(MemoryPolicyService.LoadLimits(app.Configuration)));
            // ---- Game 记忆默认策略 ----
            app.MapGet("/api/games/{gid}/memory-policy", (string gid) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                MemoryPolicy stored = DataStore.LoadMemoryPolicy(gid);
                return JsonNet(new
                {
                    exists = stored != null,
                    policy = MemoryPolicyService.Redact(stored ?? MemoryPolicy.Defaults()),
                    limits = MemoryPolicyService.LoadLimits(app.Configuration)
                });
            });

            app.MapPut("/api/games/{gid}/memory-policy", (string gid, MemoryPolicy body, HttpContext http) =>
            {
                if (!DataStore.IsValidId(gid) || body == null) return Results.BadRequest("参数非法");
                MemoryPolicy existing = DataStore.LoadMemoryPolicy(gid);
                if (body.summaryModel != null && string.IsNullOrEmpty(body.summaryModel.apiKey))
                    body.summaryModel.apiKey = existing?.summaryModel?.apiKey;

                EffectiveMemoryPolicy validated = MemoryPolicyResolver.Resolve(body,
                    new MemorySettings { inheritGameDefaults = true }, null,
                    MemoryPolicyService.LoadLimits(app.Configuration));
                StripServerConnectionDetails(validated.policy.summaryModel);
                if (!DataStore.SaveMemoryPolicy(gid, validated.policy)) return Results.Problem("保存失败");
                try
                {
                    audit.RecordRequired(new MemoryAuditEntry
                    {
                        gameId = gid,
                        actor = AuditActor(http),
                        action = "policy.game.update",
                        before = JToken.FromObject(MemoryPolicyService.Redact(existing ?? MemoryPolicy.Defaults())),
                        after = JToken.FromObject(MemoryPolicyService.Redact(validated.policy))
                    });
                }
                catch (Exception ex) { return MemoryError(ex); }
                return JsonNet(MemoryPolicyService.Redact(validated));
            });

            // ---- NPC 记忆覆盖与最终策略 ----
            app.MapGet("/api/games/{gid}/npcs/{id}/memory-policy", (string gid, string id) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, id);
                if (cfg == null) return Results.NotFound("npc not found: " + id);
                AgentConfigDto redacted = RedactSecrets(cfg);
                return JsonNet(new
                {
                    npc = redacted.memory ?? new MemorySettings(),
                    effective = MemoryPolicyService.Redact(
                        MemoryPolicyService.Resolve(gid, cfg, null, app.Configuration))
                });
            });

            app.MapPut("/api/games/{gid}/npcs/{id}/memory-policy", (string gid, string id,
                MemorySettings body, HttpContext http) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id) || body == null)
                    return Results.BadRequest("参数非法");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, id);
                if (cfg == null) return Results.NotFound("npc not found: " + id);
                MemorySettings before = RedactMemorySettings(cfg.memory);
                if (body.summaryModel != null && string.IsNullOrEmpty(body.summaryModel.apiKey))
                    body.summaryModel.apiKey = cfg.memory?.summaryModel?.apiKey;
                StripServerConnectionDetails(body.summaryModel);
                cfg.memory = body;
                if (!DataStore.SaveNpc(gid, cfg)) return Results.Problem("保存失败");
                try
                {
                    audit.RecordRequired(new MemoryAuditEntry
                    {
                        gameId = gid,
                        npcId = id,
                        actor = AuditActor(http),
                        action = "policy.npc.update",
                        before = JToken.FromObject(before),
                        after = JToken.FromObject(RedactMemorySettings(body))
                    });
                }
                catch (Exception ex) { return MemoryError(ex); }
                return JsonNet(MemoryPolicyService.Redact(
                    MemoryPolicyService.Resolve(gid, cfg, null, app.Configuration)));
            });

            app.MapPost("/api/games/{gid}/npcs/{id}/memory-policy/preview-effective",
                (string gid, string id, MemoryPolicyPreviewRequest body) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, id);
                if (cfg == null) return Results.NotFound("npc not found: " + id);
                if (body?.NpcOverride != null) cfg.memory = body.NpcOverride;
                return JsonNet(MemoryPolicyService.Redact(MemoryPolicyService.Resolve(
                    gid, cfg, body?.SessionOverride, app.Configuration)));
            });

            // ---- 玩家长期记忆管理 ----
            app.MapGet("/api/games/{gid}/memories", async (string gid, string npcId,
                string playerId, int? limit, int? offset, HttpContext http) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                npcId = OptionalNpcId(npcId);
                if (npcId != null && !DataStore.IsValidId(npcId)) return Results.BadRequest("非法 npcId");
                if (playerId != null && !DataStore.IsValidPlayerId(playerId)) return Results.BadRequest("非法 playerId");
                MemoryListPage page = await playerMemories.ListAsync(gid, npcId, playerId,
                    limit ?? 50, offset ?? 0, http.RequestAborted);
                return JsonNet(page);
            });

            app.MapPost("/api/games/{gid}/memories/cleanup", async (string gid,
                MemoryCleanupRequest body, HttpContext http) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                body = body ?? new MemoryCleanupRequest();
                int configuredDays = app.Configuration.GetValue<int?>("Memory:RetentionDays") ?? 90;
                int inactiveDays = body.InactiveDays > 0 ? body.InactiveDays : configuredDays;
                if (inactiveDays < 1 || inactiveDays > 3650)
                    return Results.UnprocessableEntity(new { error = "inactiveDays 必须在 1~3650 之间" });
                int limitValue = Math.Max(1, Math.Min(1000, body.Limit));
                DateTime cutoff = DateTime.UtcNow.AddDays(-inactiveDays);
                MemoryRetentionScan scan = await playerMemories.FindRetentionCandidatesAsync(gid,
                    cutoff, limitValue, http.RequestAborted);
                List<MemoryListItem> candidates = scan.candidates;
                var deleted = new JArray();
                var conflicts = new JArray();
                if (!body.DryRun)
                {
                    foreach (MemoryListItem item in candidates)
                    {
                        try
                        {
                            PlayerLongTermMemory before = await playerMemories.LoadAsync(gid,
                                item.npcId, item.playerId, http.RequestAborted);
                            using (await summaryQueue.InvalidatePlayerAsync(gid, item.npcId,
                                item.playerId, http.RequestAborted))
                            {
                                await playerMemories.DeleteAsync(gid, item.npcId, item.playerId,
                                    item.memoryVersion, http.RequestAborted);
                                bool sessionsCleared = await SessionStore.ClearPlayerMemoryAsync(gid,
                                    item.npcId, item.playerId, http.RequestAborted);
                                audit.RecordRequired(new MemoryAuditEntry
                                {
                                    gameId = gid,
                                    npcId = item.npcId,
                                    playerId = item.playerId,
                                    actor = AuditActor(http),
                                    action = "memory.retention.delete",
                                    before = JToken.FromObject(before),
                                    after = JValue.CreateNull(),
                                    metadata = new JObject
                                    {
                                        ["inactiveDays"] = inactiveDays,
                                        ["sessionsCleared"] = sessionsCleared
                                    }
                                });
                            }
                            deleted.Add(new JObject { ["npcId"] = item.npcId, ["playerId"] = item.playerId });
                        }
                        catch (MemoryVersionConflictException ex)
                        {
                            conflicts.Add(new JObject
                            {
                                ["npcId"] = item.npcId,
                                ["playerId"] = item.playerId,
                                ["actualVersion"] = ex.ActualVersion
                            });
                        }
                        catch (Exception ex) { return MemoryError(ex); }
                    }
                }
                return JsonNet(new
                {
                    dryRun = body.DryRun,
                    cutoffUtc = cutoff,
                    totalMemoryCount = scan.totalMemoryCount,
                    batchLimit = scan.batchLimit,
                    hasMoreCandidates = scan.hasMoreCandidates,
                    candidateCount = candidates.Count,
                    candidates,
                    deleted,
                    conflicts
                });
            });

            app.MapGet("/api/games/{gid}/memories/{npcId}/{playerId}", async
                (string gid, string npcId, string playerId, HttpContext http) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(npcId)
                    || !DataStore.IsValidPlayerId(playerId)) return Results.BadRequest("参数非法");
                PlayerLongTermMemory memory = await playerMemories.LoadAsync(gid, npcId, playerId,
                    http.RequestAborted);
                return JsonNet(memory);
            });

            app.MapGet("/api/games/{gid}/memories/{npcId}/{playerId}/export", async
                (string gid, string npcId, string playerId, HttpContext http) =>
            {
                if (!ValidMemoryKey(gid, npcId, playerId)) return Results.BadRequest("参数非法");
                PlayerLongTermMemory memory = await playerMemories.LoadAsync(gid, npcId, playerId,
                    http.RequestAborted);
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(memory, Formatting.Indented));
                return Results.File(bytes, "application/json; charset=utf-8",
                    gid + "_" + npcId + "_" + playerId + "_memory.json");
            });

            app.MapPut("/api/games/{gid}/memories/{npcId}/{playerId}/summary", async
                (string gid, string npcId, string playerId, UpdateMemorySummaryRequest body,
                    HttpContext http) =>
            {
                if (!ValidMemoryKey(gid, npcId, playerId) || body == null)
                    return Results.BadRequest("参数非法");
                if ((body.Summary?.Length ?? 0) > 4000)
                    return Results.UnprocessableEntity(new { error = "summary 最长 4000 字符" });
                try
                {
                    PlayerLongTermMemory before = await playerMemories.LoadAsync(gid, npcId, playerId,
                        http.RequestAborted);
                    PlayerLongTermMemory saved = await playerMemories.UpdateSummaryAsync(gid, npcId,
                        playerId, body.Summary, body.ExpectedVersion, http.RequestAborted);
                    AuditMemory(audit, http, "memory.summary.update", before, saved);
                    return JsonNet(saved);
                }
                catch (Exception ex) { return MemoryError(ex); }
            });

            app.MapPost("/api/games/{gid}/memories/{npcId}/{playerId}/facts", async
                (string gid, string npcId, string playerId, MemoryFactWriteRequest body,
                    HttpContext http) =>
            {
                if (!ValidMemoryKey(gid, npcId, playerId) || body?.Fact == null)
                    return Results.BadRequest("参数非法");
                IResult validation = ValidateFact(body.Fact, false);
                if (validation != null) return validation;
                try
                {
                    PlayerLongTermMemory before = await playerMemories.LoadAsync(gid, npcId, playerId,
                        http.RequestAborted);
                    int maxFacts = ResolveMaxFacts(gid, npcId, app);
                    PlayerLongTermMemory saved = await playerMemories.AddFactAsync(gid, npcId,
                        playerId, body.Fact, body.ExpectedVersion, maxFacts, http.RequestAborted);
                    AuditMemory(audit, http, "memory.fact.create", before, saved,
                        new JObject { ["factId"] = saved.facts.LastOrDefault()?.id });
                    return JsonNet(saved);
                }
                catch (Exception ex) { return MemoryError(ex); }
            });

            app.MapPut("/api/games/{gid}/memories/{npcId}/{playerId}/facts/{factId}", async
                (string gid, string npcId, string playerId, string factId, MemoryFactWriteRequest body,
                    HttpContext http) =>
            {
                if (!ValidMemoryKey(gid, npcId, playerId) || !DataStore.IsValidSessionId(factId)
                    || body?.Fact == null) return Results.BadRequest("参数非法");
                IResult validation = ValidateFact(body.Fact, true);
                if (validation != null) return validation;
                try
                {
                    PlayerLongTermMemory before = await playerMemories.LoadAsync(gid, npcId, playerId,
                        http.RequestAborted);
                    PlayerLongTermMemory saved = await playerMemories.UpdateFactAsync(gid, npcId,
                        playerId, factId, body.Fact, body.ExpectedVersion, http.RequestAborted);
                    AuditMemory(audit, http, "memory.fact.update", before, saved,
                        new JObject { ["factId"] = factId });
                    return JsonNet(saved);
                }
                catch (Exception ex) { return MemoryError(ex); }
            });

            app.MapDelete("/api/games/{gid}/memories/{npcId}/{playerId}/facts/{factId}", async
                (string gid, string npcId, string playerId, string factId, int? expectedVersion,
                    HttpContext http) =>
            {
                if (!ValidMemoryKey(gid, npcId, playerId) || !DataStore.IsValidSessionId(factId)
                    || !expectedVersion.HasValue) return Results.BadRequest("需要合法参数与 expectedVersion");
                try
                {
                    PlayerLongTermMemory before = await playerMemories.LoadAsync(gid, npcId, playerId,
                        http.RequestAborted);
                    PlayerLongTermMemory saved = await playerMemories.DeleteFactAsync(gid, npcId,
                        playerId, factId, expectedVersion.Value, http.RequestAborted);
                    AuditMemory(audit, http, "memory.fact.delete", before, saved,
                        new JObject { ["factId"] = factId });
                    return JsonNet(saved);
                }
                catch (Exception ex) { return MemoryError(ex); }
            });

            app.MapPost("/api/games/{gid}/memories/{npcId}/{playerId}/summarize",
                (string gid, string npcId, string playerId, ManualSummarizeRequest body,
                    HttpContext http) =>
            {
                if (!ValidMemoryKey(gid, npcId, playerId)
                    || !DataStore.IsValidSessionId(body?.SessionId)) return Results.BadRequest("sessionId 必填且必须合法");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, npcId);
                if (cfg == null) return Results.NotFound("npc not found");
                int turns = MemoryPolicyService.Resolve(gid, cfg, null, app.Configuration).policy.shortTermTurns;
                SessionState session = SessionStore.GetOrCreate(gid, npcId, playerId, body.SessionId, turns);
                if (session.Memory.EvictedCount == 0)
                    return Results.Conflict(new { error = "该会话没有待摘要消息" });
                bool queued = summaryQueue.EnqueueManual(gid, npcId, playerId, body.SessionId,
                    AuditActor(http));
                return queued
                    ? Results.Accepted(value: new { queued = true, pendingMessages = session.Memory.EvictedCount })
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            });

            app.MapDelete("/api/games/{gid}/memories/{npcId}/{playerId}", async
                (string gid, string npcId, string playerId, int? expectedVersion, HttpContext http) =>
            {
                if (!ValidMemoryKey(gid, npcId, playerId) || !expectedVersion.HasValue)
                    return Results.BadRequest("需要合法参数与 expectedVersion");
                try
                {
                    PlayerLongTermMemory before = await playerMemories.LoadAsync(gid, npcId, playerId,
                        http.RequestAborted);
                    bool sessionsCleared;
                    using (await summaryQueue.InvalidatePlayerAsync(gid, npcId, playerId,
                        http.RequestAborted))
                    {
                        await playerMemories.DeleteAsync(gid, npcId, playerId, expectedVersion,
                            http.RequestAborted);
                        sessionsCleared = await SessionStore.ClearPlayerMemoryAsync(gid, npcId,
                            playerId, http.RequestAborted);
                        AuditMemory(audit, http, "memory.delete", before, null,
                            new JObject { ["sessionsCleared"] = sessionsCleared });
                    }
                    return sessionsCleared
                        ? Results.Json(new { ok = true })
                        : Results.Problem("长期记忆已删除，但部分 Session 清理失败；旧摘要任务已失效");
                }
                catch (Exception ex) { return MemoryError(ex); }
            });

            // ---- 旧会话显式迁移 ----
            app.MapGet("/api/games/{gid}/memory-migrations", (string gid, string npcId) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                npcId = OptionalNpcId(npcId);
                if (npcId != null && !DataStore.IsValidId(npcId)) return Results.BadRequest("非法 npcId");
                var candidates = SessionStore.ListByGame(gid, npcId)
                    .Where(s => string.IsNullOrEmpty(s.PlayerId)
                        && (!string.IsNullOrWhiteSpace(s.Summary) || (s.Facts?.Count ?? 0) > 0))
                    .Select(s => new
                    {
                        npcId = s.NpcId,
                        sessionId = s.SessionId,
                        hasSummary = !string.IsNullOrWhiteSpace(s.Summary),
                        factCount = s.Facts?.Count ?? 0,
                        lastActiveUtc = s.LastActiveUtc
                    }).ToList();
                return JsonNet(new { total = candidates.Count, items = candidates });
            });

            app.MapPost("/api/games/{gid}/sessions/{sid}/migrate-memory", async
                (string gid, string sid, string npcId, string playerId, HttpContext http) =>
            {
                if (!ValidMemoryKey(gid, npcId, playerId) || !DataStore.IsValidSessionId(sid))
                    return Results.BadRequest("需要合法 npcId、playerId 与 sessionId");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, npcId);
                if (cfg == null) return Results.NotFound("npc not found");
                EffectiveMemoryPolicy policy = MemoryPolicyService.Resolve(gid, cfg, null, app.Configuration);
                SessionState session = SessionStore.GetOrCreate(gid, npcId, playerId, sid,
                    policy.policy.shortTermTurns);
                await session.Gate.WaitAsync(http.RequestAborted);
                try
                {
                    PlayerLongTermMemory before = await playerMemories.LoadAsync(gid, npcId,
                        playerId, http.RequestAborted);
                    bool hadLegacy = !string.IsNullOrWhiteSpace(session.Summary)
                        || (session.Facts?.Count ?? 0) > 0;
                    PlayerLongTermMemory saved = await playerMemories.LoadAndMigrateAsync(session,
                        policy.policy.maxFacts, http.RequestAborted);
                    bool sessionSaved = SessionStore.Save(session);
                    if (hadLegacy)
                    {
                        AuditMemory(audit, http, "memory.migrate.legacy-session", before, saved,
                            new JObject { ["sessionId"] = sid, ["sessionSaved"] = sessionSaved });
                    }
                    return sessionSaved
                        ? JsonNet(new { migrated = hadLegacy, memory = saved })
                        : Results.Problem("长期记忆已迁移，但 session 迁移标记保存失败；重复调用是安全的");
                }
                catch (Exception ex) { return MemoryError(ex); }
                finally { session.Gate.Release(); }
            });

            // ---- 记忆审计 ----
            app.MapGet("/api/games/{gid}/memory-audit", (string gid, string npcId,
                string playerId, string action, string date, int? limit, int? offset) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                npcId = OptionalNpcId(npcId);
                if (npcId != null && !DataStore.IsValidId(npcId)) return Results.BadRequest("非法 npcId");
                if (playerId != null && !DataStore.IsValidPlayerId(playerId)) return Results.BadRequest("非法 playerId");
                if (action != null && action.Length > 128) return Results.BadRequest("action 过长");
                return JsonNet(audit.Query(gid, npcId, playerId, action, date,
                    limit ?? 50, offset ?? 0));
            });

        }

        private static bool ValidMemoryKey(string gameId, string npcId, string playerId)
        {
            return DataStore.IsValidId(gameId) && DataStore.IsValidId(npcId)
                && DataStore.IsValidPlayerId(playerId);
        }

        private static IResult ValidateFact(MemoryFact fact, bool updating)
        {
            if (fact == null || string.IsNullOrWhiteSpace(fact.value))
                return Results.UnprocessableEntity(new { error = "fact.value 必填" });
            if (fact.value.Length > 1000)
                return Results.UnprocessableEntity(new { error = "fact.value 最长 1000 字符" });
            if (!string.IsNullOrEmpty(fact.id) && !DataStore.IsValidSessionId(fact.id))
                return Results.UnprocessableEntity(new { error = "fact.id 非法" });
            if (!string.IsNullOrEmpty(fact.category) && !DataStore.IsValidId(fact.category))
                return Results.UnprocessableEntity(new { error = "fact.category 非法" });
            if (!string.IsNullOrEmpty(fact.key) && !DataStore.IsValidSessionId(fact.key))
                return Results.UnprocessableEntity(new { error = "fact.key 非法" });
            if (fact.confidence < 0f || fact.confidence > 1f)
                return Results.UnprocessableEntity(new { error = "confidence 必须在 0~1 之间" });
            if (!string.IsNullOrEmpty(fact.sourceSessionId)
                && !DataStore.IsValidSessionId(fact.sourceSessionId))
                return Results.UnprocessableEntity(new { error = "sourceSessionId 非法" });
            return null;
        }

        private static int ResolveMaxFacts(string gameId, string npcId, WebApplication app)
        {
            AgentConfigDto cfg = DataStore.LoadNpc(gameId, npcId);
            return cfg != null
                ? MemoryPolicyService.Resolve(gameId, cfg, null, app.Configuration).policy.maxFacts
                : MemoryPolicyService.LoadLimits(app.Configuration).maxFacts;
        }

        private static void AuditMemory(MemoryAuditService audit, HttpContext http, string action,
            PlayerLongTermMemory before, PlayerLongTermMemory after, JObject metadata = null)
        {
            PlayerLongTermMemory identity = after ?? before;
            audit.RecordRequired(new MemoryAuditEntry
            {
                gameId = identity?.gameId,
                npcId = identity?.npcId,
                playerId = identity?.playerId,
                actor = AuditActor(http),
                action = action,
                before = before == null ? JValue.CreateNull() : JToken.FromObject(before),
                after = after == null ? JValue.CreateNull() : JToken.FromObject(after),
                metadata = metadata
            });
        }

        private static MemorySettings RedactMemorySettings(MemorySettings source)
        {
            MemorySettings clone = JsonConvert.DeserializeObject<MemorySettings>(
                JsonConvert.SerializeObject(source ?? new MemorySettings()));
            if (clone.summaryModel != null) clone.summaryModel.apiKey = string.Empty;
            return clone;
        }
    }
}
