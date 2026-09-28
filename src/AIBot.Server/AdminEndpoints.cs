using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Llm;
using AIBot.Core.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>后台管理端点（主方案 §7.1）：NPC/世界观 CRUD、Prompt 预览、会话管理、用量统计。</summary>
    public static class AdminEndpoints
    {
        public class CreateNpcRequest
        {
            public string NpcId { get; set; }
            public bool FromTemplate { get; set; } = true;
            public AgentConfigDto Npc { get; set; }   // FromTemplate=false 时直接用完整配置创建
        }

        public class CreateGameRequest
        {
            public string GameId { get; set; }
        }

        /// <summary>PUT /npcs/{id} 请求体。Npc 为完整配置；ClearApiKey 与"留空不改"语义区分，
        /// 置 true 时清除该 NPC 独立 key（model.apiKey 与 memory.summaryModel.apiKey 回退到全局 key）。</summary>
        public class SaveNpcRequest
        {
            public AgentConfigDto Npc { get; set; }
            public bool ClearApiKey { get; set; }
        }

        public class PreviewRequest
        {
            public SimGameState SimState { get; set; }
            public string PlayerId { get; set; }
            public string SessionId { get; set; }     // 可选：带上则使用该会话的真实记忆
        }

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

        public class RetryMemorySummaryRequest
        {
            public string GameId { get; set; }
            public string NpcId { get; set; }
            public string PlayerId { get; set; }
            public string SessionId { get; set; }
        }

        public class MemoryCleanupRequest
        {
            public int InactiveDays { get; set; }
            public bool DryRun { get; set; } = true;
            public int Limit { get; set; } = 200;
        }

        public class UpdateLlmKeyRequest
        {
            public string ApiKey { get; set; }
            public bool Clear { get; set; }
        }

        public static void MapAIBotAdmin(this WebApplication app)
        {
            // JSON→Mongo 记忆迁移互斥锁：避免控制台按钮与启动参数并发执行
            SemaphoreSlim migrateGate = new SemaphoreSlim(1, 1);
            PlayerMemoryService playerMemories = app.Services.GetRequiredService<PlayerMemoryService>();
            MemorySummaryQueue summaryQueue = app.Services.GetRequiredService<MemorySummaryQueue>();
            MemoryAuditService audit = app.Services.GetRequiredService<MemoryAuditService>();
            RuntimeLogService runtimeLogs = app.Services.GetRequiredService<RuntimeLogService>();

            app.MapGet("/api/admin/memory-limits", () =>
                JsonNet(MemoryPolicyService.LoadLimits(app.Configuration)));
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

            // ---- 模型连接与旧配置的脱敏状态 ----
            app.MapGet("/api/admin/settings/llm", () => JsonNet(LlmKeyInventory(app.Configuration, includeOk: false)));
            app.MapPut("/api/admin/settings/llm/npcs/{gid}/{id}/key",
                (string gid, string id, UpdateLlmKeyRequest body) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                string error = ValidateKeyUpdate(body);
                if (error != null) return Results.UnprocessableEntity(new { error });
                AgentConfigDto npc = DataStore.LoadNpc(gid, id);
                if (npc == null) return Results.NotFound("npc not found");
                npc.model ??= new ModelSettings();
                npc.model.apiKey = body.Clear ? null : body.ApiKey.Trim();
                if (!DataStore.SaveNpc(gid, npc)) return Results.Problem("NPC Key 保存失败");
                runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Info, "settings", "llm_key.npc.update",
                    "NPC 主模型 Key 已" + (body.Clear ? "清除" : "更新") + "（值不落日志）: " + gid + "/" + id);
                return JsonNet(LlmKeyInventory(app.Configuration, includeOk: true));
            });

            app.MapPut("/api/admin/settings/llm/games/{gid}/summary-key",
                (string gid, UpdateLlmKeyRequest body) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                string error = ValidateKeyUpdate(body);
                if (error != null) return Results.UnprocessableEntity(new { error });
                MemoryPolicy policy = DataStore.LoadMemoryPolicy(gid);
                if (policy == null) return Results.NotFound("Game 记忆策略不存在");
                if (policy.summaryModel == null) return Results.UnprocessableEntity(new { error = "请先在 Game 记忆策略中启用独立摘要模型" });
                policy.summaryModel.apiKey = body.Clear ? null : body.ApiKey.Trim();
                if (!DataStore.SaveMemoryPolicy(gid, policy)) return Results.Problem("Game 摘要 Key 保存失败");
                runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Info, "settings", "llm_key.game_summary.update",
                    "Game 摘要 Key 已" + (body.Clear ? "清除" : "更新") + "（值不落日志）: " + gid);
                return JsonNet(LlmKeyInventory(app.Configuration, includeOk: true));
            });

            app.MapPut("/api/admin/settings/llm/npcs/{gid}/{id}/summary-key",
                (string gid, string id, UpdateLlmKeyRequest body) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                string error = ValidateKeyUpdate(body);
                if (error != null) return Results.UnprocessableEntity(new { error });
                AgentConfigDto npc = DataStore.LoadNpc(gid, id);
                if (npc == null) return Results.NotFound("npc not found");
                if (npc.memory?.summaryModel == null)
                    return Results.UnprocessableEntity(new { error = "请先在 NPC 记忆策略中启用独立摘要模型；继承的 Game 摘要 Key 可在 Game 行修改" });
                npc.memory.summaryModel.apiKey = body.Clear ? null : body.ApiKey.Trim();
                if (!DataStore.SaveNpc(gid, npc)) return Results.Problem("NPC 摘要 Key 保存失败");
                runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Info, "settings", "llm_key.npc_summary.update",
                    "NPC 摘要 Key 已" + (body.Clear ? "清除" : "更新") + "（值不落日志）: " + gid + "/" + id);
                return JsonNet(LlmKeyInventory(app.Configuration, includeOk: true));
            });

            // ---- Server 模式模型连接配置：一条配置绑定地址、协议、Key 与模型 ----
            app.MapGet("/api/admin/model-connections", () => JsonNet(ModelConnectionStore.RedactedSnapshot()));
            app.MapPost("/api/admin/model-connections", (ModelConnectionSaveRequest body) =>
            {
                if (body?.connection != null)
                    body.connection.id = "conn_" + Guid.NewGuid().ToString("N");
                if (body?.clearApiKey == true)
                    return Results.UnprocessableEntity(new { error = "API Key 必填，不能清除" });
                string error = ModelConnectionStore.Validate(body?.connection, requireKey: true);
                if (error != null) return Results.UnprocessableEntity(new { error });
                if (!ModelConnectionStore.Save(body.connection, body.clearApiKey, create: true))
                    return Results.Conflict(new { error = "连接 ID 已存在" });
                runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Info, "settings", "model_connection.create",
                    "模型连接已创建（Key 不落日志）: " + body.connection.id);
                return JsonNet(ModelConnectionStore.RedactedSnapshot());
            });
            app.MapPut("/api/admin/model-connections/{id}", (string id, ModelConnectionSaveRequest body) =>
            {
                if (body?.connection == null || body.connection.id != id) return Results.BadRequest("连接 ID 不一致");
                if (body.clearApiKey) return Results.UnprocessableEntity(new { error = "API Key 必填，不能清除" });
                string error = ModelConnectionStore.Validate(body.connection);
                if (error != null) return Results.UnprocessableEntity(new { error });
                if (string.IsNullOrWhiteSpace(body.connection.apiKey)
                    && !ModelConnectionStore.HasApiKey(id))
                    return Results.UnprocessableEntity(new { error = "API Key 必填" });
                if (!ModelConnectionStore.Save(body.connection, body.clearApiKey, create: false))
                    return Results.NotFound("连接不存在");
                runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Info, "settings", "model_connection.update",
                    "模型连接已更新（Key 不落日志）: " + id);
                return JsonNet(ModelConnectionStore.RedactedSnapshot());
            });
            app.MapDelete("/api/admin/model-connections/{id}", (string id) =>
            {
                if (!DataStore.IsValidId(id)) return Results.BadRequest("连接 ID 不合法");
                if (!ModelConnectionStore.Delete(id))
                    return Results.Conflict(new { error = "连接不存在或仍被 Game/NPC 使用，请先解除分配" });
                runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Info, "settings", "model_connection.delete",
                    "模型连接已删除: " + id);
                return JsonNet(ModelConnectionStore.RedactedSnapshot());
            });
            app.MapPut("/api/admin/model-connections/binding", (ModelConnectionBindingRequest body) =>
            {
                if (body == null
                    || (body.scope != "global_main" && body.scope != "npc_main"
                        && body.scope != "npc_summary" && body.scope != "game_summary")
                    || (body.scope != "global_main" && !DataStore.IsValidId(body.gameId))
                    || ((body.scope == "npc_main" || body.scope == "npc_summary")
                        && !DataStore.IsValidId(body.npcId))
                    || (!string.IsNullOrEmpty(body.connectionId) && !DataStore.IsValidId(body.connectionId)))
                    return Results.BadRequest("分配参数不合法");
                if (body.scope != "global_main" && !DataStore.ListGameIds().Contains(body.gameId))
                    return Results.NotFound("Game 不存在");
                if ((body.scope == "npc_main" || body.scope == "npc_summary")
                    && DataStore.LoadNpc(body.gameId, body.npcId) == null)
                    return Results.NotFound("NPC 不存在");
                if (!ModelConnectionStore.SetBinding(body))
                    return Results.UnprocessableEntity(new { error = "模型连接不存在或未填写 API Key" });
                runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Info, "settings", "model_connection.bind",
                    "模型连接分配已更新: " + body.scope + " " + body.gameId + "/" + body.npcId);
                return JsonNet(ModelConnectionStore.RedactedSnapshot());
            });
            app.MapPost("/api/admin/model-connections/{id}/test", async (string id, HttpContext http) =>
            {
                ModelConnection connection = ModelConnectionStore.Snapshot().connections
                    .FirstOrDefault(x => x.id == id);
                if (connection == null) return Results.NotFound("连接不存在");
                var settings = new ModelSettings
                {
                    baseUrl = connection.baseUrl, model = connection.model, apiKey = connection.apiKey,
                    temperature = 0f, maxTokens = 8, timeoutMs = 15000
                };
                var request = new LlmRequest
                {
                    Model = settings.model,
                    Messages = new List<LlmMessage> { LlmMessage.System("连通性测试"), LlmMessage.User("reply: ok") },
                    Temperature = 0f, MaxTokens = 8
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await new HttpLlmBackend(settings).ChatStreamAsync(request, new NullSink(), http.RequestAborted);
                    sw.Stop();
                    return JsonNet(new { ok = true, latencyMs = sw.ElapsedMilliseconds, model = settings.model });
                }
                catch (Exception ex)
                {
                    ModelErrorInfo info = ModelErrorContract.Classify(ex);
                    return JsonNet(new { ok = false, code = info.Code, status = info.Status,
                        diagnosis = ModelDiagnostics.Diagnose(ex), latencyMs = sw.ElapsedMilliseconds });
                }
            });

            // ---- Game 列表与创建 ----
            app.MapGet("/api/games", () => JsonNet(new { games = DataStore.ListGameIds() }));            app.MapPost("/api/games", (CreateGameRequest body) =>
            {
                if (!DataStore.IsValidId(body?.GameId)) return Results.BadRequest("非法 gameId（字母数字与 _ . : -，1~128 位）");
                if (DataStore.ListGameIds().Contains(body.GameId, StringComparer.OrdinalIgnoreCase))
                    return Results.Conflict("gameId 已存在: " + body.GameId);
                // 骨架：world.json + memory-policy.json；NPC 之后按需创建（目录自动生成）
                if (!DataStore.SaveWorld(body.GameId, new WorldConfigDto { worldId = body.GameId }))
                    return Results.Problem("创建 world.json 失败");
                if (!DataStore.SaveMemoryPolicy(body.GameId, MemoryPolicy.Defaults()))
                    return Results.Problem("创建 memory-policy.json 失败");
                return Results.Json(new { gameId = body.GameId });
            });

            // ---- 世界观 ----
            app.MapGet("/api/games/{gid}/world", (string gid) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                return Results.Json(DataStore.LoadWorld(gid, "default"));
            });

            app.MapPut("/api/games/{gid}/world", (string gid, WorldConfigDto body) =>
            {
                if (!DataStore.IsValidId(gid) || body == null) return Results.BadRequest("参数非法");
                body.worldId = gid == "default" ? body.worldId : gid;
                return DataStore.SaveWorld(gid, body)
                    ? Results.Json(new { ok = true })
                    : Results.Problem("保存失败：data/ 根目录未找到");
            });

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
                if (!DataStore.SaveMemoryPolicy(gid, validated.policy)) return Results.Problem("保存失败");
                try { audit.RecordRequired(new MemoryAuditEntry
                {
                    gameId = gid,
                    actor = AuditActor(http),
                    action = "policy.game.update",
                    before = JToken.FromObject(MemoryPolicyService.Redact(existing ?? MemoryPolicy.Defaults())),
                    after = JToken.FromObject(MemoryPolicyService.Redact(validated.policy))
                }); }
                catch (Exception ex) { return MemoryError(ex); }
                return JsonNet(MemoryPolicyService.Redact(validated));
            });

            // ---- NPC CRUD ----
            app.MapPost("/api/games/{gid}/npcs", (string gid, CreateNpcRequest body) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                AgentConfigDto dto;
                if (body?.FromTemplate == true || body?.Npc == null)
                {
                    dto = DataStore.LoadTemplate(gid);
                    dto.npcId = body?.NpcId;
                }
                else
                {
                    dto = body.Npc;
                }
                if (!DataStore.IsValidId(dto.npcId)) return Results.BadRequest("npcId 非法（字母数字下划线短横线，1~64位）");
                if (DataStore.LoadNpc(gid, dto.npcId) != null) return Results.Conflict("npcId 已存在: " + dto.npcId);
                dto.hasApiKey = null;   // 回显字段，客户端回传时不得写入配置文件
                return DataStore.SaveNpc(gid, dto)
                    ? Results.Json(RedactSecrets(dto))
                    : Results.Problem("保存失败");
            });

            app.MapGet("/api/games/{gid}/npcs/{id}", (string gid, string id) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                AgentConfigDto dto = DataStore.LoadNpc(gid, id);
                return dto != null ? Results.Json(RedactSecrets(dto)) : Results.NotFound("npc not found: " + id);
            });

            app.MapPut("/api/games/{gid}/npcs/{id}", (string gid, string id, SaveNpcRequest req, HttpContext http) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                AgentConfigDto body = req?.Npc;
                if (body == null || body.npcId != id) return Results.BadRequest("body.npcId 必须与路径一致（或缺少 npc 包裹对象）");
                AgentConfigDto existing = DataStore.LoadNpc(gid, id);
                if (existing == null) return Results.NotFound("npc not found: " + id);
                bool clearKey = req.ClearApiKey;
                body = ResolveNpcSaveBody(body, existing, clearKey);
                if (clearKey)
                    runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Info, "npc", "npc_key.clear",
                        "已清除 NPC 独立 API Key（值不落日志），回退到全局 Key: " + id);
                body.hasApiKey = null;   // 回显字段，客户端回传时不得写入配置文件
                MemorySettings beforeMemory = RedactMemorySettings(existing.memory);
                if (!DataStore.SaveNpc(gid, body)) return Results.Problem("保存失败");
                MemorySettings afterMemory = RedactMemorySettings(body.memory);
                if (JsonConvert.SerializeObject(beforeMemory) != JsonConvert.SerializeObject(afterMemory))
                {
                    try { audit.RecordRequired(new MemoryAuditEntry
                    {
                        gameId = gid,
                        npcId = id,
                        actor = AuditActor(http),
                        action = "policy.npc.update",
                        before = JToken.FromObject(beforeMemory),
                        after = JToken.FromObject(afterMemory)
                    }); }
                    catch (Exception ex) { return MemoryError(ex); }
                }
                return Results.Json(new { ok = true });
            });

            app.MapDelete("/api/games/{gid}/npcs/{id}", (string gid, string id) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                if (!DataStore.DeleteNpc(gid, id)) return Results.NotFound("npc not found: " + id);
                ModelConnectionStore.ClearNpcBindings(gid, id);
                return Results.Json(new { ok = true });
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
                cfg.memory = body;
                if (!DataStore.SaveNpc(gid, cfg)) return Results.Problem("保存失败");
                try { audit.RecordRequired(new MemoryAuditEntry
                {
                    gameId = gid,
                    npcId = id,
                    actor = AuditActor(http),
                    action = "policy.npc.update",
                    before = JToken.FromObject(before),
                    after = JToken.FromObject(RedactMemorySettings(body))
                }); }
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

            // ---- Prompt 分层预览 ----
            app.MapPost("/api/games/{gid}/npcs/{id}/preview-prompt", async
                (string gid, string id, PreviewRequest body, HttpContext http) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                if (!string.IsNullOrEmpty(body?.PlayerId) && !DataStore.IsValidPlayerId(body.PlayerId))
                    return Results.BadRequest("非法 playerId");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, id);
                if (cfg == null) return Results.NotFound("npc not found: " + id);
                WorldConfigDto world = DataStore.LoadWorld(gid, cfg.worldId);

                string summary = null;
                List<string> facts = null;
                EffectiveMemoryPolicy memoryPolicy = MemoryPolicyService.Resolve(gid, cfg, null, app.Configuration);
                if (!string.IsNullOrEmpty(body?.PlayerId)
                    && memoryPolicy.policy.memoryScope == MemoryPolicyValues.ScopePlayerNpc)
                {
                    PlayerLongTermMemory memory = await playerMemories.LoadAsync(gid, id,
                        body.PlayerId, http.RequestAborted);
                    summary = memory.summary;
                    facts = PlayerMemoryService.ToPromptFacts(memory, memoryPolicy.policy);
                }
                else if (!string.IsNullOrEmpty(body?.SessionId))
                {
                    foreach (SessionState s in SessionStore.ListByGame(gid, id, body?.PlayerId))
                    {
                        if (s.SessionId == body.SessionId)
                        {
                            summary = s.Summary;
                            facts = s.Facts;
                            break;
                        }
                    }
                }

                var builder = new ContextBuilder();
                List<ContextBuilder.ContextLayer> layers = builder.BuildLayers(
                    cfg, world, new SimGameContext(body?.SimState ?? new SimGameState()), summary, facts);

                var layerArray = new JArray();
                var fullPrompt = new System.Text.StringBuilder();
                int total = 0;
                string[] palette = { "#4a9eff", "#ffb347", "#b07cff", "#5fd3a0", "#ff8fa3", "#e0d268", "#7ee0d6" };
                for (int i = 0; i < layers.Count; i++)
                {
                    ContextBuilder.ContextLayer layer = layers[i];
                    int est = TokenBudget.Estimate(layer.Text);
                    total += est;
                    fullPrompt.Append(layer.Text);
                    layerArray.Add(new JObject
                    {
                        ["name"] = layer.Name,
                        ["text"] = layer.Text,
                        ["estTokens"] = est,
                        ["color"] = palette[i % palette.Length]
                    });
                }

                return JsonNet(new JObject
                {
                    ["systemPrompt"] = fullPrompt.ToString(),
                    ["layers"] = layerArray,
                    ["totalEstTokens"] = total,
                    ["budget"] = TokenBudget.DefaultBudget
                });
            });

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
                try { audit.RecordRequired(new MemoryAuditEntry
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
                }); }
                catch (Exception ex) { return MemoryError(ex); }
                return Results.Json(new { ok = true });
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

            // ---- 连接测试 ----
            app.MapPost("/api/games/{gid}/npcs/{id}/test-connection", async (string gid, string id, TestConnectionRequest body) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, id);
                if (cfg == null) return Results.NotFound("npc not found: " + id);
                cfg.model = ModelConnectionStore.ResolveMain(gid, id, cfg.model);

                var settings = new AIBot.Core.Config.ModelSettings
                {
                    baseUrl = string.IsNullOrEmpty(body?.BaseUrl) ? cfg.model.baseUrl : body.BaseUrl,
                    model = string.IsNullOrEmpty(body?.Model) ? cfg.model.model : body.Model,
                    apiKey = string.IsNullOrEmpty(body?.ApiKey) ? cfg.model.apiKey : body.ApiKey,
                    temperature = 0f,
                    maxTokens = 8,
                    timeoutMs = 15000
                };

                var request = new AIBot.Core.Llm.LlmRequest
                {
                    Model = settings.model,
                    Messages = new List<AIBot.Core.Llm.LlmMessage>
                    {
                        AIBot.Core.Llm.LlmMessage.System("连通性测试"),
                        AIBot.Core.Llm.LlmMessage.User("reply: ok")
                    },
                    Temperature = 0f,
                    MaxTokens = 8
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await new AIBot.Core.Llm.HttpLlmBackend(settings).ChatStreamAsync(
                        request, new NullSink(), default(System.Threading.CancellationToken));
                    sw.Stop();
                    return JsonNet(new JObject
                    {
                        ["ok"] = true,
                        ["latencyMs"] = sw.ElapsedMilliseconds,
                        ["model"] = settings.model,
                        ["endpoint"] = settings.baseUrl
                    });
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    ModelErrorInfo info = ModelErrorContract.Classify(ex);
                    return JsonNet(new JObject
                    {
                        ["ok"] = false,
                        ["error"] = info.Message,
                        ["code"] = info.Code,
                        ["status"] = info.Status,
                        ["retryable"] = info.Retryable,
                        ["diagnosis"] = ModelDiagnostics.Diagnose(ex),
                        ["endpoint"] = settings.baseUrl,
                        ["model"] = settings.model
                    });
                }
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

        /// <summary>用 Newtonsoft 序列化返回（JObject/JArray 必须走这里，STJ 序列化 JToken 会丢值）。</summary>
        private static IResult JsonNet(object value)
        {
            return Results.Content(JsonConvert.SerializeObject(value), "application/json");
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

        private static string AuditActor(HttpContext http)
        {
            string actor = http?.Request.Headers["X-AIBot-Actor"].ToString()?.Trim();
            if (!string.IsNullOrEmpty(actor))
            {
                actor = actor.Replace("\r", string.Empty).Replace("\n", string.Empty);
                return actor.Length <= 128 ? actor : actor.Substring(0, 128);
            }
            string ip = http?.Connection.RemoteIpAddress?.ToString();
            return string.IsNullOrEmpty(ip) ? "admin" : "admin@" + ip;
        }

        private static IResult MemoryError(Exception ex)
        {
            var conflict = ex as MemoryVersionConflictException;
            if (conflict != null)
                return Results.Conflict(new
                {
                    error = "memoryVersion 已变化，请刷新后重试",
                    expectedVersion = conflict.ExpectedVersion,
                    actualVersion = conflict.ActualVersion
                });
            if (ex is MemoryValidationException)
                return Results.UnprocessableEntity(new { error = ex.Message });
            if (ex is KeyNotFoundException)
                return Results.NotFound(new { error = ex.Message });
            if (ex is ArgumentException)
                return Results.BadRequest(new { error = ex.Message });
            if (ex is MemoryAuditWriteException)
                return Results.Problem(
                    detail: "业务数据可能已经写入，但审计日志写入失败；请检查磁盘与 data/logs 权限后重试或核对结果。",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "记忆审计服务不可用");
            return Results.Problem("记忆操作失败");
        }

        private static AgentConfigDto RedactSecrets(AgentConfigDto source)
        {
            AgentConfigDto clone = JsonConvert.DeserializeObject<AgentConfigDto>(
                JsonConvert.SerializeObject(source));
            if (clone?.model != null)
            {
                clone.hasApiKey = !string.IsNullOrEmpty(source?.model?.apiKey);
                clone.model.apiKey = string.Empty;
            }
            if (clone?.memory?.summaryModel != null) clone.memory.summaryModel.apiKey = string.Empty;
            return clone;
        }

        private const int MaskPrefixLength = 7;
        private const int MaskTailLength = 4;
        private const string MaskStars = "*****";
        /// <summary>短于此长度时前后缀合计占比过大，等于把 key 还原出来，直接整体遮蔽。</summary>
        private const int MaskMinKeyLength = 16;

        /// <summary>
        /// 脱敏展示：前 7 位 + 固定 5 星号 + 末 4 位（如 sk-ca1a2*****bd31），够人工辨认是哪把 key。
        /// 星号数量写死不随原串长度变化，顺带不泄露密钥长度；短 key 一律 ***。
        /// </summary>
        private static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.Length < MaskMinKeyLength) return "***";
            return key.Substring(0, MaskPrefixLength) + MaskStars + key.Substring(key.Length - MaskTailLength);
        }

        private static string ValidateKeyUpdate(UpdateLlmKeyRequest body)
        {
            if (body == null || (!body.Clear && string.IsNullOrWhiteSpace(body.ApiKey)))
                return "apiKey 必填；如需清除请显式传 clear=true";
            if (body.Clear) return null;
            string key = body.ApiKey.Trim();
            if (key.Length < 8 || key.Length > 4096) return "apiKey 长度需在 8~4096 之间";
            if (key.Any(char.IsControl)) return "apiKey 含非法控制字符";
            return null;
        }

        /// <summary>统一管理页的脱敏快照。只读取配置，不将任何 Key 明文序列化。</summary>
        private static JObject LlmKeyInventory(IConfiguration config, bool includeOk)
        {
            JObject status = new JObject();
            if (includeOk) status["ok"] = true;
            ModelConnectionDocument connectionDoc = ModelConnectionStore.Snapshot();
            ModelConnection globalConnection = connectionDoc.connections
                .FirstOrDefault(x => x.id == connectionDoc.globalMain);
            status["globalConnectionName"] = globalConnection?.name;
            status["globalConnectionMaskedKey"] = MaskKey(globalConnection?.apiKey);
            var games = new JArray();
            var npcs = new JArray();
            foreach (string gid in DataStore.ListGameIds())
            {
                MemoryPolicy gamePolicy = DataStore.LoadMemoryPolicy(gid);
                string gameSummaryKey = gamePolicy?.summaryModel?.apiKey;
                connectionDoc.gameSummary.TryGetValue(gid, out string gameConnectionId);
                ModelConnection gameConnection = connectionDoc.connections
                    .FirstOrDefault(x => x.id == gameConnectionId);
                games.Add(new JObject
                {
                    ["gameId"] = gid,
                    ["hasSummaryModel"] = gamePolicy?.summaryModel != null,
                    ["summaryKeyConfigured"] = !string.IsNullOrWhiteSpace(gameSummaryKey),
                    ["summaryMaskedKey"] = MaskKey(gameSummaryKey),
                    ["connectionName"] = gameConnection?.name,
                    ["connectionMaskedKey"] = MaskKey(gameConnection?.apiKey)
                });
                foreach (string id in DataStore.ListNpcIds(gid))
                {
                    AgentConfigDto npc = DataStore.LoadNpc(gid, id);
                    if (npc == null) continue;
                    string mainKey = npc.model?.apiKey;
                    string pair = gid + "/" + id;
                    connectionDoc.npcMain.TryGetValue(pair, out string mainConnectionId);
                    ModelConnection mainConnection = connectionDoc.connections.FirstOrDefault(x => x.id == mainConnectionId)
                        ?? globalConnection;
                    string mainSource = mainConnection != null
                        ? mainConnection.id == mainConnectionId ? "connection" : "global_connection"
                        : string.IsNullOrWhiteSpace(mainKey) ? "none" : "npc";
                    string effectiveMainKey = mainConnection?.apiKey ?? mainKey;
                    string ownSummaryKey = npc.memory?.summaryModel?.apiKey;
                    EffectiveMemoryPolicy effective = MemoryPolicyService.Resolve(gid, npc, null, config);
                    string effectiveSummaryKey = effective.policy.summaryModel?.apiKey;
                    connectionDoc.npcSummary.TryGetValue(pair, out string summaryConnectionId);
                    if (summaryConnectionId == null && npc.memory?.useMainSummaryModel != true
                        && npc.memory?.summaryModel == null && npc.memory?.inheritGameDefaults == true)
                        connectionDoc.gameSummary.TryGetValue(gid, out summaryConnectionId);
                    ModelConnection summaryConnection = connectionDoc.connections.FirstOrDefault(x => x.id == summaryConnectionId);
                    string policySource = effective.sources.TryGetValue("summaryModel", out string source)
                        ? source : "core";
                    bool hasDedicatedSummaryKey = !string.IsNullOrWhiteSpace(effectiveSummaryKey);
                    npcs.Add(new JObject
                    {
                        ["gameId"] = gid,
                        ["npcId"] = id,
                        ["displayName"] = npc.displayName ?? id,
                        ["mainKeyConfigured"] = !string.IsNullOrWhiteSpace(mainKey),
                        ["mainMaskedKey"] = MaskKey(mainKey),
                        ["mainSource"] = mainSource,
                        ["mainConnectionName"] = mainConnection?.name,
                        ["mainEffectiveMaskedKey"] = MaskKey(effectiveMainKey),
                        ["npcSummaryModel"] = npc.memory?.summaryModel != null,
                        ["npcSummaryKeyConfigured"] = !string.IsNullOrWhiteSpace(ownSummaryKey),
                        ["npcSummaryMaskedKey"] = MaskKey(ownSummaryKey),
                        ["summarySource"] = summaryConnection != null ? "connection"
                            : hasDedicatedSummaryKey ? policySource : "main",
                        ["summaryConnectionName"] = summaryConnection?.name,
                        ["summaryConnectionScope"] = summaryConnection == null ? null
                            : connectionDoc.npcSummary.ContainsKey(pair) ? "npc" : "game",
                        ["summaryMainSource"] = summaryConnection != null || hasDedicatedSummaryKey ? null : mainSource,
                        ["summaryMaskedKey"] = MaskKey(summaryConnection != null ? summaryConnection.apiKey
                            : hasDedicatedSummaryKey ? effectiveSummaryKey : effectiveMainKey)
                    });
                }
            }
            status["games"] = games;
            status["npcs"] = npcs;
            return status;
        }

        /// <summary>
        /// 决定 PUT /npcs/{id} 落盘用的配置体，处理"留空不改 / 显式清除"两种 apiKey 语义。
        /// clearKey=true 时以已存配置为基准原地清除独立 key（主模型 + 摘要模型），最小负载即可，不误抹其他字段。
        /// </summary>
        internal static AgentConfigDto ResolveNpcSaveBody(AgentConfigDto submitted, AgentConfigDto existing, bool clearKey)
        {
            if (clearKey)
            {
                if (existing.model != null) existing.model.apiKey = null;
                if (existing.memory?.summaryModel != null) existing.memory.summaryModel.apiKey = null;
                return existing;
            }
            // 编辑器清空 apiKey 视为"不改"，避免误把已配置的 key 抹掉
            if (submitted.model != null && string.IsNullOrEmpty(submitted.model.apiKey))
                submitted.model.apiKey = existing.model?.apiKey;
            if (submitted.memory?.summaryModel != null
                && string.IsNullOrEmpty(submitted.memory.summaryModel.apiKey))
                submitted.memory.summaryModel.apiKey = existing.memory?.summaryModel?.apiKey;
            return submitted;
        }

        /// <summary>可选 npcId 过滤参数：空串与缺省等价于"不过滤"。归一化为 null，
        /// 服务层（ListByGame/ListAsync/Query）只认 null=不过滤，空串会被误当成过滤值。</summary>
        private static string OptionalNpcId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static MemorySettings RedactMemorySettings(MemorySettings source)
        {
            MemorySettings clone = JsonConvert.DeserializeObject<MemorySettings>(
                JsonConvert.SerializeObject(source ?? new MemorySettings()));
            if (clone.summaryModel != null) clone.summaryModel.apiKey = string.Empty;
            return clone;
        }
    }

    public class TestConnectionRequest
    {
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
    }

    /// <summary>连接测试用的静默 sink。</summary>
    internal sealed class NullSink : AIBot.Core.Llm.ILlmStreamSink
    {
        public void OnToken(string delta) { }
        public void OnToolCall(AIBot.Core.Llm.ToolCallDto call) { }
        public void OnCompleted(string fullText, AIBot.Core.Llm.Usage usage) { }
        public void OnError(System.Exception ex) { }
    }
}

