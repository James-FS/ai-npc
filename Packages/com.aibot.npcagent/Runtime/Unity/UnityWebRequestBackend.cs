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
            string url = _settings.baseUrl.TrimEnd('/') + "/chat/completions";
            string body = JsonConvert.SerializeObject(request);

            var aggregator = new OpenAiStreamAggregator(sink);
            var parser = new SseLineParser(aggregator.HandleDataLine);
            var handler = new SseDownloadHandler(parser);

            using (var req = new UnityWebRequest(url, "POST", handler, null))
            {
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + _settings.apiKey);
                req.timeout = Math.Max(1, _settings.timeoutMs / 1000);

                AsyncOperation op = req.SendWebRequest();
                using (ct.Register(() => req.Abort()))
                {
                    while (!op.isDone) await Task.Yield();
                }

                if (ct.IsCancellationRequested)
                {
                    sink.OnError(new OperationCanceledException(ct));
                    throw new OperationCanceledException(ct);
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    var ex = new LlmFallbackException("LLM request failed: " + req.result + " " + req.error
                        + " body=" + Truncate(req.downloadHandler != null ? req.downloadHandler.text : "", 500));
                    sink.OnError(ex);
                    throw ex;
                }
            }

            parser.Flush();
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
                    if (decoded > 0) _parser.Feed(new string(chars, 0, decoded));
                }
                return true;
            }
        }
    }
}
