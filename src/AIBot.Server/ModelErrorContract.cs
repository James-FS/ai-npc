using System;
using System.Net.Http;
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
