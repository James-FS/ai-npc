using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Logging;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AIBot.Server
{
    /// <summary>每天执行轻量日志保留清理；MySQL 与 JSON 模式共用。</summary>
    public sealed class LogMaintenanceService : BackgroundService
    {
        private readonly StorageOptions _storage;
        private readonly MySqlConnectionFactory _mysql;
        private readonly RuntimeLogService _logs;
        private readonly int _chatDays;
        private readonly int _auditDays;
        private readonly TimeSpan _interval;

        public LogMaintenanceService(StorageOptions storage, RuntimeLogService logs,
            IConfiguration configuration, MySqlConnectionFactory mysql = null)
        {
            _storage = storage;
            _mysql = mysql;
            _logs = logs;
            _chatDays = Math.Max(1, configuration.GetValue<int?>("Logging:ChatRetentionDays") ?? 30);
            _auditDays = Math.Max(1, configuration.GetValue<int?>("Logging:AuditRetentionDays") ?? 365);
            int hours = Math.Max(1, configuration.GetValue<int?>("Logging:MaintenanceIntervalHours") ?? 24);
            _interval = TimeSpan.FromHours(hours);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RunOnce();
                try { await Task.Delay(_interval, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
        }

        private void RunOnce()
        {
            try
            {
                int runtimeDeleted = _logs.CleanupNow();
                int chatDeleted = 0;
                int auditDeleted = 0;
                if (_storage.IsMySql && _mysql != null)
                {
                    using (IDbConnection connection = _mysql.OpenConnection())
                    {
                        DateTime chatCutoff = DateTime.UtcNow.AddDays(-_chatDays);
                        DateTime auditCutoff = DateTime.UtcNow.AddDays(-_auditDays);
                        chatDeleted = connection.Execute(
                            "DELETE FROM chat_logs WHERE ts < @Cutoff", new { Cutoff = chatCutoff });
                        auditDeleted = connection.Execute(
                            "DELETE FROM memory_audits WHERE ts < @Cutoff", new { Cutoff = auditCutoff });
                    }
                }
                else
                {
                    auditDeleted = CleanupAuditFiles();
                }
                _logs.Write(LogLevel.Info, "LogMaintenance", "cleanup_completed",
                    "日志保留清理完成: runtime=" + runtimeDeleted + ", chat=" + chatDeleted
                    + ", audit=" + auditDeleted);
            }
            catch (Exception ex)
            {
                _logs.Write(LogLevel.Warning, "LogMaintenance", "cleanup_failed",
                    "日志保留清理失败: " + ex.Message, null, ex);
            }
        }

        private int CleanupAuditFiles()
        {
            string root = DataStore.FindDataRoot();
            string logsRoot = root == null ? null : Path.Combine(root, "logs");
            if (logsRoot == null || !Directory.Exists(logsRoot)) return 0;
            DateTime cutoff = DateTime.UtcNow.AddDays(-_auditDays);
            int deleted = 0;
            foreach (string directory in Directory.GetDirectories(logsRoot, "memory-audit",
                SearchOption.AllDirectories))
            {
                foreach (string file in Directory.GetFiles(directory, "*.jsonl"))
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
            }
            return deleted;
        }
    }
}
