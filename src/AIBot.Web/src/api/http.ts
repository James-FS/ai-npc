import { useAppStore } from '@/stores/app'

export class ApiError extends Error {
  status: number
  code: string
  requestId?: string
  payload: unknown

  constructor(status: number, message: string, payload: unknown, code = 'request_failed', requestId?: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.requestId = requestId
    this.payload = payload
  }
}

type ErrorRecord = Record<string, unknown>

function isRecord(value: unknown): value is ErrorRecord {
  return !!value && typeof value === 'object' && !Array.isArray(value)
}

function payloadMessage(payload: unknown): string | null {
  if (typeof payload === 'string' && payload.trim()) return payload.trim()
  if (!isRecord(payload)) return null
  const error = payload.error
  if (typeof error === 'string' && error.trim()) return error.trim()
  if (isRecord(error) && typeof error.message === 'string') return error.message
  for (const key of ['message', 'detail', 'title']) {
    if (typeof payload[key] === 'string' && String(payload[key]).trim()) return String(payload[key]).trim()
  }
  const errors = payload.errors
  if (isRecord(errors)) {
    const messages = Object.values(errors).flatMap(value => Array.isArray(value) ? value : [value])
      .filter(value => typeof value === 'string') as string[]
    if (messages.length) return messages.join('；')
  }
  return null
}

function defaultStatusMessage(status: number) {
  if (status === 0) return '无法连接 Server，请确认服务已启动'
  if (status === 400) return '请求参数不合法'
  if (status === 401) return '管理 API 鉴权失败，请检查 Bearer Token'
  if (status === 403) return '没有执行该操作的权限'
  if (status === 404) return '请求的资源不存在'
  if (status === 409) return '数据已被其他请求修改，请刷新后重试'
  if (status === 429) return '请求过于频繁，请稍后重试'
  if (status >= 500) return 'Server 暂时不可用，请稍后重试'
  return `HTTP ${status}`
}

export function apiErrorFromResponse(response: Response, payload: unknown) {
  const record = isRecord(payload) ? payload : null
  const status = response.status
  const code = typeof record?.code === 'string' ? record.code : 'http_error'
  const requestIdHeader = response.headers.get('X-Request-Id') ?? undefined
  const requestId = typeof record?.requestId === 'string' ? record.requestId : requestIdHeader
  const message = payloadMessage(payload) ?? defaultStatusMessage(status)
  const suffix = requestId ? `（请求 ID: ${requestId}）` : ''
  return new ApiError(status, `${message}${suffix}`, payload, code, requestId)
}

async function readPayload(response: Response): Promise<unknown> {
  const text = await response.text()
  if (!text) return null
  try { return JSON.parse(text) } catch { return text }
}

function apiUrl(path: string) {
  const app = useAppStore()
  return `${app.serverBase.replace(/\/$/, '')}${path}`
}

export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const app = useAppStore()
  const headers = new Headers(init.headers)
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (app.adminToken) headers.set('Authorization', `Bearer ${app.adminToken}`)
  if (app.auditActor) headers.set('X-AIBot-Actor', app.auditActor)
  let response: Response
  try {
    response = await fetch(apiUrl(path), { ...init, headers })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, defaultStatusMessage(0), null, 'network_error')
  }
  const payload = await readPayload(response)
  if (!response.ok) throw apiErrorFromResponse(response, payload)
  return payload as T
}

export async function download(path: string, filename: string) {
  const app = useAppStore()
  const headers = new Headers()
  if (app.adminToken) headers.set('Authorization', `Bearer ${app.adminToken}`)
  if (app.auditActor) headers.set('X-AIBot-Actor', app.auditActor)
  let response: Response
  try {
    response = await fetch(apiUrl(path), { headers })
  } catch (error) {
    throw error instanceof DOMException && error.name === 'AbortError'
      ? error : new ApiError(0, defaultStatusMessage(0), null, 'network_error')
  }
  if (!response.ok) throw apiErrorFromResponse(response, await readPayload(response))
  const href = URL.createObjectURL(await response.blob())
  const anchor = document.createElement('a')
  anchor.href = href
  anchor.download = filename
  anchor.click()
  URL.revokeObjectURL(href)
}
