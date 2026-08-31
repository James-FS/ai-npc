export interface ModelSettings {
  baseUrl: string
  apiKey?: string
  model: string
  temperature: number
  maxTokens: number
  timeoutMs: number
}

export interface MemoryPolicy {
  shortTermTurns: number
  summaryThreshold: number
  summaryTrigger: string
  memoryScope: string
  maxFacts: number
  rememberPlayerProfile: boolean
  rememberPromises: boolean
  rememberQuestEvents: boolean
  rememberCasualChat: boolean
  backgroundSummarization: boolean
  summaryModel: ModelSettings | null
  extensions: Record<string, unknown>
}

export interface MemorySettings {
  inheritGameDefaults: boolean
  shortTermTurns?: number | null
  summaryThreshold?: number | null
  summaryTrigger?: string | null
  memoryScope?: string | null
  maxFacts?: number | null
  rememberPlayerProfile?: boolean | null
  rememberPromises?: boolean | null
  rememberQuestEvents?: boolean | null
  rememberCasualChat?: boolean | null
  backgroundSummarization?: boolean | null
  summaryModel?: ModelSettings | null
  useMainSummaryModel?: boolean | null
  extensions?: Record<string, unknown> | null
}

export interface MemoryPolicyLimits {
  maxShortTermTurns: number
  maxSummaryThreshold: number
  maxFacts: number
  allowBackgroundSummarization: boolean
  supportedSummaryTriggers: string[]
  supportedMemoryScopes: string[]
}

export interface EffectiveMemoryPolicy {
  policy: MemoryPolicy
  sources: Record<string, string>
  adjustments: string[]
  limits: MemoryPolicyLimits
}

export interface MemoryFact {
  id: string
  category: string
  key?: string | null
  value: string
  confidence: number
  source?: string | null
  sourceSessionId?: string | null
  createdUtc?: string
  updatedUtc?: string
  pinned: boolean
  expiresUtc?: string | null
}

export interface PlayerLongTermMemory {
  schemaVersion: number
  memoryVersion: number
  gameId: string
  npcId: string
  playerId: string
  summary?: string | null
  facts: MemoryFact[]
  lastSummarizedUtc?: string | null
}

export interface MemoryListItem {
  gameId: string
  npcId: string
  playerId: string
  memoryVersion: number
  factCount: number
  hasSummary: boolean
  lastSummarizedUtc?: string | null
  updatedUtc: string
}

export interface MemoryListPage {
  total: number
  limit: number
  offset: number
  items: MemoryListItem[]
}

export interface SessionSummary {
  sessionId: string
  npcId: string
  playerId?: string | null
  messageCount: number
  pendingSummaryMessages: number
  hasSummary: boolean
  factCount: number
  lastActiveUtc: string
  summaryStatus?: 'idle' | 'waiting' | 'pending' | 'failed' | string
  summaryError?: string | null
  summaryFailedUtc?: string | null
}

export interface MemorySummaryFailure {
  gameId: string
  npcId: string
  playerId: string
  sessionId: string
  attempts: number
  error: string
  failedUtc: string
}

export interface MemorySummaryQueueState {
  pending: number
  failed: number
  failedCurrent: number
  failedTotal: number
  failures: MemorySummaryFailure[]
}

export interface MigrationCandidate {
  npcId: string
  sessionId: string
  hasSummary: boolean
  factCount: number
  lastActiveUtc: string
}

export interface MemoryAuditEntry {
  id: string
  ts: string
  gameId: string
  npcId?: string | null
  playerId?: string | null
  actor: string
  action: string
  before: unknown
  after: unknown
  metadata?: Record<string, unknown> | null
}

export interface StorageInfo {
  provider: 'MySql' | 'Json'
  mysql: { server: string; port: number; database: string; autoMigrate: boolean } | null
  previousProvider: 'MySql' | 'Json' | null
  startedAt: string
}

export interface JsonMigrationResult {
  gameId: string
  scanned: number
  migrated: number
  skipped: number
}

