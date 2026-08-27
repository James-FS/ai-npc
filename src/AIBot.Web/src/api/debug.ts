import { ApiError, apiErrorFromResponse, request } from './http'
import { useAppStore } from '@/stores/app'
import type {
  DebugAgentConfig, DebugChatEvent, DebugLogPage, DebugSession, DebugSessionDetail,
  DebugStats, DebugWorldConfig, PromptPreview, SimGameState,
} from '@/types/debug'

const enc = encodeURIComponent
const gamePath = (gameId: string, path: string) => `/api/games/${enc(gameId)}${path}`

function authHeaders() {
  const app = useAppStore()
  const headers = new Headers({ 'Content-Type': 'application/json' })
  if (app.adminToken) headers.set('Authorization', `Bearer ${app.adminToken}`)
  if (app.auditActor) headers.set('X-AIBot-Actor', app.auditActor)
  return headers
}

export const debugApi = {
  npc: (gameId: string, npcId: string) => request<DebugAgentConfig>(gamePath(gameId, `/npcs/${enc(npcId)}`)),
  saveNpc: (gameId: string, npcId: string, body: DebugAgentConfig) => request<{ ok: boolean }>(gamePath(gameId, `/npcs/${enc(npcId)}`), { method: 'PUT', headers: authHeaders(), body: JSON.stringify(body) }),
  createNpc: (gameId: string, npcId: string) => request<DebugAgentConfig>(gamePath(gameId, '/npcs'), { method: 'POST', headers: authHeaders(), body: JSON.stringify({ npcId, fromTemplate: true }) }),
  deleteNpc: (gameId: string, npcId: string) => request<{ ok: boolean }>(gamePath(gameId, `/npcs/${enc(npcId)}`), { method: 'DELETE', headers: authHeaders() }),
  world: (gameId: string) => request<DebugWorldConfig>(gamePath(gameId, '/world')),
  saveWorld: (gameId: string, body: DebugWorldConfig) => request<{ ok: boolean }>(gamePath(gameId, '/world'), { method: 'PUT', headers: authHeaders(), body: JSON.stringify(body) }),
  previewPrompt: (gameId: string, npcId: string, body: { simState: SimGameState; playerId?: string; sessionId?: string }) => request<PromptPreview>(gamePath(gameId, `/npcs/${enc(npcId)}/preview-prompt`), { method: 'POST', headers: authHeaders(), body: JSON.stringify(body) }),
  sessions: (gameId: string, npcId: string, playerId: string) => request<{ sessions: DebugSession[] }>(gamePath(gameId, `/sessions?npcId=${enc(npcId)}&playerId=${enc(playerId)}`)),
  session: (gameId: string, npcId: string, playerId: string, sessionId: string) => request<DebugSessionDetail>(gamePath(gameId, `/sessions/${enc(sessionId)}?npcId=${enc(npcId)}&playerId=${enc(playerId)}`)),
  deleteSession: (gameId: string, npcId: string, playerId: string, sessionId: string) => request<{ ok: boolean }>(gamePath(gameId, `/sessions/${enc(sessionId)}?npcId=${enc(npcId)}&playerId=${enc(playerId)}`), { method: 'DELETE', headers: authHeaders() }),
  logs: (gameId: string, date: string, npcId: string, limit = 50, offset = 0) => request<DebugLogPage>(gamePath(gameId, `/logs?date=${enc(date)}&npcId=${enc(npcId)}&limit=${limit}&offset=${offset}`)),
  runtimeLogs: (query: { date?: string; level?: string; category?: string; requestId?: string; limit?: number; offset?: number } = {}) => {
    const params = new URLSearchParams()
    Object.entries(query).forEach(([key, value]) => { if (value !== undefined && value !== '') params.set(key, String(value)) })
    return request<DebugLogPage>(`/api/admin/runtime-logs?${params}`)
  },
  stats: (gameId: string) => request<DebugStats>(gamePath(gameId, '/stats')),
  testConnection: (gameId: string, npcId: string, body: { baseUrl?: string; model?: string; apiKey?: string }) => request<Record<string, unknown>>(gamePath(gameId, `/npcs/${enc(npcId)}/test-connection`), { method: 'POST', headers: authHeaders(), body: JSON.stringify(body) }),
}

export async function streamChat(
  gameId: string,
  body: { npcId: string; playerId: string; sessionId: string; message: string; simState: SimGameState; override?: { model?: string } },
  onEvent: (event: DebugChatEvent) => void,
  signal?: AbortSignal,
) {
  const app = useAppStore()
  const url = `${app.serverBase.replace(/\/$/, '')}${gamePath(gameId, '/chat/stream')}`
  let response: Response
  try {
    response = await fetch(url, {
      method: 'POST', headers: authHeaders(), body: JSON.stringify(body), signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, '无法连接 Server，请确认服务已启动', null, 'network_error')
  }
  if (!response.ok) {
    const text = await response.text()
    let payload: unknown = text
    try { payload = text ? JSON.parse(text) : null } catch { /* plain text */ }
    throw apiErrorFromResponse(response, payload)
  }
  if (!response.body) throw new Error('Server 未返回流式响应')
  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  while (true) {
    const result = await reader.read()
    if (result.done) break
    buffer += decoder.decode(result.value, { stream: true })
    const lines = buffer.split(/\r?\n/)
    buffer = lines.pop() ?? ''
    for (const line of lines) {
      if (!line.startsWith('data: ')) continue
      try { onEvent(JSON.parse(line.slice(6)) as DebugChatEvent) } catch { /* 忽略非 JSON SSE 行 */ }
    }
  }
  buffer += decoder.decode()
  if (buffer.startsWith('data: ')) {
    try { onEvent(JSON.parse(buffer.slice(6)) as DebugChatEvent) } catch { /* 忽略不完整 SSE 行 */ }
  }
}
