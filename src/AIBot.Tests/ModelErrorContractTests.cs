using AIBot.Core;
using AIBot.Core.Llm;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>
    /// 覆盖兜底回复的诊断契约：无论故障发生在传输层还是结构化解析层，
    /// SSE reply 都应带上稳定的 code/status/retryable。
    /// </summary>
    public class ModelErrorContractTests
    {
        [Theory]
        [InlineData(FallbackReasons.StructuredReplyInvalid, false)]
        [InlineData(FallbackReasons.ModelRequestFailed, true)]
        [InlineData(FallbackReasons.AgentFailed, false)]
        public void FromFallbackReason_MapsEveryReasonToStableCode(string reason, bool retryable)
        {
            ModelErrorInfo info = ModelErrorContract.FromFallbackReason(reason);

            Assert.Equal(reason, info.Code);        // code 与 Core 的原因常量同名，便于跨模式对照
            Assert.False(string.IsNullOrWhiteSpace(info.Message));
            Assert.Equal(retryable, info.Retryable);
            Assert.True(info.Status >= 400);
        }

        [Fact]
        public void FromFallbackReason_UnknownReason_FallsBackToGenericCode()
        {
            ModelErrorInfo info = ModelErrorContract.FromFallbackReason("something_new");

            Assert.Equal("model_error", info.Code);
            Assert.False(info.Retryable);
        }

        [Fact]
        public void FromFallbackReason_NullReason_DoesNotThrow()
        {
            ModelErrorInfo info = ModelErrorContract.FromFallbackReason(null);

            Assert.Equal("model_error", info.Code);
        }

        [Fact]
        public void Classify_TransportFailure_StillReportsUpstreamStatus()
        {
            // 传输层异常优先于原因映射：应保留上游语义（如 401 → 未授权）
            var error = new LlmFallbackException("LLM HTTP 401: bad key",
                new LlmTransportException(401, "bad key"));

            ModelErrorInfo info = ModelErrorContract.Classify(error);

            Assert.Equal("model_unauthorized", info.Code);
            Assert.Equal(502, info.Status);
        }
    }
}
