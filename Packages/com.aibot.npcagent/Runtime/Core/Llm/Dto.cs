using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Core.Llm
{
    /// <summary>OpenAI 兼容 chat/completions 请求体。字段名与协议一致（snake_case）。</summary>
    public sealed class LlmRequest
    {
        [JsonProperty("model")] public string Model;
        [JsonProperty("messages")] public List<LlmMessage> Messages = new List<LlmMessage>();
        [JsonProperty("tools")] public List<ToolSchema> Tools;
        [JsonProperty("stream")] public bool Stream = true;
        [JsonProperty("temperature")] public float Temperature = 0.8f;
        [JsonProperty("max_tokens")] public int MaxTokens = 500;
        [JsonProperty("response_format")] public ResponseFormat ResponseFormat;

        public bool ShouldSerializeTools() { return Tools != null && Tools.Count > 0; }
        public bool ShouldSerializeResponseFormat() { return ResponseFormat != null; }
    }

    /// <summary>结构化输出开关。与 tools 同用可能被供应商拒绝（§5.5 策略：带工具时禁用）。</summary>
    public sealed class ResponseFormat
    {
        [JsonProperty("type")] public string Type;
        public static ResponseFormat JsonObject() { return new ResponseFormat { Type = "json_object" }; }
    }

    public sealed class LlmMessage
    {
        [JsonProperty("role")] public string Role;              // system / user / assistant / tool
        [JsonProperty("content")] public string Content;        // role=tool 时为工具结果文本
        [JsonProperty("tool_calls")] public List<ToolCallDto> ToolCalls;
        [JsonProperty("tool_call_id")] public string ToolCallId;

        public bool ShouldSerializeToolCalls() { return ToolCalls != null && ToolCalls.Count > 0; }
        public bool ShouldSerializeToolCallId() { return ToolCallId != null; }

        public static LlmMessage System(string content) { return new LlmMessage { Role = "system", Content = content }; }
        public static LlmMessage User(string content) { return new LlmMessage { Role = "user", Content = content }; }
        public static LlmMessage Assistant(string content) { return new LlmMessage { Role = "assistant", Content = content }; }
    }

    /// <summary>上游 OpenAI 原生形状的 tool_call（流式分片由 OpenAiStreamAggregator 聚合后才是完整对象）。</summary>
    public sealed class ToolCallDto
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("type")] public string Type = "function";
        [JsonProperty("function")] public FunctionCall Function;
    }

    public sealed class FunctionCall
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("arguments")] public string Arguments;    // JSON 字符串
    }

    public sealed class ToolSchema
    {
        [JsonProperty("type")] public string Type = "function";
        [JsonProperty("function")] public FunctionDef Function;
    }

    public sealed class FunctionDef
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("description")] public string Description;
        [JsonProperty("parameters")] public JObject Parameters;
    }

    public sealed class Usage
    {
        [JsonProperty("prompt_tokens")] public int PromptTokens;
        [JsonProperty("completion_tokens")] public int CompletionTokens;
        [JsonProperty("total_tokens")] public int TotalTokens;
    }
}
