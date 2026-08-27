using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace AIBot.Server
{
    /// <summary>管理台和 Unity 客户端共用的最小错误契约。</summary>
    public sealed class ApiErrorBody
    {
        public string error { get; set; }
        public string code { get; set; }
        public int status { get; set; }
        public string requestId { get; set; }
        public object details { get; set; }
    }

    public static class ApiErrorWriter
    {
        public static Task WriteAsync(HttpContext context, int status, string code,
            string message, object details = null, CancellationToken ct = default(CancellationToken))
        {
            if (context.Response.HasStarted) return Task.CompletedTask;
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            var body = new ApiErrorBody
            {
                error = string.IsNullOrWhiteSpace(message) ? "请求失败" : message,
                code = string.IsNullOrWhiteSpace(code) ? "request_failed" : code,
                status = status,
                requestId = context.TraceIdentifier,
                details = details
            };
            return JsonSerializer.SerializeAsync(context.Response.Body, body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, ct);
        }
    }
}
