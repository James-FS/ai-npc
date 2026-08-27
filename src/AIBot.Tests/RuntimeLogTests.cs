using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AIBot.Core.Logging;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    public sealed class RuntimeLogTests
    {
        [Fact]
        public void Redact_RemovesSecretsAndCapsPayload()
        {
            string value = RuntimeLogService.Redact(
                "Bearer super-secret apiKey=abc123 Password=hunter2 " + new string('x', 5000));

            Assert.DoesNotContain("super-secret", value);
            Assert.DoesNotContain("abc123", value);
            Assert.DoesNotContain("hunter2", value);
            Assert.True(value.Length <= 4000);
        }

        [Fact]
        public void WriteAndQuery_UsesDailyJsonlAndFiltersRequestId()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "aibot-runtime-log-test-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("Logging:RuntimeFileEnabled", "true"),
                    new System.Collections.Generic.KeyValuePair<string, string>("Logging:RuntimeDirectory", root),
                    new System.Collections.Generic.KeyValuePair<string, string>("Logging:RuntimeRetentionDays", "14")
                }).Build();
                var service = new RuntimeLogService(config, NullLogger<RuntimeLogService>.Instance);
                service.Write(LogLevel.Warning, "Test", "event", "message", new RuntimeLogContext
                {
                    RequestId = "req-test"
                });

                var result = service.Query(DateTime.UtcNow.ToString("yyyy-MM-dd"),
                    "Warning", "Test", "req-test", 50, 0);
                Assert.Equal(1, (int)result["total"]);
                Assert.Equal("req-test", result["items"][0]["requestId"].ToString());
            }
            finally
            {
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            }
        }
    }
}
