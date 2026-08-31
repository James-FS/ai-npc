CREATE TABLE IF NOT EXISTS memory_summary_jobs (
  job_key VARCHAR(512) NOT NULL,
  game_id VARCHAR(64) NOT NULL,
  npc_id VARCHAR(64) NOT NULL,
  player_id VARCHAR(128) NOT NULL,
  session_id VARCHAR(128) NOT NULL,
  `force` TINYINT(1) NOT NULL DEFAULT 0,
  actor VARCHAR(128) NULL,
  generation BIGINT NOT NULL DEFAULT 0,
  status VARCHAR(16) NOT NULL DEFAULT 'pending',
  attempts INT NOT NULL DEFAULT 0,
  last_error VARCHAR(500) NULL,
  available_utc DATETIME(6) NOT NULL,
  created_utc DATETIME(6) NOT NULL,
  updated_utc DATETIME(6) NOT NULL,
  PRIMARY KEY (job_key),
  KEY ix_summary_jobs_status (status, updated_utc),
  KEY ix_summary_jobs_owner (game_id, npc_id, player_id, session_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

