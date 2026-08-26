export interface LoreBlock {
  title: string
  content: string
  unlockStage: number
  isSecret: boolean
  enabled: boolean
}

export interface DebugModelSettings {
  baseUrl: string
  apiKey?: string
  model: string
  temperature: number
  maxTokens: number
  timeoutMs: number
}

export interface DebugAgentConfig {
  npcId: string
  displayName: string
  persona: string
  backstory: string
  worldId: string
  loreBlocks: LoreBlock[]
  enabledToolIds: string[]
  fallbackReplies: string[]
  model: DebugModelSettings
  memory: Record<string, unknown>
  output: { emotions: string[]; actions: string[] }
  configVersion: number
}

export interface DebugWorldConfig {
  worldId: string
  description: string
  extraRules: string[]
}

export interface SimGameState {
  stage: number
  favorability: number
  extras: Record<string, string>
  items: Record<string, number>
}

export interface DebugChatEvent {
  type: string
  delta?: string
  say?: string
  emotion?: string
  action?: string
  name?: string
  args?: unknown
  success?: boolean
  result?: string
  message?: string
  fallback?: boolean
  usage?: { promptTokens: number; completionTokens: number }
  elapsedMs?: number
  sessionId?: string
}

export interface DebugSession {
  sessionId: string
  npcId: string
  playerId?: string | null
  messageCount: number
  pendingSummaryMessages: number
  hasSummary: boolean
  factCount: number
  lastActiveUtc: string
}

export interface DebugSessionDetail {
  sessionId: string
  npcId: string
  playerId?: string | null
  messages: { role: string; content: string }[]
  pendingSummaryMessages: number
  summary?: string | null
  facts: string[]
}

export interface PromptPreview {
  systemPrompt: string
  layers: { name: string; text: string; estTokens: number; color: string }[]
  totalEstTokens: number
  budget: number
}

export interface DebugLogPage {
  date: string
  total: number
  limit: number
  offset: number
  items: Record<string, unknown>[]
}

export interface DebugStats {
  totalRequests: number
  fallbackRequests: number
  injectionAttempts: number
  promptTokens: number
  completionTokens: number
  averageElapsedMs: number
  totalFallbacks?: number
  totalPromptTokens?: number
  totalCompletionTokens?: number
  avgMs?: number
  byNpc?: Record<string, unknown>[] | Record<string, Record<string, unknown>>
  note?: string
  [key: string]: unknown
}
