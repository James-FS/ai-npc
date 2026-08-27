using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIBot.Server
{
    public static class ApiErrorMiddleware
    {
        public static void UseAIBotApiErrors(this WebApplication app)
        {
            RuntimeLogService runtimeLogs = app.Services.GetRequiredService<RuntimeLogService>();
            app.Use(async (context, next) =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                string requestId = context.Request.Headers["X-Request-Id"].ToString();
                if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 80
                    || !System.Text.RegularExpressions.Regex.IsMatch(requestId, "^[A-Za-z0-9._:-]+$"))
                    requestId = context.TraceIdentifier;
                context.TraceIdentifier = requestId;
                context.Response.Headers["X-Request-Id"] = requestId;
                try
                {
                    await next();
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // 客户端主动停止流式请求时无需再写入响应。
                }
                catch (Exception ex)
                {
                    runtimeLogs.Write(AIBot.Core.Logging.LogLevel.Error, "Http", "unhandled_exception",
                        "未处理的 API 异常: " + ex.Message, new RuntimeLogContext
                        {
                            RequestId = requestId,
                            ErrorCode = "internal_error"
                        }, ex);
                    await ApiErrorWriter.WriteAsync(context, StatusCodes.Status500InternalServerError,
                        "internal_error", "Server 处理请求时发生内部错误");
                }
                finally
                {
                    stopwatch.Stop();
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        var level = context.Response.StatusCode >= 500
                            ? AIBot.Core.Logging.LogLevel.Error
                            : context.Response.StatusCode >= 400
                                ? AIBot.Core.Logging.LogLevel.Warning
                                : AIBot.Core.Logging.LogLevel.Info;
                        runtimeLogs.Write(level, "Http", "request_completed",
                            context.Request.Method + " " + context.Request.Path,
                            new RuntimeLogContext
                            {
                                RequestId = requestId,
                                Status = context.Response.StatusCode,
                                DurationMs = stopwatch.ElapsedMilliseconds
                            });
                    }
                }
            });
        }
    }
}
