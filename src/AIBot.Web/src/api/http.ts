import { useAppStore } from '@/stores/app'

export class ApiError extends Error {
  status: number
  payload: unknown

  constructor(status: number, message: string, payload: unknown) {
    super(message)
    this.status = status
    this.payload = payload
  }
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
  const response = await fetch(apiUrl(path), { ...init, headers })
  const text = await response.text()
  let payload: unknown = null
  if (text) {
    try { payload = JSON.parse(text) } catch { payload = text }
  }
  if (!response.ok) {
    const record = payload && typeof payload === 'object' ? payload as Record<string, unknown> : null
    const message = String(record?.error ?? record?.detail ?? payload ?? `HTTP ${response.status}`)
    throw new ApiError(response.status, message, payload)
  }
  return payload as T
}

export async function download(path: string, filename: string) {
  const app = useAppStore()
  const headers = new Headers()
  if (app.adminToken) headers.set('Authorization', `Bearer ${app.adminToken}`)
  if (app.auditActor) headers.set('X-AIBot-Actor', app.auditActor)
  const response = await fetch(apiUrl(path), { headers })
  if (!response.ok) throw new ApiError(response.status, await response.text(), null)
  const href = URL.createObjectURL(await response.blob())
  const anchor = document.createElement('a')
  anchor.href = href
  anchor.download = filename
  anchor.click()
  URL.revokeObjectURL(href)
}
