using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Tests
{
    /// <summary>脚本化 Mock 后端：按轮次回放预置 SSE 文本（真实解析路径，非接口桩）。</summary>
    public sealed class MockLlmBackend : ILlmBackend
    {
        private readonly Queue<string> _rounds = new Queue<string>();
        public readonly List<LlmRequest> Requests = new List<LlmRequest>();

        public MockLlmBackend(params string[] sseRounds)
        {
            foreach (string round in sseRounds) _rounds.Enqueue(round);
        }

        public Task ChatStreamAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct)
        {
            Requests.Add(request);
            if (_rounds.Count == 0)
            {
                var ex = new LlmFallbackException("mock: no scripted round left");
                sink.OnError(ex);
                throw ex;
            }
            string script = _rounds.Dequeue();
            var aggregator = new OpenAiStreamAggregator(sink);
            var parser = new SseLineParser(aggregator.HandleDataLine);
            parser.Feed(script);
            parser.Flush();
            aggregator.Complete();
            return Task.CompletedTask;
        }
    }

    /// <summary>记录全部回调，供断言。</summary>
    public sealed class RecordingSink : ILlmStreamSink
    {
        public readonly StringBuilder Tokens = new StringBuilder();
        public readonly List<ToolCallDto> ToolCalls = new List<ToolCallDto>();
        public string CompletedText;
        public Usage Usage;
        public Exception Error;

        public void OnToken(string delta) { Tokens.Append(delta); }
        public void OnToolCall(ToolCallDto call) { ToolCalls.Add(call); }
        public void OnCompleted(string fullText, Usage usage) { CompletedText = fullText; Usage = usage; }
        public void OnError(Exception ex) { Error = ex; }
    }

    /// <summary>构造 OpenAI 风格 SSE 脚本。</summary>
    public static class Sse
    {
        public static string Round(params string[] dataJsons)
        {
            var sb = new StringBuilder();
            foreach (string json in dataJsons) sb.Append("data: ").Append(json).Append("\n\n");
            sb.Append("data: [DONE]\n\n");
            return sb.ToString();
        }

        public static string Token(string content)
        {
            return new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["index"] = 0,
                    ["delta"] = new JObject { ["content"] = content }
                })
            }.ToString(Formatting.None);
        }

        public static string ToolStart(string id, string name)
        {
            return new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["index"] = 0,
                    ["delta"] = new JObject
                    {
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["index"] = 0,
                            ["id"] = id,
                            ["type"] = "function",
                            ["function"] = new JObject { ["name"] = name, ["arguments"] = "" }
                        })
                    }
                })
            }.ToString(Formatting.None);
        }

        public static string ToolArgs(string fragment)
        {
            return new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["index"] = 0,
                    ["delta"] = new JObject
                    {
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["index"] = 0,
                            ["function"] = new JObject { ["arguments"] = fragment }
                        })
                    }
                })
            }.ToString(Formatting.None);
        }

        public static string UsageChunk(int promptTokens, int completionTokens)
        {
            return new JObject
            {
                ["choices"] = new JArray(),
                ["usage"] = new JObject
                {
                    ["prompt_tokens"] = promptTokens,
                    ["completion_tokens"] = completionTokens,
                    ["total_tokens"] = promptTokens + completionTokens
                }
            }.ToString(Formatting.None);
        }
    }
}
