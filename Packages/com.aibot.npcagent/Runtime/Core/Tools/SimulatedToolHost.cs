using System;
using System.Threading.Tasks;
using AIBot.Core.Context;
using Newtonsoft.Json.Linq;

namespace AIBot.Core.Tools
{
    /// <summary>
    /// 模拟工具集（主方案 §7.2 SimulatedToolHost）：测试台/无游戏环境下的"游戏环境替身"。
    /// 真实读写 SimGameState（好感度/剧情阶段/背包），结果回传模型——NPC 能"真的"给道具、改好感，
    /// 状态随会话持久化。游戏端换成真实 IAgentTool 实现即可。
    /// </summary>
    public sealed class SimulatedToolHost
    {
        public const string GiveItemId = "give_item";
        public const string ChangeFavorId = "change_favor";
        public const string AdvanceStageId = "advance_stage";

        private readonly SimGameState _state;

        public SimulatedToolHost(SimGameState state)
        {
            _state = state ?? new SimGameState();
        }

        /// <summary>注册全部模拟工具（是否暴露给模型由 NPC 配置的 enabledToolIds 决定）。</summary>
        public void RegisterAll(ToolRegistry registry)
        {
            registry.Register(new SimTool(GiveItemId, "送给玩家一件道具",
                "{\"type\":\"object\",\"properties\":{\"item_id\":{\"type\":\"string\",\"description\":\"道具ID\"},\"count\":{\"type\":\"integer\",\"description\":\"数量，默认1\"}},\"required\":[\"item_id\"]}",
                args =>
                {
                    string item = args["item_id"]?.ToString() ?? "?";
                    int count = args.Value<int?>("count") ?? 1;
                    _state.items[item] = _state.GetItemCount(item) + count;
                    return "已给玩家 " + item + " x" + count + "（当前持有 x" + _state.items[item] + "）";
                }));
            registry.Register(new SimTool(ChangeFavorId, "调整你对玩家的好感度",
                "{\"type\":\"object\",\"properties\":{\"delta\":{\"type\":\"integer\",\"description\":\"变化量，正数=增加\"}},\"required\":[\"delta\"]}",
                args =>
                {
                    int delta = args.Value<int?>("delta") ?? 0;
                    int before = _state.favorability;
                    _state.favorability += delta;
                    return "好感度 " + before + " → " + _state.favorability;
                }));
            registry.Register(new SimTool(AdvanceStageId, "推进剧情阶段",
                "{\"type\":\"object\",\"properties\":{\"delta\":{\"type\":\"integer\",\"description\":\"推进的章节数，默认1\"}}}",
                args =>
                {
                    int delta = args.Value<int?>("delta") ?? 1;
                    _state.stage += delta;
                    return "剧情推进到阶段 " + _state.stage;
                }));
        }

        private sealed class SimTool : IAgentTool
        {
            private readonly string _id;
            private readonly string _description;
            private readonly string _schema;
            private readonly Func<JObject, string> _run;

            public SimTool(string id, string description, string schema, Func<JObject, string> run)
            {
                _id = id; _description = description; _schema = schema; _run = run;
            }

            public string Id { get { return _id; } }
            public string Description { get { return _description; } }
            public string ParametersSchema { get { return _schema; } }

            public Task<ToolResult> ExecuteAsync(string argsJson, object hostContext)
            {
                try
                {
                    JObject args = string.IsNullOrEmpty(argsJson) ? new JObject() : JObject.Parse(argsJson);
                    return Task.FromResult(ToolResult.Ok(_run(args)));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail("参数错误: " + ex.Message));
                }
            }
        }
    }
}
