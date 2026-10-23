using System.Collections.Generic;
using AIBot.Core.Context;
using AIBot.Core.Memory;
using AIBot.Core.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AIBot.Server
{
    /// <summary>对话请求体（对应主方案 §7.1 契约）。</summary>
    public class ChatRequestBody
    {
        public string NpcId { get; set; }
        public string PlayerId { get; set; }
        public string SessionId { get; set; } = "s-local";
        public string RequestId { get; set; }
        public string Message { get; set; }
        public SimGameState SimState { get; set; }
        public OverrideSettings Override { get; set; }
        public MemoryPolicyOverrides MemoryOverride { get; set; } // 管理端调试临时覆盖，Server 最终仍应用安全上限
        public string ToolMode { get; set; } = ServerToolModes.None; // none（正式默认）/ simulated（仅调试）/ game（工具回传）
        public List<ClientToolDescriptor> Tools { get; set; } // game：客户端上传的工具描述（首段必带）
        public string RoundToken { get; set; }             // game 续跑：待续挂起轮令牌
        public List<ClientToolResult> ToolResults { get; set; } // game 续跑：工具执行结果
        public string GameContext { get; set; }            // game：原始游戏状态快照（仅作 prompt 上下文，不进指纹）
    }

    public class OverrideSettings
    {
        public string Model { get; set; }
        public float? Temperature { get; set; }
    }

    /// <summary>聊天路由和请求契约；业务流程由 ChatRequestHandler 处理。</summary>
    public static class ChatEndpoints
    {
        public static void MapAIBotChat(this WebApplication app)
        {
            var handler = new ChatRequestHandler(app);
            app.MapGet("/api/games/{gid}/npcs", (string gid) =>
                DataStore.IsValidId(gid)
                    ? Results.Json(new { gameId = gid, npcs = DataStore.ListNpcIds(gid) })
                    : Results.BadRequest("非法 gameId"));
            app.MapPost("/api/games/{gid}/chat/stream", handler.HandleAsync)
                .RequireRateLimiting("chat");
        }
    }
}
