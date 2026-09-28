import { request } from './http'

export interface LlmKeyStatus {
  globalConnectionName?: string | null
  globalConnectionMaskedKey?: string | null
  games: GameKeyStatus[]
  npcs: NpcKeyStatus[]
}

export interface GameKeyStatus {
  gameId: string
  hasSummaryModel: boolean
  summaryKeyConfigured: boolean
  summaryMaskedKey?: string | null
  connectionName?: string | null
  connectionMaskedKey?: string | null
}

export interface NpcKeyStatus {
  gameId: string
  npcId: string
  displayName: string
  mainKeyConfigured: boolean
  mainMaskedKey?: string | null
  mainSource: string
  mainConnectionName?: string | null
  mainEffectiveMaskedKey?: string | null
  npcSummaryModel: boolean
  npcSummaryKeyConfigured: boolean
  npcSummaryMaskedKey?: string | null
  summarySource: string
  summaryConnectionName?: string | null
  summaryConnectionScope?: 'npc' | 'game' | null
  summaryMainSource?: string | null
  summaryMaskedKey?: string | null
}

export interface ModelConnectionView {
  id: string
  name: string
  baseUrl: string
  apiFormat: 'openai_chat_completions'
  model: string
  hasApiKey: boolean
  maskedKey?: string | null
}

export interface ModelConnectionInput {
  id: string
  name: string
  baseUrl: string
  apiFormat: 'openai_chat_completions'
  model: string
  apiKey?: string
}

export interface ModelConnectionState {
  connections: ModelConnectionView[]
  bindings: {
    globalMain?: string | null
    npcMain: Record<string, string>
    npcSummary: Record<string, string>
    gameSummary: Record<string, string>
  }
}

export interface ModelConnectionTest {
  ok: boolean
  latencyMs?: number
  model?: string
  code?: string
  status?: number
  diagnosis?: string
}

export const settingsApi = {
  modelConnections: () => request<ModelConnectionState>('/api/admin/model-connections'),
  createModelConnection: (connection: ModelConnectionInput) => request<ModelConnectionState>(
    '/api/admin/model-connections', { method: 'POST', body: JSON.stringify({ connection }) }),
  saveModelConnection: (connection: ModelConnectionInput) => request<ModelConnectionState>(
    `/api/admin/model-connections/${encodeURIComponent(connection.id)}`,
    { method: 'PUT', body: JSON.stringify({ connection }) }),
  deleteModelConnection: (id: string) => request<ModelConnectionState>(
    `/api/admin/model-connections/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  bindModelConnection: (scope: 'global_main' | 'npc_main' | 'npc_summary' | 'game_summary', gameId: string | null,
    npcId: string | null, connectionId: string | null) => request<ModelConnectionState>(
      '/api/admin/model-connections/binding',
      { method: 'PUT', body: JSON.stringify({ scope, gameId, npcId, connectionId }) }),
  testModelConnection: (id: string) => request<ModelConnectionTest>(
    `/api/admin/model-connections/${encodeURIComponent(id)}/test`, { method: 'POST' }),
  llmKeyStatus: () => request<LlmKeyStatus>('/api/admin/settings/llm'),
  updateNpcKey: (gameId: string, npcId: string, apiKey?: string) =>
    request<LlmKeyStatus>(`/api/admin/settings/llm/npcs/${encodeURIComponent(gameId)}/${encodeURIComponent(npcId)}/key`,
      { method: 'PUT', body: JSON.stringify(apiKey ? { apiKey } : { clear: true }) }),
  updateGameSummaryKey: (gameId: string, apiKey?: string) =>
    request<LlmKeyStatus>(`/api/admin/settings/llm/games/${encodeURIComponent(gameId)}/summary-key`,
      { method: 'PUT', body: JSON.stringify(apiKey ? { apiKey } : { clear: true }) }),
  updateNpcSummaryKey: (gameId: string, npcId: string, apiKey?: string) =>
    request<LlmKeyStatus>(`/api/admin/settings/llm/npcs/${encodeURIComponent(gameId)}/${encodeURIComponent(npcId)}/summary-key`,
      { method: 'PUT', body: JSON.stringify(apiKey ? { apiKey } : { clear: true }) }),
}
