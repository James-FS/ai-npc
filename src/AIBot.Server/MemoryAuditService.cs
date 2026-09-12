using System;
using System.IO;
using System.Threading;
using AIBot.Core.Logging;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    public sealed class MemoryAuditWriteException : IOException
    {
        public MemoryAuditWriteException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public sealed class MemoryAuditEntry
    {
        public string id;
        public string ts;
        public string gameId;
        public string npcId;
        public string playerId;
        public string actor;
        public string action;
        public JToken before;
        public JToken after;
        public JObject metadata;
    }

    /// <summary>记忆与策略人工变更审计门面：归一化字段后委托给 <see cref="IMemoryAuditStore"/>，失败重试。</summary>
    public sealed class MemoryAuditService
    {
        private readonly IMemoryAuditStore _store;
        private readonly ILogSink _log = new ConsoleLogSink();

        public MemoryAuditService(IMemoryAuditStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public bool Record(MemoryAuditEntry entry)
        {
            try
            {
                Write(entry);
                return true;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "记忆审计写入失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>关键修改使用的必写审计；短暂 I/O 故障会重试，最终失败则由 API/队列显式处理。</summary>
        public void RecordRequired(MemoryAuditEntry entry)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Write(entry);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 2) Thread.Sleep(25 * (attempt + 1));
                }
            }
            _log.Log(LogLevel.Error, "记忆审计写入重试耗尽: " + last?.Message);
            throw new MemoryAuditWriteException("记忆审计写入失败，操作结果不能被视为已完整提交", last);
        }

        private void Write(MemoryAuditEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!DataStore.IsValidId(entry.gameId)) throw new ArgumentException("非法 gameId", nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.action))
                throw new ArgumentException("审计 action 不能为空", nameof(entry));

            entry.id = string.IsNullOrEmpty(entry.id) ? "audit-" + Guid.NewGuid().ToString("N") : entry.id;
            entry.ts = string.IsNullOrEmpty(entry.ts) ? DateTime.UtcNow.ToString("o") : entry.ts;
            entry.actor = string.IsNullOrWhiteSpace(entry.actor) ? "admin" : entry.actor;
            _store.Write(entry);
        }

        public JObject Query(string gameId, string npcId, string playerId, string action,
            string date, int limit, int offset)
        {
            return _store.Query(gameId, npcId, playerId, action, date, limit, offset);
        }
    }
}
