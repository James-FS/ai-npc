using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Config;
using AIBot.Core.Llm;

namespace AIBot.Server
{
    /// <summary>Server summary calls honor the centrally resolved generation settings while Core keeps its Local defaults.</summary>
    internal sealed class SummaryParameterBackend : ILlmBackend
    {
        private readonly ILlmBackend _inner;
        private readonly ModelSettings _settings;

        public SummaryParameterBackend(ILlmBackend inner, ModelSettings settings)
        {
            _inner = inner;
            _settings = settings;
        }

        public Task ChatStreamAsync(LlmRequest request, ILlmStreamSink sink, CancellationToken ct)
        {
            request.Temperature = _settings.temperature;
            request.MaxTokens = _settings.maxTokens;
            return _inner.ChatStreamAsync(request, sink, ct);
        }
    }
}
