using AIBot.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.IncludeFields = true;        // Core DTO 使用公共字段（SimGameState 等）
});

var app = builder.Build();

app.UseDefaultFiles();       // / → wwwroot/index.html（测试台）
app.UseStaticFiles();

app.MapGet("/api/health", () => Microsoft.AspNetCore.Http.Results.Ok(new
{
    ok = true,
    version = "0.1.0-m1",
    dataRoot = DataStore.FindDataRoot(),
    npcs = DataStore.ListNpcIds("default")
}));

app.MapAIBotChat();
app.MapAIBotAdmin();
app.Run();
