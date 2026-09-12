using System;
using System.Threading;
using System.Threading.Tasks;
using AIBot.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AIBot.Server
{
    /// <summary>每天执行轻量日志保留清理；三种存储模式共用。</summary>
    public sealed class LogMaintenanceService : BackgroundService
    {
        private readonly StorageOptions _storage;
        private readonly IMemoryAuditStore _auditStore;
        private readonly RuntimeLogService _logs;
        private readonly int _auditDays;
        private readonly TimeSpan _sessionIdle;
        private readonly TimeSpan _interval;

        public LogMaintenanceService(StorageOptions storage, RuntimeLogService logs,
            IConfiguration configuration, IMemoryAuditStore auditStore = null)
        {
            _storage = storage;
            _auditStore = auditStore;
            _logs = logs;
            _auditDays = Math.Max(1, configuration.GetValue<int?>("Logging:AuditRetentionDays") ?? 365);
            int idleHours = Math.Max(1, configuration.GetValue<int?>("Sessions:MemoryIdleHours") ?? 24);
            _sessionIdle = TimeSpan.FromHours(idleHours);
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
                int sessionsPruned = SessionStore.PruneInactive(_sessionIdle);
                int sessionFilesPruned = SessionStore.PruneInactiveFiles(_sessionIdle);
                // JSON：chat 由 JsonChatLogStore 写入时自清理；audit 走 store 的 DeleteExpired。
                // Mongo：chat/audit 均交 TTL（DELETE 返回 0，跳过）。
                int auditDeleted = _auditStore == null
                    ? 0 : _auditStore.DeleteExpired(DateTime.UtcNow.AddDays(-_auditDays));
                _logs.Write(LogLevel.Info, "LogMaintenance", "cleanup_completed",
                    "日志保留清理完成: provider=" + _storage.Provider + ", runtime=" + runtimeDeleted
                    + ", audit=" + auditDeleted + ", sessionsPruned=" + sessionsPruned
                    + ", sessionFilesPruned=" + sessionFilesPruned);
            }
            catch (Exception ex)
            {
                _logs.Write(LogLevel.Warning, "LogMaintenance", "cleanup_failed",
                    "日志保留清理失败: " + ex.Message, null, ex);
            }
        }
    }
}
