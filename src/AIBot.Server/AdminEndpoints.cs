using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>管理 API 入口及配置端点：模型连接、Game、NPC、世界观和 Prompt 预览。</summary>
    public static partial class AdminEndpoints
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

        public class UpdateLlmKeyRequest
        {
            public string ApiKey { get; set; }
            public bool Clear { get; set; }
        }

        public static void MapAIBotAdmin(this WebApplication app)
        {
            MapConfiguration(app);
            MapMemory(app);
            MapOperations(app);
        }

        private static void MapConfiguration(WebApplication app)
        {
            PlayerMemoryService playerMemories = app.Services.GetRequiredService<PlayerMemoryService>();
            MemoryAuditService audit = app.Services.GetRequiredService<MemoryAuditService>();
            RuntimeLogService runtimeLogs = app.Services.GetRequiredService<RuntimeLogService>();

            // ---- 模型连接与旧配置的脱敏状态 ----
            app.MapGet("/api/admin/settings/llm", () => JsonNet(LlmKeyInventory(app.Configuration, includeOk: false)));
            app.MapPut("/api/admin/settings/llm/npcs/{gid}/{id}/key",
                (string gid, string id) => Results.Json(new { error = "模型密钥已统一在 /api/admin/model-connections 管理" }, statusCode: 410));

            app.MapPut("/api/admin/settings/llm/games/{gid}/summary-key",
                (string gid) => Results.Json(new { error = "模型密钥已统一在 /api/admin/model-connections 管理" }, statusCode: 410));

            app.MapPut("/api/admin/settings/llm/npcs/{gid}/{id}/summary-key",
                (string gid, string id) => Results.Json(new { error = "模型密钥已统一在 /api/admin/model-connections 管理" }, statusCode: 410));

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
                    baseUrl = connection.baseUrl,
                    model = connection.model,
                    apiKey = connection.apiKey,
                    temperature = 0f,
                    maxTokens = 8,
                    timeoutMs = 15000
                };
                var request = new LlmRequest
                {
                    Model = settings.model,
                    Messages = new List<LlmMessage> { LlmMessage.System("连通性测试"), LlmMessage.User("reply: ok") },
                    Temperature = 0f,
                    MaxTokens = 8
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
                    return JsonNet(new
                    {
                        ok = false,
                        code = info.Code,
                        status = info.Status,
                        diagnosis = ModelDiagnostics.Diagnose(ex),
                        latencyMs = sw.ElapsedMilliseconds
                    });
                }
            });

            // ---- Game 列表与创建 ----
            app.MapGet("/api/games", () => JsonNet(new { games = DataStore.ListGameIds() }));
            app.MapPost("/api/games", (CreateGameRequest body) =>
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
                StripServerConnectionDetails(dto.model);
                StripServerConnectionDetails(dto.memory?.summaryModel);
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
                StripServerConnectionDetails(body.model);
                StripServerConnectionDetails(body.memory?.summaryModel);
                MemorySettings beforeMemory = RedactMemorySettings(existing.memory);
                if (!DataStore.SaveNpc(gid, body)) return Results.Problem("保存失败");
                MemorySettings afterMemory = RedactMemorySettings(body.memory);
                if (JsonConvert.SerializeObject(beforeMemory) != JsonConvert.SerializeObject(afterMemory))
                {
                    try
                    {
                        audit.RecordRequired(new MemoryAuditEntry
                        {
                            gameId = gid,
                            npcId = id,
                            actor = AuditActor(http),
                            action = "policy.npc.update",
                            before = JToken.FromObject(beforeMemory),
                            after = JToken.FromObject(afterMemory)
                        });
                    }
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

        }


        /// <summary>用 Newtonsoft 序列化返回（JObject/JArray 必须走这里，STJ 序列化 JToken 会丢值）。</summary>
        private static IResult JsonNet(object value)
        {
            return Results.Content(JsonConvert.SerializeObject(value), "application/json");
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

        private static void StripServerConnectionDetails(ModelSettings settings)
        {
            if (settings == null) return;
            settings.baseUrl = null;
            settings.model = null;
            settings.apiKey = null;
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
