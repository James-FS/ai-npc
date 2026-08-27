CREATE DATABASE IF NOT EXISTS ai_npc CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE ai_npc;

-- 与 AIBot.Server 内置 DatabaseMigrator 相同；可直接在 MySQL 客户端执行。
CREATE TABLE IF NOT EXISTS player_memories (
  game_id VARCHAR(64) NOT NULL,
  npc_id VARCHAR(64) NOT NULL,
  player_id VARCHAR(128) NOT NULL,
  schema_version INT NOT NULL DEFAULT 2,
  memory_version INT NOT NULL DEFAULT 0,
  summary TEXT NULL,
  last_summarized_utc DATETIME(6) NULL,
  created_utc DATETIME(6) NOT NULL,
  updated_utc DATETIME(6) NOT NULL,
  PRIMARY KEY (game_id, npc_id, player_id),
  KEY ix_player_memories_updated (game_id, updated_utc),
  KEY ix_player_memories_npc (game_id, npc_id, player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS memory_audits (
  id VARCHAR(128) NOT NULL,
  ts DATETIME(6) NOT NULL,
  game_id VARCHAR(64) NOT NULL,
  npc_id VARCHAR(64) NULL,
  player_id VARCHAR(128) NULL,
  actor VARCHAR(128) NOT NULL,
  action VARCHAR(128) NOT NULL,
  before_json JSON NULL,
  after_json JSON NULL,
  metadata_json JSON NULL,
  PRIMARY KEY (id),
  KEY ix_memory_audits_game_time (game_id,ts),
  KEY ix_memory_audits_filter (game_id,npc_id,player_id,action,ts)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS chat_logs (
  id BIGINT NOT NULL AUTO_INCREMENT,
  game_id VARCHAR(64) NOT NULL,
  ts DATETIME(6) NOT NULL,
  npc_id VARCHAR(64) NOT NULL,
  player_id VARCHAR(128) NULL,
  session_id VARCHAR(128) NOT NULL,
  legacy_memory_scope TINYINT(1) NOT NULL DEFAULT 0,
  user_message TEXT NULL,
  say TEXT NULL,
  emotion VARCHAR(64) NULL,
  action VARCHAR(64) NULL,
  fallback TINYINT(1) NOT NULL DEFAULT 0,
  prompt_tokens INT NOT NULL DEFAULT 0,
  completion_tokens INT NOT NULL DEFAULT 0,
  elapsed_ms BIGINT NOT NULL DEFAULT 0,
  tools_json JSON NULL,
  injection TINYINT(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (id),
  KEY ix_chat_logs_game_time (game_id,ts),
  KEY ix_chat_logs_npc_time (game_id,npc_id,ts),
  KEY ix_chat_logs_player_time (game_id,player_id,ts)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS sessions (
  game_id VARCHAR(64) NOT NULL,
  npc_id VARCHAR(64) NOT NULL,
  player_key VARCHAR(128) NOT NULL DEFAULT '',
  session_id VARCHAR(128) NOT NULL,
  payload_json JSON NOT NULL,
  has_pending_memory TINYINT(1) NOT NULL DEFAULT 0,
  last_active_utc DATETIME(6) NOT NULL,
  created_utc DATETIME(6) NOT NULL,
  updated_utc DATETIME(6) NOT NULL,
  PRIMARY KEY (game_id,npc_id,player_key,session_id),
  KEY ix_sessions_player_active (game_id,npc_id,player_key,last_active_utc),
  KEY ix_sessions_pending (has_pending_memory,updated_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS memory_facts (
  id VARCHAR(128) NOT NULL,
  game_id VARCHAR(64) NOT NULL,
  npc_id VARCHAR(64) NOT NULL,
  player_id VARCHAR(128) NOT NULL,
  category VARCHAR(64) NULL,
  fact_key VARCHAR(128) NULL,
  fact_value TEXT NULL,
  confidence FLOAT NOT NULL DEFAULT 0,
  source VARCHAR(64) NULL,
  source_session_id VARCHAR(128) NULL,
  created_utc DATETIME(6) NOT NULL,
  updated_utc DATETIME(6) NOT NULL,
  pinned TINYINT(1) NOT NULL DEFAULT 0,
  expires_utc DATETIME(6) NULL,
  PRIMARY KEY (game_id,npc_id,player_id,id),
  KEY ix_memory_facts_owner (game_id, npc_id, player_id, updated_utc),
  CONSTRAINT fk_memory_facts_memory FOREIGN KEY (game_id, npc_id, player_id)
    REFERENCES player_memories (game_id, npc_id, player_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
