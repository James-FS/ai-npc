using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AIBot.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CoreLogLevel = AIBot.Core.Logging.LogLevel;

namespace AIBot.Server
{
    public sealed class RuntimeLogContext
    {
        public string RequestId;
        public string GameId;
        public string NpcId;
        public string PlayerId;
        public string SessionId;
        public int? Status;
        public long? DurationMs;
        public string ErrorCode;
    }

    public sealed class RuntimeLogEntry
    {
        public string tsUtc;
        public string level;
        public string category;
        public string @event;
        public string message;
        public string requestId;
        public string gameId;
        public string npcId;
        public string playerId;
        public string sessionId;
        public int? status;
        public long? durationMs;
        public string errorCode;
        public string exceptionType;
    }

    /// <summary>小规模部署使用的结构化运行日志：标准控制台输出 + 按日 JSONL 文件。</summary>
    public sealed class RuntimeLogService
    {
        private static readonly object FileLock = new object();
        private static readonly Regex[] SecretPatterns =
        {
            new Regex("Bearer\\s+[^\\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex("(?i)(api[_-]?key|token|password|pwd)\\s*[:=]\\s*[^;,&\\s]+", RegexOptions.Compiled),
            new Regex("(?i)Password=[^;]*", RegexOptions.Compiled)
        };
        private readonly ILogger<RuntimeLogService> _logger;
        private readonly bool _fileEnabled;
        private readonly int _retentionDays;
        private readonly string _directory;

        public RuntimeLogService(IConfiguration configuration, ILogger<RuntimeLogService> logger)
        {
            _logger = logger;
            _fileEnabled = configuration.GetValue<bool?>("Logging:RuntimeFileEnabled") ?? true;
            _retentionDays = Math.Max(1,
                configuration.GetValue<int?>("Logging:RuntimeRetentionDays") ?? 14);
            string configuredDirectory = configuration["Logging:RuntimeDirectory"];
            string root = DataStore.FindDataRoot();
            _directory = !string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.GetFullPath(configuredDirectory)
                : root != null
                    ? Path.Combine(root, "logs", "runtime")
                    : Path.Combine(Path.GetTempPath(), "aibot-logs", "runtime");
        }

        public string DirectoryPath { get { return _directory; } }
        public int RetentionDays { get { return _retentionDays; } }
        public bool FileEnabled { get { return _fileEnabled; } }

        public void Write(CoreLogLevel level, string category, string eventName, string message,
            RuntimeLogContext context = null, Exception exception = null)
        {
            string safeMessage = Redact(message);
            LogConsole(level, category, eventName, safeMessage, context);
            if (!_fileEnabled) return;
            var entry = new RuntimeLogEntry
            {
                tsUtc = DateTime.UtcNow.ToString("o"),
                level = level.ToString(),
                category = string.IsNullOrWhiteSpace(category) ? "Server" : category,
                @event = string.IsNullOrWhiteSpace(eventName) ? "log" : eventName,
                message = safeMessage,
                requestId = context?.RequestId,
                gameId = context?.GameId,
                npcId = context?.NpcId,
                playerId = context?.PlayerId,
                sessionId = context?.SessionId,
                status = context?.Status,
                durationMs = context?.DurationMs,
                errorCode = context?.ErrorCode,
                exceptionType = exception?.GetType().Name
            };
            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(_directory);
                    string path = Path.Combine(_directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
                    File.AppendAllText(path, JsonConvert.SerializeObject(entry, Formatting.None) + "\n");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Runtime log file write failed: {Message}", Redact(ex.Message));
            }
        }

        public JObject Query(string date, string level, string category, string requestId,
            int limit, int offset)
        {
            DateTime day;
            if (!DateTime.TryParse(date, out day)) day = DateTime.UtcNow;
            int safeLimit = Math.Max(1, Math.Min(200, limit));
            int safeOffset = Math.Max(0, offset);
            var matches = new List<JObject>();
            string file = Path.Combine(_directory, day.ToString("yyyy-MM-dd") + ".jsonl");
            if (File.Exists(file))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        JObject item = JObject.Parse(line);
                        if (!Matches(item, "level", level)) continue;
                        if (!Matches(item, "category", category)) continue;
                        if (!Matches(item, "requestId", requestId)) continue;
                        matches.Add(item);
                    }
                    catch (JsonException) { }
                }
            }
            matches.Reverse();
            return new JObject
            {
                ["date"] = day.ToString("yyyy-MM-dd"),
                ["total"] = matches.Count,
                ["limit"] = safeLimit,
                ["offset"] = safeOffset,
                ["items"] = new JArray(matches.Skip(safeOffset).Take(safeLimit))
            };
        }

        public int CleanupNow()
        {
            if (!Directory.Exists(_directory)) return 0;
            DateTime cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            int deleted = 0;
            foreach (string file in Directory.GetFiles(_directory, "*.jsonl"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return deleted;
        }

        private void LogConsole(CoreLogLevel level, string category, string eventName, string message,
            RuntimeLogContext context)
        {
            string template = "{Category}/{Event}: {Message} requestId={RequestId} status={Status} durationMs={DurationMs}";
            object[] args = { category, eventName, message, context?.RequestId, context?.Status, context?.DurationMs };
            if (level == CoreLogLevel.Error) _logger.LogError(template, args);
            else if (level == CoreLogLevel.Warning) _logger.LogWarning(template, args);
            else if (level == CoreLogLevel.Debug) _logger.LogDebug(template, args);
            else _logger.LogInformation(template, args);
        }

        private static bool Matches(JObject item, string field, string filter)
        {
            return string.IsNullOrWhiteSpace(filter) || string.Equals(item[field]?.ToString(), filter,
                StringComparison.OrdinalIgnoreCase);
        }

        public static string Redact(string value)
        {
            string result = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            foreach (Regex pattern in SecretPatterns) result = pattern.Replace(result, match =>
            {
                int separator = Math.Max(match.Value.IndexOf('='), match.Value.IndexOf(':'));
                return separator >= 0 ? match.Value.Substring(0, separator + 1) + "***" : "Bearer ***";
            });
            return result.Length <= 4000 ? result : result.Substring(0, 4000);
        }
    }

    /// <summary>将 Core 的 ILogSink 桥接到 Server 结构化日志。</summary>
    public sealed class ServerLogSink : ILogSink
    {
        private readonly RuntimeLogService _logs;
        private readonly string _category;
        private readonly RuntimeLogContext _context;

        public ServerLogSink(RuntimeLogService logs, string category, RuntimeLogContext context = null)
        {
            _logs = logs;
            _category = category;
            _context = context;
        }

        public void Log(CoreLogLevel level, string message, Exception ex = null)
        {
            _logs.Write(level, _category, "core", message, _context, ex);
        }
    }
}
