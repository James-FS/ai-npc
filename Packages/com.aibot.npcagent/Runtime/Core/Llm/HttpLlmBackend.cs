using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Config;
using Newtonsoft.Json;

namespace AIBot.Core.Llm
{
    /// <summary>上游 HTTP 错误（携带状态码与响应体，供重试/降级决策与诊断）。</summary>
    public sealed class LlmTransportException : Exception
    {
        public readonly int StatusCode;
        public readonly string Body;

        public LlmTransportException(int statusCode, string body)
            : base("LLM HTTP " + statusCode + (string.IsNullOrEmpty(body) ? "" : ": " + body))
        {
            StatusCode = statusCode;
            Body = body ?? "";
        }
    }

    /// <summary>
    /// HttpClient 版流式后端（Server/CLI 宿主用；Unity 侧用 UnityWebRequestBackend）。
    /// 策略（主方案 §5.5）：网络/5xx/429/超时重试 1 次（仅未流出任何 token 时）；
    /// 400 且涉及 response_format 时自动去掉该参数重试（兼容性降级）；其余抛 LlmFallbackException。
    /// </summary>
    public sealed class HttpLlmBackend : ILlmBackend
    {
        private static readonly HttpClient SharedClient = CreateClient();
        private readonly ModelSettings _settings;

        /// <summary>代理支持：AIBot_HTTP_PROXY / HTTPS_PROXY 环境变量（如 http://127.0.0.1:7890）。用于访问 opencode.ai 等直连不通的端点。</summary>
        private static HttpClient CreateClient()
        {
            string proxyUrl = Environment.GetEnvironmentVariable("AIBot_HTTP_PROXY")
                ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
                ?? Environment.GetEnvironmentVariable("https_proxy");
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                var handler = new HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(proxyUrl),
                    UseProxy = true
                };
                return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            }
            return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public HttpLlmBackend(ModelSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public async Task ChatStreamAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct)
        {
            try
            {
                await ChatStreamCoreAsync(request, sink, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                sink.OnError(ex);
                throw;
            }
        }

        private async Task ChatStreamCoreAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct)
        {
            bool degraded = false;
            for (int attempt = 0; ; attempt++)
            {
                var gate = new GateSink(sink);
                try
                {
                    await OnceAsync(request, gate, ct);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;                                   // 调用方主动取消
                }
                catch (OperationCanceledException)
                {
                    if (attempt >= 1 || gate.Streamed) throw new LlmFallbackException("LLM timeout after " + _settings.timeoutMs + "ms");
                    await Task.Delay(1000, ct);
                }
                catch (LlmTransportException ex)
                {
                    if (ex.StatusCode == 400 && !degraded && request.ResponseFormat != null
                        && ex.Body.Contains("response_format"))
                    {
                        degraded = true;                     // json_object 与环境不兼容：降级为纯 prompt 约束
                        request.ResponseFormat = null;
                        attempt = -1;
                        continue;
                    }
                    bool retryable = ex.StatusCode >= 500 || ex.StatusCode == 429 || ex.StatusCode == 408;
                    if (retryable && attempt < 1 && !gate.Streamed)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    throw new LlmFallbackException(ex.Message, ex);
                }
                catch (HttpRequestException ex)
                {
                    if (attempt < 1 && !gate.Streamed)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    throw new LlmFallbackException("LLM network error: " + ex.Message, ex);
                }
            }
        }

        private async Task OnceAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct)
        {
            string url = _settings.baseUrl.TrimEnd('/') + "/chat/completions";
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(_settings.timeoutMs);
                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    httpRequest.Content = new StringContent(
                        JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
                    if (!string.IsNullOrWhiteSpace(_settings.apiKey))
                        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.apiKey);

                    using (HttpResponseMessage resp = await SharedClient.SendAsync(
                        httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string body = await resp.Content.ReadAsStringAsync();
                            throw new LlmTransportException((int)resp.StatusCode, body);
                        }

                        var aggregator = new OpenAiStreamAggregator(sink);
                        var parser = new SseLineParser(aggregator.HandleDataLine);
                        Decoder utf8 = Encoding.UTF8.GetDecoder();
                        var buffer = new byte[8192];
                        using (Stream stream = await resp.Content.ReadAsStreamAsync())
                        {
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, timeoutCts.Token)) > 0)
                            {
                                char[] chars = new char[Encoding.UTF8.GetMaxCharCount(read)];
                                int decoded = utf8.GetChars(buffer, 0, read, chars, 0);
                                if (decoded > 0) parser.Feed(new string(chars, 0, decoded));
                            }
                        }
                        parser.Flush();
                        if (!aggregator.SawDone && !aggregator.SawFinishReason)
                            throw new LlmTransportException(502,
                                "LLM stream ended before [DONE] or finish_reason");
                        aggregator.Complete();
                    }
                }
            }
        }

        /// <summary>重试安全阀：一旦有 token 已流出到下游，禁止重放（避免重复输出）。</summary>
        private sealed class GateSink : ILlmStreamSink, IReasoningSink
        {
            private readonly ILlmStreamSink _inner;
            public bool Streamed;

            public GateSink(ILlmStreamSink inner) { _inner = inner; }

            public void OnToken(string delta) { Streamed = true; _inner.OnToken(delta); }
            public void OnToolCall(ToolCallDto call) { _inner.OnToolCall(call); }
            public void OnCompleted(string fullText, Usage usage) { _inner.OnCompleted(fullText, usage); }
            public void OnError(Exception ex) { _inner.OnError(ex); }
            public void OnReasoningToken(string delta)
            {
                Streamed = true;
                var sink = _inner as IReasoningSink;
                if (sink != null) sink.OnReasoningToken(delta);
            }
        }
    }
}
