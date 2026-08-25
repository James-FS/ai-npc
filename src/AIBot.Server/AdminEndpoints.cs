using System;
using System.Collections.Generic;
using AIBot.Core.Config;
using AIBot.Core.Context;
using AIBot.Core.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        public class PreviewRequest
        {
            public SimGameState SimState { get; set; }
            public string SessionId { get; set; }     // 可选：带上则使用该会话的真实记忆
        }

        public static void MapAIBotAdmin(this WebApplication app)
        {
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
                return DataStore.SaveNpc(gid, dto)
                    ? Results.Json(dto)
                    : Results.Problem("保存失败");
            });

            app.MapGet("/api/games/{gid}/npcs/{id}", (string gid, string id) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                AgentConfigDto dto = DataStore.LoadNpc(gid, id);
                return dto != null ? Results.Json(dto) : Results.NotFound("npc not found: " + id);
            });

            app.MapPut("/api/games/{gid}/npcs/{id}", (string gid, string id, AgentConfigDto body) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                if (body == null || body.npcId != id) return Results.BadRequest("body.npcId 必须与路径一致");
                AgentConfigDto existing = DataStore.LoadNpc(gid, id);
                if (existing == null) return Results.NotFound("npc not found: " + id);
                if (string.IsNullOrEmpty(body.model?.apiKey) && body.model != null)
                {
                    // 编辑器清空 apiKey 视为"不改"，避免误把已配置的 key 抹掉
                    body.model.apiKey = existing.model?.apiKey;
                }
                return DataStore.SaveNpc(gid, body)
                    ? Results.Json(new { ok = true })
                    : Results.Problem("保存失败");
            });

            app.MapDelete("/api/games/{gid}/npcs/{id}", (string gid, string id) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                return DataStore.DeleteNpc(gid, id)
                    ? Results.Json(new { ok = true })
                    : Results.NotFound("npc not found: " + id);
            });

            // ---- Prompt 分层预览 ----
            app.MapPost("/api/games/{gid}/npcs/{id}/preview-prompt", (string gid, string id, PreviewRequest body) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, id);
                if (cfg == null) return Results.NotFound("npc not found: " + id);
                WorldConfigDto world = DataStore.LoadWorld(gid, cfg.worldId);

                string summary = null;
                List<string> facts = null;
                if (!string.IsNullOrEmpty(body?.SessionId))
                {
                    foreach (SessionState s in SessionStore.ListByGame(gid, id))
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
            app.MapGet("/api/games/{gid}/sessions", (string gid, string npcId) =>
            {
                if (!DataStore.IsValidId(gid)) return Results.BadRequest("非法 gameId");
                if (npcId != null && !DataStore.IsValidId(npcId)) return Results.BadRequest("非法 npcId");
                var array = new JArray();
                foreach (SessionState s in SessionStore.ListByGame(gid, npcId))
                {
                    array.Add(new JObject
                    {
                        ["sessionId"] = s.SessionId,
                        ["npcId"] = s.NpcId,
                        ["messageCount"] = s.Memory.Messages.Count,
                        ["hasSummary"] = !string.IsNullOrEmpty(s.Summary),
                        ["factCount"] = s.Facts.Count,
                        ["lastActiveUtc"] = s.LastActiveUtc
                    });
                }
                return JsonNet(new { sessions = array });
            });

            app.MapGet("/api/games/{gid}/sessions/{sid}", (string gid, string sid, string npcId) =>
            {
                if (!DataStore.IsValidId(gid) || string.IsNullOrEmpty(sid)) return Results.BadRequest("参数非法");
                if (npcId == null || !DataStore.IsValidId(npcId)) return Results.BadRequest("需要 npcId 查询参数");
                // 走 GetOrCreate：仅存于磁盘的会话被真实加载（列表里的磁盘条目只是占位）
                AgentConfigDto cfg = DataStore.LoadNpc(gid, npcId);
                SessionState s = SessionStore.GetOrCreate(gid, npcId, sid, cfg != null ? cfg.memory.shortTermTurns : 12);
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
                    ["messages"] = messages,
                    ["summary"] = s.Summary,
                    ["facts"] = facts
                });
            });
            app.MapDelete("/api/games/{gid}/sessions/{sid}", (string gid, string sid, string npcId) =>
            {
                if (!DataStore.IsValidId(gid) || string.IsNullOrEmpty(sid)) return Results.BadRequest("参数非法");
                if (npcId == null || !DataStore.IsValidId(npcId)) return Results.BadRequest("需要 npcId 查询参数");
                return SessionStore.Delete(gid, npcId, sid)
                    ? Results.Json(new { ok = true })
                    : Results.NotFound("session not found");
            });

            // ---- 连接测试 ----
            app.MapPost("/api/games/{gid}/npcs/{id}/test-connection", async (string gid, string id, TestConnectionRequest body) =>
            {
                if (!DataStore.IsValidId(gid) || !DataStore.IsValidId(id)) return Results.BadRequest("非法 ID");
                AgentConfigDto cfg = DataStore.LoadNpc(gid, id);
                if (cfg == null) return Results.NotFound("npc not found: " + id);

                var settings = new AIBot.Core.Config.ModelSettings
                {
                    baseUrl = string.IsNullOrEmpty(body?.BaseUrl) ? cfg.model.baseUrl : body.BaseUrl,
                    model = string.IsNullOrEmpty(body?.Model) ? cfg.model.model : body.Model,
                    apiKey = string.IsNullOrEmpty(body?.ApiKey)
                        ? (cfg.model.apiKey
                           ?? Environment.GetEnvironmentVariable("AIBOT_LLM_KEY")
                           ?? app.Configuration["Llm:ApiKey"])
                        : body.ApiKey,
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
                    return JsonNet(new JObject
                    {
                        ["ok"] = false,
                        ["error"] = ex.Message,
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
