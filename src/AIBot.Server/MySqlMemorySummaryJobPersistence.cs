using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;

namespace AIBot.Server
{
    /// <summary>摘要任务的 MySQL 持久化；仅在 Storage=MySql 时启用。</summary>
    public sealed class MySqlMemorySummaryJobPersistence
    {
        private readonly MySqlConnectionFactory _factory;

        public MySqlMemorySummaryJobPersistence(MySqlConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public void UpsertPending(MemorySummaryJob job, string key)
        {
            using (IDbConnection connection = _factory.OpenConnection())
            {
                connection.Execute(@"
INSERT INTO memory_summary_jobs
 (job_key,game_id,npc_id,player_id,session_id,force,actor,generation,status,attempts,last_error,available_utc,created_utc,updated_utc)
VALUES (@Key,@GameId,@NpcId,@PlayerId,@SessionId,@Force,@Actor,@Generation,'pending',0,NULL,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE
 status=IF(status='succeeded','pending',status), force=VALUES(force), actor=VALUES(actor),
 generation=VALUES(generation), available_utc=UTC_TIMESTAMP(6), updated_utc=UTC_TIMESTAMP(6)",
                    new
                    {
                        Key = key, job.GameId, job.NpcId, job.PlayerId, job.SessionId,
                        job.Force, Actor = job.Actor ?? "system", job.Generation
                    });
            }
        }

        public List<MemorySummaryJobRecord> LoadRecoverable()
        {
            using (IDbConnection connection = _factory.OpenConnection())
            {
                return connection.Query<MemorySummaryJobRecord>(@"
SELECT job_key AS JobKey, game_id AS GameId, npc_id AS NpcId, player_id AS PlayerId,
       session_id AS SessionId, force AS Force, actor AS Actor, generation AS Generation,
       status AS Status, attempts AS Attempts, last_error AS LastError
FROM memory_summary_jobs
WHERE status IN ('pending','processing')
ORDER BY created_utc").ToList();
            }
        }

        public List<MemorySummaryJobRecord> LoadFailed()
        {
            using (IDbConnection connection = _factory.OpenConnection())
            {
                return connection.Query<MemorySummaryJobRecord>(@"
SELECT job_key AS JobKey, game_id AS GameId, npc_id AS NpcId, player_id AS PlayerId,
       session_id AS SessionId, force AS Force, actor AS Actor, generation AS Generation,
       status AS Status, attempts AS Attempts, last_error AS LastError, updated_utc AS UpdatedUtc
FROM memory_summary_jobs WHERE status='failed'
ORDER BY updated_utc DESC").ToList();
            }
        }

        public void MarkProcessing(string key)
        {
            ExecuteStatus(key, "processing", null, "attempts=attempts+1");
        }

        public void MarkSucceeded(string key)
        {
            using (IDbConnection connection = _factory.OpenConnection())
                connection.Execute("DELETE FROM memory_summary_jobs WHERE job_key=@Key", new { Key = key });
        }

        public void MarkFailed(string key, string error)
        {
            ExecuteStatus(key, "failed", error, null);
        }

        public void MarkPending(string key)
        {
            ExecuteStatus(key, "pending", null, null);
        }

        public void DeleteForPlayer(string gameId, string npcId, string playerId)
        {
            using (IDbConnection connection = _factory.OpenConnection())
                connection.Execute(@"DELETE FROM memory_summary_jobs
WHERE game_id=@GameId AND npc_id=@NpcId AND player_id=@PlayerId",
                    new { GameId = gameId, NpcId = npcId, PlayerId = playerId });
        }

        private void ExecuteStatus(string key, string status, string error, string extra)
        {
            string sql = "UPDATE memory_summary_jobs SET status=@Status, last_error=@Error, updated_utc=UTC_TIMESTAMP(6)";
            if (!string.IsNullOrEmpty(extra)) sql += ", " + extra;
            sql += " WHERE job_key=@Key";
            using (IDbConnection connection = _factory.OpenConnection())
                connection.Execute(sql, new { Key = key, Status = status, Error = error });
        }
    }

    public sealed class MemorySummaryJobRecord
    {
        public string JobKey { get; set; }
        public string GameId { get; set; }
        public string NpcId { get; set; }
        public string PlayerId { get; set; }
        public string SessionId { get; set; }
        public bool Force { get; set; }
        public string Actor { get; set; }
        public long Generation { get; set; }
        public string Status { get; set; }
        public int Attempts { get; set; }
        public string LastError { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
