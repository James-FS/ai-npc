using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Core.Llm;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace AIBot.Unity
{
    /// <summary>
    /// UnityWebRequest 版流式后端（开发期直连 DeepSeek/GLM）。
    /// SSE 字节增量 → Decoder 增量 UTF-8 解码（防多字节字符被切断）→ SseLineParser → 聚合器。
    /// </summary>
    public sealed class UnityWebRequestBackend : ILlmBackend
    {
        private readonly ModelSettings _settings;

        public UnityWebRequestBackend(ModelSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public async Task ChatStreamAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct)
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
                    throw;
                }
                catch (LlmTransportException ex)
                {
                    if (ex.StatusCode == 400 && !degraded && request.ResponseFormat != null
                        && ex.Body.Contains("response_format"))
                    {
                        degraded = true;
                        request.ResponseFormat = null;
                        attempt = -1;
                        continue;
                    }
                    bool retryable = ex.StatusCode == 0 || ex.StatusCode >= 500
                        || ex.StatusCode == 429 || ex.StatusCode == 408;
                    if (retryable && attempt < 1 && !gate.Streamed)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    var fallback = new LlmFallbackException(ex.Message, ex);
                    sink.OnError(fallback);
                    throw fallback;
                }
            }
        }

        private async Task OnceAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct)
        {
            string url = _settings.baseUrl.TrimEnd('/') + "/chat/completions";
            string body = JsonConvert.SerializeObject(request);

            var aggregator = new OpenAiStreamAggregator(sink);
            var parser = new SseLineParser(aggregator.HandleDataLine);
            var handler = new SseDownloadHandler(parser);

            using (var req = new UnityWebRequest(url, "POST", handler, null))
            {
                // 修复：此前 uploadHandler 为 null，序列化的 body 从未上传，导致网关收到空请求
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrWhiteSpace(_settings.apiKey))
                    req.SetRequestHeader("Authorization", "Bearer " + _settings.apiKey);
                req.timeout = Math.Max(1, _settings.timeoutMs / 1000);

                AsyncOperation op = req.SendWebRequest();
                using (ct.Register(() => req.Abort()))
                {
                    while (!op.isDone) await Task.Yield();
                }

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string responseBody = handler.RawText;
                    throw new LlmTransportException((int)req.responseCode,
                        string.IsNullOrEmpty(responseBody) ? req.error : Truncate(responseBody, 500));
                }
            }

            parser.Flush();
            if (!aggregator.SawDone && !aggregator.SawFinishReason)
                throw new LlmTransportException(502,
                    "LLM stream ended before [DONE] or finish_reason");
            aggregator.Complete();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max) + "…";
        }

        private sealed class SseDownloadHandler : DownloadHandlerScript
        {
            private readonly SseLineParser _parser;
            private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
            private readonly StringBuilder _raw = new StringBuilder();

            public string RawText { get { return _raw.ToString(); } }

            public SseDownloadHandler(SseLineParser parser)
            {
                _parser = parser;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (dataLength > 0)
                {
                    char[] chars = new char[Encoding.UTF8.GetMaxCharCount(dataLength)];
                    int decoded = _utf8.GetChars(data, 0, dataLength, chars, 0);
                    if (decoded > 0)
                    {
                        string text = new string(chars, 0, decoded);
                        if (_raw.Length < 4096)
                        {
                            int take = Math.Min(text.Length, 4096 - _raw.Length);
                            _raw.Append(text, 0, take);
                        }
                        _parser.Feed(text);
                    }
                }
                return true;
            }
        }

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

