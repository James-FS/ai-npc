using System;
using System.Net.Http;
using AIBot.Core.Llm;

namespace AIBot.Server
{
    /// <summary>把上游连接错误翻译成人能看懂的诊断建议（test-connection 端点使用）。</summary>
    public static class ModelDiagnostics
    {
        public static string Diagnose(Exception ex)
        {
            if (ex == null) return "未知错误";
            int status = ExtractStatus(ex);
            string message = ex.Message ?? "";

            if (status == 401 || message.Contains("Authentication") || message.Contains("Bearer"))
                return "API key 无效或未填（401）";
            if (status == 403) return "key 无权访问该模型（403）";
            if (status == 404 || message.Contains("not found"))
                return "模型名不存在或写错（检查 model 字段）";
            if (status == 429) return "触发限流或免费额度用尽（429），稍后重试或换模型";
            if (status == 400) return "请求参数被拒（400）：baseUrl 与 key/模型是否属于同一供应商？";
            if (message.Contains("timeout") || message.Contains("超时"))
                return "连接超时：端点可能被网络阻断。境外端点（opencode.ai / openrouter 等）需设置 AIBot_HTTP_PROXY；国内直连建议 DeepSeek / GLM / OpenCode Go 通道";
            if (IsNetworkError(ex))
                return "网络错误：检查 baseUrl 拼写与网络连通性";
            return message;
        }

        private static int ExtractStatus(Exception ex)
        {
            // HttpLlmBackend 把 LlmTransportException 包在 LlmFallbackException.Inner 里
            Exception cursor = ex;
            for (int i = 0; i < 4 && cursor != null; i++, cursor = cursor.InnerException)
            {
                if (cursor is LlmTransportException transport) return transport.StatusCode;
            }
            return 0;
        }

        private static bool IsNetworkError(Exception ex)
        {
            Exception cursor = ex;
            for (int i = 0; i < 4 && cursor != null; i++, cursor = cursor.InnerException)
            {
                if (cursor is HttpRequestException) return true;
            }
            return false;
        }
    }
}
