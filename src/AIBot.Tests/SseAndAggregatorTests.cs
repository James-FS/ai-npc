using System.Collections.Generic;
using AIBot.Core.Llm;
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
    }
}
