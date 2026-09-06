import { request } from './http'

export interface LlmKeyStatus {
  hasConsoleKey: boolean
  maskedTail?: string | null
  envConfigured: boolean
  priority: string
}

export const settingsApi = {
  llmKeyStatus: () => request<LlmKeyStatus>('/api/admin/settings/llm'),
  updateLlmKey: (apiKey: string) =>
    request<LlmKeyStatus>('/api/admin/settings/llm', { method: 'PUT', body: JSON.stringify({ apiKey }) }),
  clearLlmKey: () =>
    request<LlmKeyStatus>('/api/admin/settings/llm', { method: 'PUT', body: JSON.stringify({ clear: true }) }),
}
