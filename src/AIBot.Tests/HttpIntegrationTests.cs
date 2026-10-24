using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AIBot.Core.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AIBot.Tests
{
    public sealed class HttpIntegrationTests
    {
        [Fact]
        public async Task AdminConfigurationAndOperationsKeepTheirHttpContracts()
        {
            await using var host = await RunningServer.StartAsync();
            host.Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "admin");

            JObject games = JObject.Parse(await host.Client.GetStringAsync("/api/games"));
            Assert.Contains("other", games["games"].Values<string>());
            JObject npc = JObject.Parse(await host.Client.GetStringAsync("/api/games/other/npcs/npc"));
            Assert.Equal("npc", (string)npc["npcId"]);
            Assert.True(string.IsNullOrEmpty((string)npc["model"]["apiKey"]));
            JObject connections = JObject.Parse(await host.Client.GetStringAsync("/api/admin/model-connections"));
            Assert.NotEmpty((JArray)connections["connections"]);

            using var worldBody = new StringContent(new JObject
            {
                ["worldId"] = "default", ["description"] = "Updated world", ["extraRules"] = new JArray("Keep promises")
            }.ToString(), Encoding.UTF8, "application/json");
            using var saved = await host.Client.PutAsync("/api/games/other/world", worldBody);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            JObject world = JObject.Parse(await host.Client.GetStringAsync("/api/games/other/world"));
            Assert.Equal("Updated world", (string)world["description"]);
            Assert.Equal("Keep promises", (string)world["extraRules"][0]);

            JObject storage = JObject.Parse(await host.Client.GetStringAsync("/api/admin/storage"));
            Assert.Equal("Json", (string)storage["provider"]);
            JObject queue = JObject.Parse(await host.Client.GetStringAsync("/api/admin/memory-summary-queue"));
            Assert.Equal(0, (int)queue["pending"]);
            Assert.Equal(HttpStatusCode.OK, (await host.Chat("client", "split-routes")).StatusCode);
            JObject sessions = JObject.Parse(await host.Client.GetStringAsync("/api/games/other/sessions?npcId=npc&playerId=p"));
            Assert.Single((JArray)sessions["sessions"]);
            Assert.Equal(1, (int)(await host.Stats())["totalRequests"]);
        }

        [Fact]
        public async Task AdminMemoryWritesKeepVersionChecksAndAuditAfterRouteSplit()
        {
            await using var host = await RunningServer.StartAsync();
            host.Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "admin");
            const string memoryPath = "/api/games/other/memories/npc/p";
            using var fact = new StringContent(new JObject
            {
                ["expectedVersion"] = 0,
                ["fact"] = new JObject { ["category"] = "profile", ["key"] = "name", ["value"] = "Test Hero" }
            }.ToString(), Encoding.UTF8, "application/json");
            using var added = await host.Client.PostAsync(memoryPath + "/facts", fact);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            JObject saved = JObject.Parse(await added.Content.ReadAsStringAsync());
            int version = (int)saved["memoryVersion"];
            Assert.True(version > 0);
            Assert.Equal("Test Hero", (string)saved["facts"][0]["value"]);

            using var staleBody = new StringContent(new JObject
            { ["summary"] = "Must not overwrite", ["expectedVersion"] = 0 }.ToString(), Encoding.UTF8, "application/json");
            using var stale = await host.Client.PutAsync(memoryPath + "/summary", staleBody);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            JObject conflict = JObject.Parse(await stale.Content.ReadAsStringAsync());
            Assert.Equal(version, (int)conflict["actualVersion"]);
            JObject current = JObject.Parse(await host.Client.GetStringAsync(memoryPath));
            Assert.Equal(version, (int)current["memoryVersion"]);
            Assert.Equal("Test Hero", (string)current["facts"][0]["value"]);
            string audit = await host.Client.GetStringAsync("/api/games/other/memory-audit?npcId=npc&playerId=p");
            Assert.Contains("memory.fact.create", audit);
        }

        [Fact]
        public async Task UnsupportedUsageOptionDegradesWithoutBreakingChat()
        {
            await using var host = await RunningServer.StartAsync();
            host.RejectUsageOption = true;
            string response = await (await host.Chat("client", "usage-compat")).Content.ReadAsStringAsync();
            Assert.Contains("\"fallback\":false", response);
            Assert.Equal(2, host.UpstreamCalls);
        }

        [Fact]
        public async Task ChatReportsMissingServerModelBindingWithoutUsingLegacyInlineSettings()
        {
            await using var host = await RunningServer.StartAsync();
            string path = Path.Combine(host.Root, "games", "other", "npcs", "npc.json");
            AgentConfigDto npc = JsonConvert.DeserializeObject<AgentConfigDto>(File.ReadAllText(path));
            npc.model.apiKey = null;
            File.WriteAllText(path, JsonConvert.SerializeObject(npc));
            File.Delete(Path.Combine(host.Root, "model-connections.json"));
            await host.RestartAsync();

            HttpResponseMessage response = await host.Chat("client", "missing-model-binding");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            JObject body = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("model_connection_missing", (string)body["code"]);
            Assert.Contains("未分配 Server 主模型连接", (string)body["error"]);
            Assert.Equal(0, host.UpstreamCalls);
        }

        [Fact]
        public async Task SummaryBindingAndPolicySaveResolveToTheSameConcreteSettingsUsedByChat()
        {
            await using var host = await RunningServer.StartAsync();
            host.Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "admin");

            using var create = new StringContent(new JObject
            {
                ["connection"] = new JObject
                {
                    ["id"] = "summary-special", ["name"] = "Summary Special",
                    ["baseUrl"] = host.UpstreamBase + "/summary/v1", ["apiFormat"] = "openai_chat_completions",
                    ["model"] = "summary-not-deepseek", ["apiKey"] = "summary-test-key-0123456789"
                }
            }.ToString(Formatting.None), Encoding.UTF8, "application/json");
            var created = await host.Client.PostAsync("/api/admin/model-connections", create);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            string summaryConnectionId = (string)JObject.Parse(await created.Content.ReadAsStringAsync())["connections"].Last["id"];

            using var binding = new StringContent(new JObject
            {
                ["scope"] = "npc_summary", ["gameId"] = "other", ["npcId"] = "npc",
                ["connectionId"] = summaryConnectionId, ["summaryMode"] = "dedicated"
            }.ToString(Formatting.None), Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.OK, (await host.Client.PutAsync("/api/admin/model-connections/binding", binding)).StatusCode);

            using var policy = new StringContent(new JObject
            {
                ["inheritGameDefaults"] = false, ["useMainSummaryModel"] = true,
                ["shortTermTurns"] = 1, ["summaryThreshold"] = 1, ["memoryScope"] = "player_npc",
                ["maxFacts"] = 8, ["rememberPlayerProfile"] = true, ["rememberPromises"] = true,
                ["rememberQuestEvents"] = true, ["rememberCasualChat"] = false, ["backgroundSummarization"] = false,
                ["summaryModel"] = new JObject { ["temperature"] = 0.23, ["maxTokens"] = 877, ["timeoutMs"] = 6543 }
            }.ToString(Formatting.None), Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.OK, (await host.Client.PutAsync("/api/games/other/npcs/npc/memory-policy", policy)).StatusCode);
            JObject preview = JObject.Parse(await host.Client.GetStringAsync("/api/games/other/npcs/npc/memory-policy"));
            Assert.Equal(host.UpstreamBase + "/summary/v1", (string)preview["effective"]["policy"]["summaryModel"]["baseUrl"]);
            Assert.Equal("summary-not-deepseek", (string)preview["effective"]["policy"]["summaryModel"]["model"]);
            Assert.Equal(string.Empty, (string)preview["effective"]["policy"]["summaryModel"]["apiKey"]);
            Assert.Equal(0.23, (double)preview["effective"]["policy"]["summaryModel"]["temperature"]);
            Assert.Equal("npc: Summary Special", (string)preview["effective"]["sources"]["summaryModel"]);

            Assert.Equal(HttpStatusCode.OK, (await host.Chat("client", "summary-routing-1")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.Chat("client", "summary-routing-2")).StatusCode);
            JObject summaryCall = host.Requests.Last(request => (string)request["path"] == "/summary/v1/chat/completions");
            Assert.Equal("summary-not-deepseek", (string)summaryCall["body"]["model"]);
            Assert.Equal(0.23, (double)summaryCall["body"]["temperature"]);
            Assert.Equal(877, (int)summaryCall["body"]["max_tokens"]);
            Assert.Equal("Bearer summary-test-key-0123456789", (string)summaryCall["authorization"]);
            JObject mainCall = host.Requests.First(request => (string)request["path"] == "/v1/chat/completions");
            Assert.Equal("primary-not-deepseek", (string)mainCall["body"]["model"]);
            Assert.Equal("Bearer test-key", (string)mainCall["authorization"]);

            using var missingBinding = new StringContent(new JObject
            {
                ["scope"] = "npc_summary", ["gameId"] = "other", ["npcId"] = "npc",
                ["connectionId"] = null, ["summaryMode"] = "dedicated"
            }.ToString(Formatting.None), Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.OK, (await host.Client.PutAsync("/api/admin/model-connections/binding", missingBinding)).StatusCode);
            JObject diagnostic = JObject.Parse(await host.Client.GetStringAsync("/api/games/other/npcs/npc/memory-policy"));
            Assert.Equal("invalid-model-connection", (string)diagnostic["effective"]["sources"]["summaryModel"]);
            Assert.Contains(((JArray)diagnostic["effective"]["adjustments"]).Values<string>(),
                item => item.Contains("未分配有效摘要连接", StringComparison.Ordinal));
            HttpResponseMessage invalidChat = await host.Chat("client", "summary-routing-missing");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidChat.StatusCode);
            Assert.Equal(3, host.UpstreamCalls);
        }

        [Fact]
        public async Task UnreadableSessionNeverRunsModelOrOverwritesRequestHistory()
        {
            await using var host = await RunningServer.StartAsync();
            string dir = Path.Combine(host.Root, "games", "other", "sessions", "npc", "p");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "s.json");
            File.WriteAllText(path, "{ corrupted history");
            Assert.Equal(HttpStatusCode.InternalServerError, (await host.Chat("client", "uncertain")).StatusCode);
            Assert.Equal(HttpStatusCode.InternalServerError, (await host.Chat("client", "uncertain")).StatusCode);
            Assert.Equal(0, host.UpstreamCalls);
            Assert.Equal("{ corrupted history", File.ReadAllText(path));
        }

        [Fact]
        public async Task DistinctAdminAndClientTokensSupportDebugWithoutGrantingClientAdminAccess()
        {
            await using var host = await RunningServer.StartAsync();
            Assert.Equal(HttpStatusCode.OK, (await host.Chat("admin", "admin-debug", toolMode: "simulated")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.Chat("client", "client-chat")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Chat("client", "client-debug", toolMode: "simulated")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.Chat(null, "anonymous")).StatusCode);
            using var managed = new HttpRequestMessage(HttpMethod.Get, "/api/games");
            managed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "client");
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(managed)).StatusCode);
        }

        [Fact]
        public async Task NonDefaultGameIsReadyAndReplayDoesNotInvokeModelTwice()
        {
            await using var host = await RunningServer.StartAsync();
            Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/ready")).StatusCode);
            string first = await (await host.Chat("client", "stable")).Content.ReadAsStringAsync();
            var replay = await host.Chat("client", "stable");
            Assert.Equal("true", replay.Headers.GetValues("X-AIBot-Replayed").Single());
            Assert.Equal(first.Substring(first.IndexOf("data: {\"type\":\"reply\"", StringComparison.Ordinal)),
                await replay.Content.ReadAsStringAsync());
            Assert.Equal(1, host.UpstreamCalls);
            JObject stats = await host.Stats();
            Assert.Equal(1, (int)stats["totalRequests"]);
            Assert.Equal(7, (int)stats["totalPromptTokens"]);
            await host.RestartAsync();
            Assert.Equal(stats.ToString(), (await host.Stats()).ToString());
            var conflict = await host.Chat("client", "stable", "different message");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        [Fact]
        public async Task OldRequestIdIsRejectedAfterReplayPayloadExpires()
        {
            await using var host = await RunningServer.StartAsync();
            for (int i = 0; i < 25; i++)
                Assert.Equal(HttpStatusCode.OK, (await host.Chat("client", "req-" + i)).StatusCode);
            var expired = await host.Chat("client", "req-0");
            Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
            Assert.Contains("request_replay_expired", await expired.Content.ReadAsStringAsync());
            Assert.Equal(25, host.UpstreamCalls);
        }

        [Fact]
        public async Task PersistedPendingToolRoundCanBeReplayedAndResumedAfterServerRestart()
        {
            await using var host = await RunningServer.StartAsync();
            host.Scripts.Enqueue("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"echo\",\"arguments\":\"{}\"}}]}}],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":2}}\n\ndata: [DONE]\n\n");
            var body = new JObject { ["requestId"] = "start-tools", ["npcId"] = "npc", ["playerId"] = "p",
                ["sessionId"] = "s", ["message"] = "hello", ["toolMode"] = "game",
                ["tools"] = new JArray(new JObject { ["id"] = "echo", ["parametersSchema"] = "{\"type\":\"object\"}" }) };
            string pending = await (await host.Post(body)).Content.ReadAsStringAsync();
            Assert.Contains("tool_pending", pending);
            Assert.Contains("start-tools#0", File.ReadAllText(Path.Combine(host.Root, "games", "other", "sessions", "npc", "p", "s.json")));
            await host.RestartAsync();
            var replay = await host.Post(body);
            Assert.Equal(pending, await replay.Content.ReadAsStringAsync());
            var resume = new JObject { ["requestId"] = "resume-tools", ["npcId"] = "npc", ["playerId"] = "p",
                ["sessionId"] = "s", ["toolMode"] = "game", ["roundToken"] = "start-tools#0",
                ["toolResults"] = new JArray(new JObject { ["callId"] = "call-1", ["success"] = true, ["message"] = "done" }) };
            Assert.Contains("\"type\":\"reply\"", await (await host.Post(resume)).Content.ReadAsStringAsync());
            var resumedReplay = await host.Post(resume);
            Assert.Equal("true", resumedReplay.Headers.GetValues("X-AIBot-Replayed").Single());
            Assert.Equal(2, host.UpstreamCalls);
            JObject stats = await host.Stats();
            Assert.Equal(2, (int)stats["totalRequests"]);
            Assert.Equal(18, (int)stats["totalPromptTokens"]);
            Assert.Equal(5, (int)stats["totalCompletionTokens"]);
        }

        [Fact]
        public async Task InvalidSummaryIsMeteredAndItsMessagesSurviveRestart()
        {
            await using var host = await RunningServer.StartAsync();
            string npcPath = Path.Combine(host.Root, "games", "other", "npcs", "npc.json");
            AgentConfigDto npc = JsonConvert.DeserializeObject<AgentConfigDto>(File.ReadAllText(npcPath));
            npc.memory.shortTermTurns = 1;
            npc.memory.summaryThreshold = 1;
            File.WriteAllText(npcPath, JsonConvert.SerializeObject(npc));
            Assert.Equal(HttpStatusCode.OK, (await host.Chat("client", "summary-first")).StatusCode);
            host.Scripts.Enqueue("data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"say\\\":\\\"hello\\\"}\"}}],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3}}\n\ndata: [DONE]\n\n");
            host.Scripts.Enqueue("data: {\"choices\":[{\"delta\":{\"content\":\"invalid summary\"}}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2}}\n\ndata: [DONE]\n\n");
            Assert.Equal(HttpStatusCode.OK, (await host.Chat("client", "summary-second")).StatusCode);
            JObject stats = await host.Stats();
            Assert.Equal(2, (int)stats["totalRequests"]);
            Assert.Equal(1, (int)stats["totalSummaryCalls"]);
            Assert.Equal(19, (int)stats["totalPromptTokens"]);
            await host.RestartAsync();
            var session = JObject.Parse(File.ReadAllText(Path.Combine(host.Root, "games", "other", "sessions", "npc", "p", "s.json")));
            Assert.Equal(2, ((JArray)session["evictedMessages"]).Count);
            Assert.Equal(stats.ToString(), (await host.Stats()).ToString());
        }

        private sealed class RunningServer : IAsyncDisposable
        {
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "aibot-http-" + Guid.NewGuid().ToString("N"));
            public HttpClient Client { get; private set; }
            public ConcurrentQueue<string> Scripts { get; } = new ConcurrentQueue<string>();
            public ConcurrentQueue<JObject> Requests { get; } = new ConcurrentQueue<JObject>();
            public int UpstreamCalls;
            public bool RejectUsageOption;
            public string UpstreamBase { get; private set; }
            private WebApplication _upstream;
            private Process _process;

            public static async Task<RunningServer> StartAsync()
            {
                var host = new RunningServer();
                try
                {
                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
                    builder.Logging.ClearProviders();
                    builder.WebHost.UseUrls("http://127.0.0.1:0");
                    host._upstream = builder.Build();
                    host._upstream.MapPost("/v1/chat/completions", (HttpContext http) => host.HandleCompletion(http));
                    host._upstream.MapPost("/summary/v1/chat/completions", (HttpContext http) => host.HandleCompletion(http));
                    await host._upstream.StartAsync();
                    string upstream = host._upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
                    host.UpstreamBase = upstream;
                    Directory.CreateDirectory(Path.Combine(host.Root, "games", "other", "npcs"));
                    var npc = new AgentConfigDto { npcId = "npc", displayName = "tester" };
                    npc.enabledToolIds.Add("echo");
                    npc.model.baseUrl = upstream + "/v1";
                    npc.model.apiKey = "test-key";
                    npc.model.model = "primary-not-deepseek";
                    npc.memory.summaryThreshold = 0;
                    File.WriteAllText(Path.Combine(host.Root, "games", "other", "npcs", "npc.json"), JsonConvert.SerializeObject(npc));
                    await host.RestartAsync();
                    return host;
                }
                catch { await host.DisposeAsync(); throw; }
            }

            private async Task HandleCompletion(HttpContext http)
            {
                System.Threading.Interlocked.Increment(ref UpstreamCalls);
                using var reader = new StreamReader(http.Request.Body);
                JObject body = JObject.Parse(await reader.ReadToEndAsync());
                Requests.Enqueue(new JObject { ["path"] = http.Request.Path.Value, ["body"] = body,
                    ["authorization"] = http.Request.Headers.Authorization.ToString() });
                if (RejectUsageOption && body["stream_options"] != null)
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("unsupported stream_options");
                    return;
                }
                string script = Scripts.TryDequeue(out string queued) ? queued
                    : "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"say\\\":\\\"hello\\\"}\"}}],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3}}\n\ndata: [DONE]\n\n";
                http.Response.ContentType = "text/event-stream";
                await http.Response.WriteAsync(script);
            }

            public async Task RestartAsync()
            {
                if (_process != null)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                    _process.Dispose();
                    Client.Dispose();
                }
                var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Root, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AIBot.Server.dll"));
                start.ArgumentList.Add("--urls");
                start.ArgumentList.Add("http://127.0.0.1:0");
                start.Environment["AIBOT_DATA_ROOT"] = Root;
                start.Environment["AIBOT_STORAGE_PROVIDER"] = "Json";
                start.Environment["AIBOT_ADMIN_TOKEN"] = "admin";
                start.Environment["AIBOT_CLIENT_TOKEN"] = "client";
                start.Environment["AIBot_HTTP_PROXY"] = "";
                start.Environment["HTTPS_PROXY"] = "";
                start.Environment["https_proxy"] = "";
                start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
                _process = new Process { StartInfo = start, EnableRaisingEvents = true };
                _process.OutputDataReceived += (_, e) =>
                {
                    const string marker = "Now listening on: ";
                    int index = e.Data?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
                    if (index >= 0) listening.TrySetResult(e.Data.Substring(index + marker.Length).Trim());
                };
                _process.ErrorDataReceived += (_, e) => { if (e.Data != null) listening.TrySetException(new Exception(e.Data)); };
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                Client = new HttpClient { BaseAddress = new Uri(await listening.Task.WaitAsync(TimeSpan.FromSeconds(20))) };
            }

            public Task<HttpResponseMessage> Chat(string token, string requestId, string message = "hello", string toolMode = "none")
                => Post(new JObject { ["requestId"] = requestId, ["npcId"] = "npc", ["playerId"] = "p",
                    ["sessionId"] = "s", ["message"] = message, ["toolMode"] = toolMode }, token);

            public async Task<JObject> Stats()
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/games/other/stats");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "admin");
                var response = await Client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return JObject.Parse(await response.Content.ReadAsStringAsync());
            }

            public Task<HttpResponseMessage> Post(JObject body, string token = "client")
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/games/other/chat/stream")
                    { Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json") };
                if (token != null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return Client.SendAsync(request);
            }

            public async ValueTask DisposeAsync()
            {
                if (_process != null)
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                    _process.Dispose();
                }
                Client?.Dispose();
                if (_upstream != null) await _upstream.DisposeAsync();
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
        }
    }
}
