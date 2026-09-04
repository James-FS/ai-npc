using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AIBot.Core.Llm
{
    /// <summary>
    /// 上游 OpenAI 原生 chunk 的聚合器：消费 SseLineParser 输出的 data 载荷，
    /// 聚合 token 增量与按 index 分片到达的 tool_call.arguments，驱动 ILlmStreamSink。
    /// </summary>
    public sealed class OpenAiStreamAggregator
    {
        private readonly ILlmStreamSink _sink;
        private readonly StringBuilder _text = new StringBuilder();
        private readonly SortedDictionary<int, ToolCallDto> _toolCalls = new SortedDictionary<int, ToolCallDto>();
        private Usage _usage;
        private bool _completed;

        public bool SawDone { get; private set; }
        public bool SawFinishReason { get; private set; }
        public bool SawValidChunk { get; private set; }
        public bool IsCompleted { get { return _completed; } }

        public OpenAiStreamAggregator(ILlmStreamSink sink)
        {
            _sink = sink;
        }

        /// <summary>处理一行 data 载荷（JSON 或 "[DONE]"）。</summary>
        public void HandleDataLine(string payload)
        {
            if (_completed) return;
            if (payload == "[DONE]")
            {
                SawDone = true;
                Complete();
                return;
            }

            JObject chunk;
            try { chunk = JObject.Parse(payload); }
            catch (System.Exception) { return; }        // 容忍上游偶发非 JSON 行
            SawValidChunk = true;

            JToken usageNode = chunk["usage"];
            if (usageNode != null && usageNode.HasValues)
            {
                _usage = usageNode.ToObject<Usage>() ?? _usage;
            }

            JArray choices = chunk["choices"] as JArray;
            if (choices == null || choices.Count == 0) return;
            string finishReason = choices[0]["finish_reason"]?.ToString();
            if (!string.IsNullOrEmpty(finishReason)) SawFinishReason = true;
            JObject delta = choices[0]["delta"] as JObject;
            if (delta == null) return;

            string content = delta["content"]?.ToString();
            if (!string.IsNullOrEmpty(content))
            {
                _text.Append(content);
                _sink.OnToken(content);
            }

            string reasoning = delta["reasoning_content"]?.ToString();
            if (!string.IsNullOrEmpty(reasoning))
            {
                var reasoningSink = _sink as IReasoningSink;
                if (reasoningSink != null) reasoningSink.OnReasoningToken(reasoning);
            }

            JArray toolCalls = delta["tool_calls"] as JArray;
            if (toolCalls != null) AggregateToolCalls(toolCalls);
        }

        private void AggregateToolCalls(JArray toolCalls)
        {
            foreach (JToken token in toolCalls)
            {
                int index = token["index"]?.Value<int>() ?? 0;
                ToolCallDto agg;
                if (!_toolCalls.TryGetValue(index, out agg))
                {
                    agg = new ToolCallDto { Function = new FunctionCall() };
                    _toolCalls[index] = agg;
                }
                string id = token["id"]?.ToString();
                if (!string.IsNullOrEmpty(id)) agg.Id = id;
                JObject function = token["function"] as JObject;
                if (function != null)
                {
                    string name = function["name"]?.ToString();
                    if (!string.IsNullOrEmpty(name)) agg.Function.Name = name;
                    string args = function["arguments"]?.ToString();
                    if (!string.IsNullOrEmpty(args))
                    {
                        agg.Function.Arguments = (agg.Function.Arguments ?? string.Empty) + args;
                    }
                }
            }
        }

        /// <summary>流正常结束（[DONE] 或传输完成）时调用，幂等。</summary>
        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            foreach (KeyValuePair<int, ToolCallDto> pair in _toolCalls)
            {
                if (string.IsNullOrEmpty(pair.Value.Function.Arguments)) pair.Value.Function.Arguments = "{}";
                _sink.OnToolCall(pair.Value);
            }
            _sink.OnCompleted(_text.ToString(), _usage ?? new Usage());
        }
    }
}
