using System;
using System.Net.Http;
using AIBot.Core;
using AIBot.Core.Llm;

namespace AIBot.Server
{
    public sealed class ModelErrorInfo
    {
        public string Code { get; set; }
        public int Status { get; set; }
        public string Message { get; set; }
        public bool Retryable { get; set; }
    }

    public static class ModelErrorContract
    {
        /// <summary>
        /// 没有传输层异常时的兜底诊断（典型是结构化解析失败：模型正常返回，但内容不是
        /// 约定 JSON）。直接沿用 Core 的 <c>AgentLoopResult.FallbackReason</c> 作为 code，
        /// 使 Server 模式与 local 模式向游戏层暴露同一套失败标识。
        /// </summary>
        public static ModelErrorInfo FromFallbackReason(string reason)
        {
            switch (reason)
            {
                case FallbackReasons.StructuredReplyInvalid:
                    return Info(FallbackReasons.StructuredReplyInvalid, 502,
                        "模型返回内容无法解析为约定的结构化回复", false);
                case FallbackReasons.ModelRequestFailed:
                    return Info(FallbackReasons.ModelRequestFailed, 502, "模型请求失败", true);
                case FallbackReasons.AgentFailed:
                    return Info(FallbackReasons.AgentFailed, 502, "Agent 处理本轮对话失败", false);
                default:
                    return Info("model_error", 502, "模型请求失败", false);
            }
        }

        public static ModelErrorInfo Classify(Exception error)
        {
            int status = ExtractStatus(error);
            string message = error?.Message ?? "模型请求失败";
            if (status == 401) return Info("model_unauthorized", 502, "模型 API key 无效或未授权", false);
            if (status == 403) return Info("model_forbidden", 502, "模型 API key 无权访问该模型", false);
            if (status == 404) return Info("model_not_found", 502, "模型或模型端点不存在", false);
            if (status == 429) return Info("model_rate_limited", 429, "模型服务限流，请稍后重试", true);
            if (status == 400) return Info("model_invalid_request", 502, "模型请求参数无效", false);
            if (status >= 500) return Info("model_upstream_error", 502, "模型服务暂时不可用", true);
            if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || message.Contains("超时")) return Info("model_timeout", 504, "模型请求超时", true);
            if (Find(error, e => e is HttpRequestException) != null)
                return Info("model_network_error", 502, "无法连接模型服务", true);
            if (message.IndexOf("JSON", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("response", StringComparison.OrdinalIgnoreCase) >= 0)
                return Info("model_invalid_response", 502, "模型返回内容无法解析", false);
            return Info("model_error", 502, "模型请求失败", false);
        }

        private static ModelErrorInfo Info(string code, int status, string message, bool retryable)
        {
            return new ModelErrorInfo { Code = code, Status = status, Message = message, Retryable = retryable };
        }

        private static int ExtractStatus(Exception error)
        {
            Exception current = Find(error, e => e is LlmTransportException);
            return current is LlmTransportException transport ? transport.StatusCode : 0;
        }

        private static Exception Find(Exception error, Func<Exception, bool> predicate)
        {
            for (Exception current = error; current != null; current = current.InnerException)
                if (predicate(current)) return current;
            return null;
        }
    }
}
