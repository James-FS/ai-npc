using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIBot.Core.Llm;
using Newtonsoft.Json.Linq;

namespace AIBot.Core.Tools
{
    /// <summary>宿主注册的工具。游戏端真实执行；测试台由 SimulatedToolHost 记录不执行。</summary>
    public interface IAgentTool
    {
        string Id { get; }
        string Description { get; }              // 给模型看的用途说明
        string ParametersSchema { get; }         // JSON Schema 字符串
        Task<ToolResult> ExecuteAsync(string argsJson, object hostContext);
    }

    public class ToolResult
    {
        public bool Success;
        public string MessageForModel;           // 回传给模型的执行结果
        public static ToolResult Ok(string message = "done") { return new ToolResult { Success = true, MessageForModel = message }; }
        public static ToolResult Fail(string why) { return new ToolResult { Success = false, MessageForModel = why }; }
    }

    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, IAgentTool> _tools = new Dictionary<string, IAgentTool>();

        /// <summary>当前宿主已注册的工具数量，供运行时能力提示使用。</summary>
        public int Count { get { return _tools.Count; } }

        /// <summary>已注册的工具 id 快照（game 模式向 Server 上传 schema 用）。</summary>
        public List<string> Ids { get { return new List<string>(_tools.Keys); } }

        public void Register(IAgentTool tool)
        {
            if (tool == null || string.IsNullOrEmpty(tool.Id)) throw new ArgumentException("tool id required");
            _tools[tool.Id] = tool;
        }

        public bool TryGet(string id, out IAgentTool tool) { return _tools.TryGetValue(id, out tool); }

        /// <summary>按配置启用的 id 列表生成 OpenAI tools 描述。未知 id 跳过（记 Warning 由宿主日志处理）。</summary>
        public List<ToolSchema> BuildSchemas(IEnumerable<string> enabledIds)
        {
            var schemas = new List<ToolSchema>();
            if (enabledIds == null) return schemas;
            foreach (string id in enabledIds)
            {
                IAgentTool tool;
                if (!_tools.TryGetValue(id, out tool)) continue;
                schemas.Add(new ToolSchema
                {
                    Function = new FunctionDef
                    {
                        Name = tool.Id,
                        Description = tool.Description,
                        Parameters = ParseSchema(tool.ParametersSchema)
                    }
                });
            }
            return schemas;
        }

        public async Task<ToolResult> ExecuteAsync(string id, string argsJson, object hostContext)
        {
            IAgentTool tool;
            if (!_tools.TryGetValue(id, out tool)) return ToolResult.Fail("unknown tool: " + id);
            try
            {
                string args = string.IsNullOrEmpty(argsJson) ? "{}" : argsJson;
                return await tool.ExecuteAsync(args, hostContext);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("tool error: " + ex.Message);
            }
        }

        private static JObject ParseSchema(string json)
        {
            if (string.IsNullOrEmpty(json)) return JObject.Parse("{\"type\":\"object\",\"properties\":{}}");
            return JObject.Parse(json);
        }
    }
}
