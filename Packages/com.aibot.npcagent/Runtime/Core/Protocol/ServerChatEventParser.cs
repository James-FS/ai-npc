using System;
using AIBot.Core.Llm;
using AIBot.Core.Output;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Core.Protocol
{
    /// <summary>Server 工具执行策略。默认 none，避免把调试模拟结果误当成正式游戏业务结果。</summary>
    public static class ServerToolModes
    {
        public const string None = "none";
        public const string Simulated = "simulated";

        public static bool TryNormalize(string value, out string normalized)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? None : value.Trim().ToLowerInvariant();
            if (candidate == None || candidate == Simulated)
            {
                normalized = candidate;
                return true;
            }
            normalized = null;
            return false;
        }
    }

    /// <summary>Unity、Web 与 Server 共用的幂等请求标识规则。</summary>
    public static class ChatRequestIds
    {
        public static string NewId() { return Guid.NewGuid().ToString("N"); }

        public static bool IsValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 80) return false;
            foreach (char c in value)
            {
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ':' || c == '-';
                if (!valid) return false;
            }
            return true;
        }
    }

    /// <summary>AIBot.Server 下行 SSE 事件类型。token.delta 已经是可直接展示的 NPC 台词。</summary>
    public enum ServerChatEventKind
    {
        Unknown,
        Token,
        Reasoning,
        ToolCall,
        Reply,
        Error,
        Done
    }

    public sealed class ServerModelDiagnostic
    {
        public string Code;
        public int Status;
        public string Message;
        public bool Retryable;
    }

    public sealed class ServerChatEvent
    {
        public ServerChatEventKind Kind;
        public string Delta;
        public StructuredReply Reply;
        public Usage Usage = new Usage();
        public bool Fallback;
        public long ElapsedMs;
        public ServerModelDiagnostic Diagnostic;
        public bool Terminal;
        public string ToolName;
        public string ToolCallId;
        public string ToolArgumentsJson;
        public bool ToolSuccess;
        public string ToolResult;
        public string SessionId;
        public string RequestId;
    }

    /// <summary>
    /// 聚合一轮 Server SSE 的终态。即使先收到 error，只要随后收到有效 reply，reply 仍是权威结果。
    /// </summary>
    public sealed class ServerChatResponseState
    {
        public ServerChatEvent ReplyEvent { get; private set; }
        public ServerChatEvent ErrorEvent { get; private set; }
        public bool Done { get; private set; }
        public string SessionId { get; private set; }
        public string RequestId { get; private set; }

        public ServerModelDiagnostic CompletionError
        {
            get { return ReplyEvent == null ? ErrorEvent?.Diagnostic : null; }
        }

        public void Apply(ServerChatEvent value)
        {
            if (value == null) return;
            if (value.Kind == ServerChatEventKind.Reply) ReplyEvent = value;
            else if (value.Kind == ServerChatEventKind.Error) ErrorEvent = value;
            else if (value.Kind == ServerChatEventKind.Done)
            {
                Done = true;
                SessionId = value.SessionId;
                RequestId = value.RequestId;
            }
        }
    }

    /// <summary>
    /// 解析 AIBot.Server 的 Agent 级 SSE 契约。它与 OpenAI 原生 SSE 不同：
    /// token 是 Server 已从结构化回复中提取的纯台词，客户端不得再次解析 say 字段。
    /// </summary>
    public static class ServerChatEventParser
    {
        public static bool TryParse(string json, out ServerChatEvent parsed)
        {
            parsed = null;
            JObject payload;
            try { payload = JObject.Parse(json); }
            catch { return false; }

            string type = (string)payload["type"];
            var value = new ServerChatEvent();
            switch (type)
            {
                case "token":
                    value.Kind = ServerChatEventKind.Token;
                    value.Delta = (string)payload["delta"] ?? string.Empty;
                    break;
                case "reasoning":
                    value.Kind = ServerChatEventKind.Reasoning;
                    value.Delta = (string)payload["delta"] ?? string.Empty;
                    break;
                case "tool_call":
                    value.Kind = ServerChatEventKind.ToolCall;
                    value.ToolCallId = (string)payload["callId"];
                    value.ToolName = (string)payload["name"];
                    JToken args = payload["args"];
                    value.ToolArgumentsJson = args == null ? "{}" : args.ToString(Formatting.None);
                    value.ToolSuccess = (bool?)payload["success"] ?? false;
                    value.ToolResult = (string)payload["result"];
                    break;
                case "reply":
                    value.Kind = ServerChatEventKind.Reply;
                    value.Reply = new StructuredReply
                    {
                        say = (string)payload["say"] ?? string.Empty,
                        emotion = (string)payload["emotion"] ?? "neutral",
                        action = (string)payload["action"] ?? "idle"
                    };
                    JObject usage = payload["usage"] as JObject;
                    value.Usage = new Usage
                    {
                        PromptTokens = usage == null ? 0 : (int?)usage["promptTokens"] ?? 0,
                        CompletionTokens = usage == null ? 0 : (int?)usage["completionTokens"] ?? 0
                    };
                    value.Usage.TotalTokens = value.Usage.PromptTokens + value.Usage.CompletionTokens;
                    value.Fallback = (bool?)payload["fallback"] ?? false;
                    value.ElapsedMs = (long?)payload["elapsedMs"] ?? 0;
                    value.Diagnostic = ParseDiagnostic(payload["diagnostic"] as JObject);
                    break;
                case "error":
                    value.Kind = ServerChatEventKind.Error;
                    value.Diagnostic = ParseDiagnostic(payload);
                    value.Terminal = (bool?)payload["terminal"] ?? true;
                    break;
                case "done":
                    value.Kind = ServerChatEventKind.Done;
                    value.SessionId = (string)payload["sessionId"];
                    value.RequestId = (string)payload["requestId"];
                    break;
                default:
                    return false;
            }

            parsed = value;
            return true;
        }

        private static ServerModelDiagnostic ParseDiagnostic(JObject payload)
        {
            if (payload == null) return null;
            return new ServerModelDiagnostic
            {
                Code = (string)payload["code"],
                Status = (int?)payload["status"] ?? 0,
                Message = (string)payload["message"],
                Retryable = (bool?)payload["retryable"] ?? false
            };
        }
    }
}
