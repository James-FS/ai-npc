using System;
using System.Threading.RateLimiting;
using AIBot.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
// Windows 默认 EventLog provider 在普通用户进程下可能因无写权限抛异常，
// 甚至会截断原本已经生成的 4xx/5xx 响应。Server 明确使用跨平台控制台日志。
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
StorageOptions storageOptions = StorageOptions.From(builder.Configuration);
storageOptions.Validate();
MySqlConnectionFactory mySqlFactory = storageOptions.IsMySql
    ? new MySqlConnectionFactory(storageOptions.MySqlConnectionString) : null;
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.IncludeFields = true;        // Core DTO 使用公共字段（SimGameState 等）
});
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton<RuntimeLogService>();
if (storageOptions.IsMySql)
{
    builder.Services.AddSingleton(mySqlFactory);
    builder.Services.AddSingleton<IMemoryRepository, MySqlMemoryRepository>();
    builder.Services.AddSingleton<MemoryAuditService>(provider =>
        new MemoryAuditService(provider.GetRequiredService<MySqlConnectionFactory>()));
}
else
{
    builder.Services.AddSingleton<IMemoryRepository, JsonMemoryRepository>();
    builder.Services.AddSingleton<MemoryAuditService>();
}
builder.Services.AddSingleton<PlayerMemoryService>();
builder.Services.AddSingleton<MemorySummaryQueue>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MemorySummaryQueue>());
builder.Services.AddHostedService<LogMaintenanceService>();
int chatRequestsPerMinute = builder.Configuration.GetValue<int?>("Security:ChatRequestsPerMinute") ?? 60;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        await ApiErrorWriter.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests,
            "rate_limited", "请求过于频繁，请稍后重试", null, cancellationToken);
    };
    options.AddPolicy("chat", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, chatRequestsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();

app.UseAIBotApiErrors();

if (storageOptions.IsMySql)
{
    SessionStore.UseMySql(mySqlFactory);
    ChatLogService.UseMySql(mySqlFactory);
}
ChatLogService.Configure(builder.Configuration, app.Services.GetRequiredService<RuntimeLogService>());

if (storageOptions.IsMySql && storageOptions.AutoMigrate)
{
    await DatabaseMigrator.ApplyAsync(mySqlFactory, app.Lifetime.ApplicationStopping);
}

if (storageOptions.IsMySql && Array.Exists(args, value => string.Equals(value, "--migrate-json",
    StringComparison.OrdinalIgnoreCase)))
{
    var source = new JsonMemoryRepository();
    var target = new MySqlMemoryRepository(mySqlFactory);
    var migration = await new JsonToMySqlMemoryMigrator(source, target)
        .RunAsync(builder.Configuration["Storage:MigrationGameId"] ?? "default", app.Lifetime.ApplicationStopping);
    Console.WriteLine("JSON→MySQL memory migration: scanned=" + migration.Scanned
        + ", migrated=" + migration.Migrated + ", skipped=" + migration.Skipped);
    if (Array.Exists(args, value => string.Equals(value, "--exit-after-migrate", StringComparison.OrdinalIgnoreCase)))
        return;
}

await StartupDiagnostics.RunAsync(storageOptions, mySqlFactory, builder.Configuration,
    app.Lifetime.ApplicationStopping);

// 可选管理鉴权：本地未配置时保持零门槛；部署时设置 AIBOT_ADMIN_TOKEN 即保护管理 API。
string adminToken = Environment.GetEnvironmentVariable("AIBOT_ADMIN_TOKEN")
    ?? app.Configuration["Security:AdminToken"];
string clientToken = Environment.GetEnvironmentVariable("AIBOT_CLIENT_TOKEN")
    ?? app.Configuration["Security:ClientToken"];
if (string.IsNullOrWhiteSpace(clientToken))
{
    app.Logger.LogWarning("AIBOT_CLIENT_TOKEN 未配置：聊天 API 未启用客户端鉴权，仅适合本机开发环境");
}
if (!storageOptions.IsMySql)
{
    app.Logger.LogWarning("当前使用 JSON 存储：请保持单 Server 实例运行，不要让多个进程同时写入同一 data 目录");
}
app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? string.Empty;
    string normalizedPath = path.TrimEnd('/');
    // StartsWithSegments 同时覆盖精确的 /api/games 和其子路径，避免管理端的
    // GET/POST /api/games 在配置 token 后意外绕过鉴权；聊天 SSE 仍保持客户端可用。
    bool isGameApi = context.Request.Path.StartsWithSegments("/api/games");
    bool isChatStream = normalizedPath.EndsWith("/chat/stream", StringComparison.OrdinalIgnoreCase);
    bool isNpcList = HttpMethods.IsGet(context.Request.Method)
        && normalizedPath.EndsWith("/npcs", StringComparison.OrdinalIgnoreCase);
    bool isManagedApi = path.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase)
        || (isGameApi && !isChatStream);
    if (isChatStream && !string.IsNullOrWhiteSpace(clientToken))
    {
        string auth = context.Request.Headers.Authorization.ToString();
        if (!string.Equals(auth, "Bearer " + clientToken, StringComparison.Ordinal))
        {
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status401Unauthorized,
                "client_auth_required", "聊天 API 需要有效的客户端 token");
            return;
        }
    }
    if (isManagedApi && !string.IsNullOrEmpty(adminToken))
    {
        string auth = context.Request.Headers.Authorization.ToString();
        bool readOnlyClientAccess = isNpcList && !string.IsNullOrWhiteSpace(clientToken)
            && string.Equals(auth, "Bearer " + clientToken, StringComparison.Ordinal);
        if (!readOnlyClientAccess
            && !string.Equals(auth, "Bearer " + adminToken, StringComparison.Ordinal))
        {
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status401Unauthorized,
                "admin_auth_required", "管理 API 需要有效的 Bearer token");
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();       // / → wwwroot/index.html，再跳转到 Vue 统一管理台
app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/api/health", () => Microsoft.AspNetCore.Http.Results.Ok(new
{
    ok = true,
    version = "0.3.0-m4"
}));

app.MapGet("/api/ready", async (HttpContext http) =>
{
    var result = await ReadinessService.CheckAsync(storageOptions, mySqlFactory,
        app.Services.GetRequiredService<MemorySummaryQueue>(), app.Configuration, http.RequestAborted);
    return Results.Json(result.Body, statusCode: result.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapAIBotChat();
app.MapAIBotAdmin();
app.Run();
