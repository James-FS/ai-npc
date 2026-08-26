import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { MemoryFact, MemoryListItem, PlayerLongTermMemory, SessionSummary } from '@/types/memory'

export const useMemoryInspectorStore = defineStore('memory-inspector', () => {
  const items = ref<MemoryListItem[]>([])
  const total = ref(0)
  const detail = ref<PlayerLongTermMemory | null>(null)
  const sessions = ref<SessionSummary[]>([])
  const loading = ref(false)

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
      const [memory, sessionResult] = await Promise.all([
        memoryApi.memory(gameId, npcId, playerId),
        memoryApi.sessions(gameId, npcId, playerId),
      ])
      detail.value = memory
      sessions.value = sessionResult.sessions
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

  return { items, total, detail, sessions, loading, search, open, saveSummary, saveFact, deleteFact }
})
