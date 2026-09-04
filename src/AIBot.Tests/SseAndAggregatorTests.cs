using System.Collections.Generic;
using AIBot.Core.Llm;
using AIBot.Core.Protocol;
using Xunit;

namespace AIBot.Tests
{
    public class SseLineParserTests
    {
        private static List<string> Collect(string script, System.Action<string, SseLineParser> feed)
        {
            var payloads = new List<string>();
            var parser = new SseLineParser(payloads.Add);
            feed(script, parser);
            parser.Flush();
            return payloads;
        }

        [Fact]
        public void OneShot_YieldsAllDataLines()
        {
            var payloads = Collect("data: {\"a\":1}\n\ndata: {\"b\":2}\n\ndata: [DONE]\n\n",
                (s, p) => p.Feed(s));
            Assert.Equal(new List<string> { "{\"a\":1}", "{\"b\":2}", "[DONE]" }, payloads);
        }

        [Fact]
        public void CharByChar_SameAsOneShot()
        {
            string script = "data: {\"a\":1}\n\ndata: {\"b\":\"你好\"}\n\ndata: [DONE]\n\n";
            var oneShot = Collect(script, (s, p) => p.Feed(s));
            var charByChar = Collect(script, (s, p) =>
            {
                foreach (char c in s) p.Feed(c.ToString());
            });
            Assert.Equal(oneShot, charByChar);
        }

        [Fact]
        public void MultiEvent_StuckInOneChunk_IsSplit()
        {
            var payloads = Collect("data: {\"a\":1}\r\ndata: {\"b\":2}\r\n\r\n", (s, p) => p.Feed(s));
            Assert.Equal(2, payloads.Count);
        }

        [Fact]
        public void CommentAndEventLines_AreIgnored()
        {
            var payloads = Collect(": keep-alive\n\nevent: token\ndata: {\"a\":1}\n\n", (s, p) => p.Feed(s));
            Assert.Single(payloads);
            Assert.Equal("{\"a\":1}", payloads[0]);
        }

        [Fact]
        public void TrailingLineWithoutNewline_IsFlushed()
        {
            var payloads = Collect("data: {\"a\":1}", (s, p) => p.Feed(s));
            Assert.Single(payloads);
        }
    }

    public class OpenAiStreamAggregatorTests
    {
        [Fact]
        public void TokenStream_CombinesTextAndUsage()
        {
            var sink = new RecordingSink();
            var aggregator = new OpenAiStreamAggregator(sink);
            string script = Sse.Round(
                Sse.Token("你"),
                Sse.Token("好"),
                Sse.UsageChunk(100, 20));
            new SseLineParser(aggregator.HandleDataLine).Feed(script);

            Assert.Equal("你好", sink.CompletedText);
            Assert.Equal("你好", sink.Tokens.ToString());
            Assert.Equal(100, sink.Usage.PromptTokens);
            Assert.Equal(20, sink.Usage.CompletionTokens);
        }

        [Fact]
        public void ToolCall_FragmentedArguments_AreAggregated()
        {
            var sink = new RecordingSink();
            var aggregator = new OpenAiStreamAggregator(sink);
            string script = Sse.Round(
                Sse.ToolStart("call_1", "give_item"),
                Sse.ToolArgs("{\"item_"),
                Sse.ToolArgs("id\":\"iron_ore\",\"count\":3}"));
            new SseLineParser(aggregator.HandleDataLine).Feed(script);

            ToolCallDto call = Assert.Single(sink.ToolCalls);
            Assert.Equal("call_1", call.Id);
            Assert.Equal("give_item", call.Function.Name);
            Assert.Equal("{\"item_id\":\"iron_ore\",\"count\":3}", call.Function.Arguments);
        }

        [Fact]
        public void ToolCall_EmptyArguments_BecomeEmptyObject()
        {
            var sink = new RecordingSink();
            var aggregator = new OpenAiStreamAggregator(sink);
            new SseLineParser(aggregator.HandleDataLine).Feed(Sse.Round(Sse.ToolStart("call_9", "ping")));

            ToolCallDto call = Assert.Single(sink.ToolCalls);
            Assert.Equal("{}", call.Function.Arguments);
        }

        [Fact]
        public void GarbageLine_IsIgnored()
        {
            var sink = new RecordingSink();
            var aggregator = new OpenAiStreamAggregator(sink);
            var parser = new SseLineParser(aggregator.HandleDataLine);
            parser.Feed("data: not-json\n\n");
            parser.Feed("data: " + Sse.Token("ok") + "\n\n");
            parser.Feed("data: [DONE]\n\n");

            Assert.Equal("ok", sink.CompletedText);
        }

        [Fact]
        public void MissingCompletionMarker_IsDetectable()
        {
            var sink = new RecordingSink();
            var aggregator = new OpenAiStreamAggregator(sink);
            var parser = new SseLineParser(aggregator.HandleDataLine);
            parser.Feed("data: " + Sse.Token("partial") + "\n\n");
            parser.Flush();

            Assert.False(aggregator.SawDone);
            Assert.False(aggregator.SawFinishReason);
            Assert.False(aggregator.IsCompleted);
        }

        [Fact]
        public void FinishReason_IsAcceptedAsCompletionMarker()
        {
            var sink = new RecordingSink();
            var aggregator = new OpenAiStreamAggregator(sink);
            var parser = new SseLineParser(aggregator.HandleDataLine);
            parser.Feed("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            parser.Flush();

            Assert.True(aggregator.SawFinishReason);
            aggregator.Complete();
            Assert.NotNull(sink.CompletedText);
        }
    }

    public class ServerChatEventParserTests
    {
        [Theory]
        [InlineData(null, ServerToolModes.None)]
        [InlineData("", ServerToolModes.None)]
        [InlineData(" SIMULATED ", ServerToolModes.Simulated)]
        public void ToolMode_NormalizesSupportedValues(string input, string expected)
        {
            Assert.True(ServerToolModes.TryNormalize(input, out string normalized));
            Assert.Equal(expected, normalized);
        }

        [Fact]
        public void ToolMode_RejectsUnknownValue()
        {
            Assert.False(ServerToolModes.TryNormalize("remote", out string normalized));
            Assert.Null(normalized);
        }

        [Fact]
        public void Token_IsAlreadyDisplayText()
        {
            Assert.True(ServerChatEventParser.TryParse(
                "{\"type\":\"token\",\"delta\":\"你好，旅行者\"}", out ServerChatEvent parsed));

            Assert.Equal(ServerChatEventKind.Token, parsed.Kind);
            Assert.Equal("你好，旅行者", parsed.Delta);
        }

        [Fact]
        public void FallbackReply_PreservesReplyAndDiagnostic()
        {
            const string json = "{\"type\":\"reply\",\"say\":\"稍后再谈。\",\"emotion\":\"neutral\"," +
                "\"action\":\"idle\",\"fallback\":true,\"usage\":{\"promptTokens\":10,\"completionTokens\":2}," +
                "\"elapsedMs\":123,\"diagnostic\":{\"code\":\"model_timeout\",\"status\":504," +
                "\"message\":\"模型请求超时\",\"retryable\":true}}";

            Assert.True(ServerChatEventParser.TryParse(json, out ServerChatEvent parsed));

            Assert.Equal(ServerChatEventKind.Reply, parsed.Kind);
            Assert.True(parsed.Fallback);
            Assert.Equal("稍后再谈。", parsed.Reply.say);
            Assert.Equal(12, parsed.Usage.TotalTokens);
            Assert.Equal("model_timeout", parsed.Diagnostic.Code);
            Assert.True(parsed.Diagnostic.Retryable);
        }

        [Fact]
        public void Error_DefaultsToTerminal()
        {
            Assert.True(ServerChatEventParser.TryParse(
                "{\"type\":\"error\",\"message\":\"Server 故障\"}", out ServerChatEvent parsed));

            Assert.Equal(ServerChatEventKind.Error, parsed.Kind);
            Assert.True(parsed.Terminal);
            Assert.Equal("Server 故障", parsed.Diagnostic.Message);
        }

        [Fact]
        public void Reasoning_ParsesDelta()
        {
            Assert.True(ServerChatEventParser.TryParse(
                "{\"type\":\"reasoning\",\"delta\":\"先检查任务状态\"}", out ServerChatEvent parsed));

            Assert.Equal(ServerChatEventKind.Reasoning, parsed.Kind);
            Assert.Equal("先检查任务状态", parsed.Delta);
        }

        [Fact]
        public void ToolCall_ParsesExecutionFields()
        {
            const string json = "{\"type\":\"tool_call\",\"callId\":\"call-7\",\"name\":\"give_item\"," +
                "\"args\":{\"item_id\":\"iron_ore\",\"count\":3},\"success\":true," +
                "\"result\":\"已给玩家铁矿 x3\"}";

            Assert.True(ServerChatEventParser.TryParse(json, out ServerChatEvent parsed));

            Assert.Equal(ServerChatEventKind.ToolCall, parsed.Kind);
            Assert.Equal("call-7", parsed.ToolCallId);
            Assert.Equal("give_item", parsed.ToolName);
            Assert.Equal("{\"item_id\":\"iron_ore\",\"count\":3}", parsed.ToolArgumentsJson);
            Assert.True(parsed.ToolSuccess);
            Assert.Equal("已给玩家铁矿 x3", parsed.ToolResult);
        }

        [Fact]
        public void Done_ParsesSessionId()
        {
            Assert.True(ServerChatEventParser.TryParse(
                "{\"type\":\"done\",\"sessionId\":\"s-123\",\"requestId\":\"req-123\"}", out ServerChatEvent parsed));

            Assert.Equal(ServerChatEventKind.Done, parsed.Kind);
            Assert.Equal("s-123", parsed.SessionId);
            Assert.Equal("req-123", parsed.RequestId);
        }

        [Theory]
        [InlineData("req-123")]
        [InlineData("unity:session.turn_1")]
        public void RequestId_AcceptsProtocolSafeValues(string value)
        {
            Assert.True(ChatRequestIds.IsValid(value));
        }

        [Theory]
        [InlineData("")]
        [InlineData("contains space")]
        [InlineData("包含中文")]
        public void RequestId_RejectsUnsafeValues(string value)
        {
            Assert.False(ChatRequestIds.IsValid(value));
        }

        [Fact]
        public void MalformedAndUnknownEvents_AreRejected()
        {
            Assert.False(ServerChatEventParser.TryParse("not-json", out _));
            Assert.False(ServerChatEventParser.TryParse("{\"type\":\"future_event\"}", out _));
        }

        [Fact]
        public void ResponseState_FallbackReplyOverridesEarlierError()
        {
            var state = new ServerChatResponseState();
            Assert.True(ServerChatEventParser.TryParse(
                "{\"type\":\"error\",\"message\":\"模型超时\",\"terminal\":true}", out ServerChatEvent error));
            Assert.True(ServerChatEventParser.TryParse(
                "{\"type\":\"reply\",\"say\":\"稍后再谈。\",\"fallback\":true}", out ServerChatEvent reply));

            state.Apply(error);
            Assert.Equal("模型超时", state.CompletionError.Message);
            state.Apply(reply);

            Assert.NotNull(state.ReplyEvent);
            Assert.True(state.ReplyEvent.Fallback);
            Assert.Null(state.CompletionError);
        }

        [Fact]
        public void FragmentedSse_PreservesUnicodeAndTerminalState()
        {
            var events = new List<ServerChatEvent>();
            var state = new ServerChatResponseState();
            var parser = new SseLineParser(payload =>
            {
                if (!ServerChatEventParser.TryParse(payload, out ServerChatEvent parsed)) return;
                events.Add(parsed);
                state.Apply(parsed);
            });
            string script = "data: {\"type\":\"token\",\"delta\":\"你好\"}\n\n" +
                "data: {\"type\":\"reply\",\"say\":\"你好，旅行者\"}\n\n" +
                "data: {\"type\":\"done\",\"sessionId\":\"s-中文\"}\n\n";
            foreach (char value in script) parser.Feed(value.ToString());
            parser.Flush();

            Assert.Equal(3, events.Count);
            Assert.Equal("你好", events[0].Delta);
            Assert.Equal("你好，旅行者", state.ReplyEvent.Reply.say);
            Assert.True(state.Done);
            Assert.Equal("s-中文", state.SessionId);
        }
    }
}
