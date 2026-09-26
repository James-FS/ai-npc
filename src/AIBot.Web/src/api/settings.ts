import { request } from './http'

export interface LlmKeyStatus {
  hasConsoleKey: boolean
  /** 脱敏串，形如 sk-ca1a2*****bd31（前 7 位 + 末 4 位）；短 key 为 ***，未配置为 null */
  maskedKey?: string | null
  envConfigured: boolean
  /** 环境变量 key 的脱敏串，规则同上；未配置为 null */
  envMaskedKey?: string | null
  priority: string
}

export const settingsApi = {
  llmKeyStatus: () => request<LlmKeyStatus>('/api/admin/settings/llm'),
  updateLlmKey: (apiKey: string) =>
    request<LlmKeyStatus>('/api/admin/settings/llm', { method: 'PUT', body: JSON.stringify({ apiKey }) }),
  clearLlmKey: () =>
    request<LlmKeyStatus>('/api/admin/settings/llm', { method: 'PUT', body: JSON.stringify({ clear: true }) }),
}
