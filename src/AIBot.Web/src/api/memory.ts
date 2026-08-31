import { download, request } from './http'
import type {
  EffectiveMemoryPolicy, MemoryAuditEntry, MemoryFact, MemoryListPage,
  MemoryPolicy, MemoryPolicyLimits, MemorySettings, MigrationCandidate,
  PlayerLongTermMemory, SessionSummary,
  MemorySummaryQueueState, StorageInfo, JsonMigrationResult,
} from '@/types/memory'

const enc = encodeURIComponent
const gamePath = (gameId: string, path: string) => `/api/games/${enc(gameId)}${path}`

export const memoryApi = {
  limits: () => request<MemoryPolicyLimits>('/api/admin/memory-limits'),
  storage: () => request<StorageInfo>('/api/admin/storage'),
  migrateJsonToMysql: () => request<JsonMigrationResult>('/api/admin/storage/migrate-json', { method: 'POST' }),
  games: () => request<{ games: string[] }>('/api/games'),
  createGame: (gameId: string) => request<{ gameId: string }>('/api/games', { method: 'POST', body: JSON.stringify({ gameId }) }),
  queue: () => request<MemorySummaryQueueState>('/api/admin/memory-summary-queue'),
  retrySummaryQueue: (filter?: { gameId?: string; npcId?: string; playerId?: string; sessionId?: string }) => request<{ retried: number; pending: number; failedCurrent: number }>('/api/admin/memory-summary-queue/retry', { method: 'POST', body: JSON.stringify(filter ?? {}) }),
  retention: () => request<{ retentionDays: number; scope: string; clearsRelatedSessions: boolean }>('/api/admin/memory-retention'),
  npcIds: (gameId: string) => request<{ gameId: string; npcs: string[] }>(gamePath(gameId, '/npcs')),
  gamePolicy: (gameId: string) => request<{ exists: boolean; policy: MemoryPolicy; limits: MemoryPolicyLimits }>(gamePath(gameId, '/memory-policy')),
  saveGamePolicy: (gameId: string, body: MemoryPolicy) => request<EffectiveMemoryPolicy>(gamePath(gameId, '/memory-policy'), { method: 'PUT', body: JSON.stringify(body) }),
  npcPolicy: (gameId: string, npcId: string) => request<{ npc: MemorySettings; effective: EffectiveMemoryPolicy }>(gamePath(gameId, `/npcs/${enc(npcId)}/memory-policy`)),
  saveNpcPolicy: (gameId: string, npcId: string, body: MemorySettings) => request<EffectiveMemoryPolicy>(gamePath(gameId, `/npcs/${enc(npcId)}/memory-policy`), { method: 'PUT', body: JSON.stringify(body) }),
  previewNpcPolicy: (gameId: string, npcId: string, npcOverride: MemorySettings, sessionOverride?: Partial<MemorySettings>) => request<EffectiveMemoryPolicy>(gamePath(gameId, `/npcs/${enc(npcId)}/memory-policy/preview-effective`), { method: 'POST', body: JSON.stringify({ npcOverride, sessionOverride: sessionOverride ?? null }) }),
  memories: (gameId: string, query: { npcId?: string; playerId?: string; limit?: number; offset?: number }) => {
    const params = new URLSearchParams()
    if (query.npcId) params.set('npcId', query.npcId)
    if (query.playerId) params.set('playerId', query.playerId)
    params.set('limit', String(query.limit ?? 50))
    params.set('offset', String(query.offset ?? 0))
    return request<MemoryListPage>(gamePath(gameId, `/memories?${params}`))
  },
  memory: (gameId: string, npcId: string, playerId: string) => request<PlayerLongTermMemory>(gamePath(gameId, `/memories/${enc(npcId)}/${enc(playerId)}`)),
  sessions: (gameId: string, npcId: string, playerId: string) => request<{ sessions: SessionSummary[] }>(gamePath(gameId, `/sessions?npcId=${enc(npcId)}&playerId=${enc(playerId)}`)),
  saveSummary: (gameId: string, npcId: string, playerId: string, summary: string, expectedVersion: number) => request<PlayerLongTermMemory>(gamePath(gameId, `/memories/${enc(npcId)}/${enc(playerId)}/summary`), { method: 'PUT', body: JSON.stringify({ summary, expectedVersion }) }),
  addFact: (gameId: string, npcId: string, playerId: string, fact: Partial<MemoryFact>, expectedVersion: number) => request<PlayerLongTermMemory>(gamePath(gameId, `/memories/${enc(npcId)}/${enc(playerId)}/facts`), { method: 'POST', body: JSON.stringify({ fact, expectedVersion }) }),
  updateFact: (gameId: string, npcId: string, playerId: string, factId: string, fact: MemoryFact, expectedVersion: number) => request<PlayerLongTermMemory>(gamePath(gameId, `/memories/${enc(npcId)}/${enc(playerId)}/facts/${enc(factId)}`), { method: 'PUT', body: JSON.stringify({ fact, expectedVersion }) }),
  deleteFact: (gameId: string, npcId: string, playerId: string, factId: string, expectedVersion: number) => request<PlayerLongTermMemory>(gamePath(gameId, `/memories/${enc(npcId)}/${enc(playerId)}/facts/${enc(factId)}?expectedVersion=${expectedVersion}`), { method: 'DELETE' }),
  summarize: (gameId: string, npcId: string, playerId: string, sessionId: string) => request<{ queued: boolean; pendingMessages: number }>(gamePath(gameId, `/memories/${enc(npcId)}/${enc(playerId)}/summarize`), { method: 'POST', body: JSON.stringify({ sessionId }) }),
  deleteMemory: (gameId: string, npcId: string, playerId: string, expectedVersion: number) => request<{ ok: boolean }>(gamePath(gameId, `/memories/${enc(npcId)}/${enc(playerId)}?expectedVersion=${expectedVersion}`), { method: 'DELETE' }),
  exportMemory: (gameId: string, npcId: string, playerId: string) => download(gamePath(gameId, `/memories/${enc(npcId)}/${enc(playerId)}/export`), `${gameId}_${npcId}_${playerId}_memory.json`),
  migrations: (gameId: string, npcId?: string) => request<{ total: number; items: MigrationCandidate[] }>(gamePath(gameId, `/memory-migrations${npcId ? `?npcId=${enc(npcId)}` : ''}`)),
  migrate: (gameId: string, npcId: string, playerId: string, sessionId: string) => request<{ migrated: boolean; memory: PlayerLongTermMemory }>(gamePath(gameId, `/sessions/${enc(sessionId)}/migrate-memory?npcId=${enc(npcId)}&playerId=${enc(playerId)}`), { method: 'POST' }),
  audit: (gameId: string, query: { npcId?: string; playerId?: string; action?: string; date?: string; limit?: number; offset?: number }) => {
    const params = new URLSearchParams()
    Object.entries(query).forEach(([key, value]) => { if (value !== undefined && value !== '') params.set(key, String(value)) })
    return request<{ total: number; items: MemoryAuditEntry[]; date: string }>(gamePath(gameId, `/memory-audit?${params}`))
  },
  cleanup: (gameId: string, inactiveDays: number, dryRun: boolean) => request<Record<string, unknown>>(gamePath(gameId, '/memories/cleanup'), { method: 'POST', body: JSON.stringify({ inactiveDays, dryRun, limit: 500 }) }),
}

