import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { MemoryFact, MemoryListItem, PlayerLongTermMemory, SessionSummary } from '@/types/memory'
import type { MemorySummaryQueueState } from '@/types/memory'

export const useMemoryInspectorStore = defineStore('memory-inspector', () => {
  const items = ref<MemoryListItem[]>([])
  const total = ref(0)
  const detail = ref<PlayerLongTermMemory | null>(null)
  const sessions = ref<SessionSummary[]>([])
  const loading = ref(false)
  const queueLoading = ref(false)
  const queue = ref<MemorySummaryQueueState>({ pending: 0, failed: 0, failedCurrent: 0, failedTotal: 0, failures: [] })

  async function loadQueue() {
    queueLoading.value = true
    try {
      const result = await memoryApi.queue()
      queue.value = {
        pending: result.pending ?? 0,
        failed: result.failed ?? 0,
        failedCurrent: result.failedCurrent ?? 0,
        failedTotal: result.failedTotal ?? result.failed ?? 0,
        failures: result.failures ?? [],
      }
    }
    finally { queueLoading.value = false }
  }

  async function search(gameId: string, query: { npcId?: string; playerId?: string; limit?: number; offset?: number }) {
    loading.value = true
    try {
      const page = await memoryApi.memories(gameId, query)
      items.value = page.items
      total.value = page.total
    } finally { loading.value = false }
  }

  async function open(gameId: string, npcId: string, playerId: string) {
    loading.value = true
    try {
      const [memory, sessionResult, queueResult] = await Promise.all([
        memoryApi.memory(gameId, npcId, playerId),
        memoryApi.sessions(gameId, npcId, playerId),
        memoryApi.queue(),
      ])
      detail.value = memory
      sessions.value = sessionResult.sessions
      queue.value = {
        pending: queueResult.pending ?? 0,
        failed: queueResult.failed ?? 0,
        failedCurrent: queueResult.failedCurrent ?? 0,
        failedTotal: queueResult.failedTotal ?? queueResult.failed ?? 0,
        failures: queueResult.failures ?? [],
      }
    } finally { loading.value = false }
  }

  async function saveSummary(gameId: string, summary: string) {
    if (!detail.value) return
    detail.value = await memoryApi.saveSummary(gameId, detail.value.npcId, detail.value.playerId, summary, detail.value.memoryVersion)
  }

  async function saveFact(gameId: string, fact: Partial<MemoryFact>, factId?: string) {
    if (!detail.value) return
    detail.value = factId
      ? await memoryApi.updateFact(gameId, detail.value.npcId, detail.value.playerId, factId, fact as MemoryFact, detail.value.memoryVersion)
      : await memoryApi.addFact(gameId, detail.value.npcId, detail.value.playerId, fact, detail.value.memoryVersion)
  }

  async function deleteFact(gameId: string, factId: string) {
    if (!detail.value) return
    detail.value = await memoryApi.deleteFact(gameId, detail.value.npcId, detail.value.playerId, factId, detail.value.memoryVersion)
  }

  return { items, total, detail, sessions, queue, loading, queueLoading, search, open, loadQueue, saveSummary, saveFact, deleteFact }
})
