using System;
using System.Threading.RateLimiting;
using AIBot.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
StorageOptions storageOptions = StorageOptions.From(builder.Configuration);
storageOptions.Validate();
MySqlConnectionFactory mySqlFactory = storageOptions.IsMySql
    ? new MySqlConnectionFactory(storageOptions.MySqlConnectionString) : null;
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.IncludeFields = true;        // Core DTO 使用公共字段（SimGameState 等）
});
builder.Services.AddSingleton(storageOptions);
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
int chatRequestsPerMinute = builder.Configuration.GetValue<int?>("Security:ChatRequestsPerMinute") ?? 60;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

if (storageOptions.IsMySql)
{
    SessionStore.UseMySql(mySqlFactory);
    ChatLogService.UseMySql(mySqlFactory);
}

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
app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? string.Empty;
    bool isManagedApi = path.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith("/api/games/", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("/chat/stream", StringComparison.OrdinalIgnoreCase));
    if (isManagedApi && !string.IsNullOrEmpty(adminToken))
    {
        string auth = context.Request.Headers.Authorization.ToString();
        if (!string.Equals(auth, "Bearer " + adminToken, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("管理 API 需要有效的 Bearer token");
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
    version = "0.3.0-m3",
    storage = storageOptions.Provider,
    dataRoot = DataStore.FindDataRoot(),
    npcs = DataStore.ListNpcIds("default")
}));

app.MapAIBotChat();
app.MapAIBotAdmin();
app.Run();
