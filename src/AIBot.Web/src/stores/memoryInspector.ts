import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { MemoryFact, MemoryListItem, PlayerLongTermMemory, SessionSummary } from '@/types/memory'
import type { MemorySummaryQueueState } from '@/types/memory'

export const useMemoryInspectorStore = defineStore('memory-inspector', () => {
  const items = ref<MemoryListItem[]>([])
  const total = ref(0)
  const detail = ref<PlayerLongTermMemory | null>(null)
  const sessions = ref<SessionSummary[]>([])
  const listLoading = ref(false), detailLoading = ref(false)
  const loading = computed(() => listLoading.value || detailLoading.value)
  const queueLoading = ref(false)
  const queue = ref<MemorySummaryQueueState>({ pending: 0, failed: 0, failedCurrent: 0, failedTotal: 0, failures: [] })
  let listRequest = 0, detailRequest = 0, queueRequest = 0, mutationRequest = 0
  let listGame = '', detailOwner = ''

  async function loadQueue() {
    const request = ++queueRequest
    queueLoading.value = true
    try {
      const result = await memoryApi.queue()
      if (request !== queueRequest) return
      queue.value = {
        pending: result.pending ?? 0,
        failed: result.failed ?? 0,
        failedCurrent: result.failedCurrent ?? 0,
        failedTotal: result.failedTotal ?? result.failed ?? 0,
        failures: result.failures ?? [],
      }
    } catch (error) { if (request === queueRequest) throw error }
    finally { if (request === queueRequest) queueLoading.value = false }
  }

  async function search(gameId: string, query: { npcId?: string; playerId?: string; limit?: number; offset?: number }) {
    const request = ++listRequest
    items.value = []; total.value = 0
    if (listGame !== gameId) listGame = gameId
    listLoading.value = true
    try {
      const page = await memoryApi.memories(gameId, query)
      if (request !== listRequest || listGame !== gameId) return
      items.value = page.items
      total.value = page.total
    } catch (error) {
      if (request === listRequest && listGame === gameId) throw error
    } finally { if (request === listRequest) listLoading.value = false }
  }

  async function open(gameId: string, npcId: string, playerId: string) {
    const request = ++detailRequest, requestedOwner = `${gameId}/${npcId}/${playerId}`
    detailOwner = requestedOwner; mutationRequest++
    detail.value = null; sessions.value = []
    detailLoading.value = true
    try {
      const [memory, sessionResult, queueResult] = await Promise.all([
        memoryApi.memory(gameId, npcId, playerId),
        memoryApi.sessions(gameId, npcId, playerId),
        memoryApi.queue(),
      ])
      if (request !== detailRequest || detailOwner !== requestedOwner) return
      detail.value = memory
      sessions.value = sessionResult.sessions
      queue.value = {
        pending: queueResult.pending ?? 0,
        failed: queueResult.failed ?? 0,
        failedCurrent: queueResult.failedCurrent ?? 0,
        failedTotal: queueResult.failedTotal ?? queueResult.failed ?? 0,
        failures: queueResult.failures ?? [],
      }
    } catch (error) {
      if (request === detailRequest && detailOwner === requestedOwner) throw error
    } finally { if (request === detailRequest) detailLoading.value = false }
  }

  async function saveSummary(gameId: string, summary: string) {
    const current = detail.value, request = ++mutationRequest, epoch = detailRequest
    if (!current || detailOwner !== `${gameId}/${current.npcId}/${current.playerId}`) return false
    const key = detailOwner
    let result
    try { result = await memoryApi.saveSummary(gameId, current.npcId, current.playerId, summary, current.memoryVersion) }
    catch (error) { if (request === mutationRequest && epoch === detailRequest && detailOwner === key) throw error; return false }
    if (request === mutationRequest && epoch === detailRequest && detailOwner === key) { detail.value = result; return true }
    return false
  }

  async function saveFact(gameId: string, fact: Partial<MemoryFact>, factId?: string) {
    const current = detail.value, request = ++mutationRequest, epoch = detailRequest
    if (!current || detailOwner !== `${gameId}/${current.npcId}/${current.playerId}`) return false
    const key = detailOwner, snapshot = { ...fact }
    let result
    try { result = factId
      ? await memoryApi.updateFact(gameId, current.npcId, current.playerId, factId, snapshot as MemoryFact, current.memoryVersion)
      : await memoryApi.addFact(gameId, current.npcId, current.playerId, snapshot, current.memoryVersion) }
    catch (error) { if (request === mutationRequest && epoch === detailRequest && detailOwner === key) throw error; return false }
    if (request === mutationRequest && epoch === detailRequest && detailOwner === key) { detail.value = result; return true }
    return false
  }

  async function deleteFact(gameId: string, factId: string) {
    const current = detail.value, request = ++mutationRequest, epoch = detailRequest
    if (!current || detailOwner !== `${gameId}/${current.npcId}/${current.playerId}`) return false
    const key = detailOwner
    let result
    try { result = await memoryApi.deleteFact(gameId, current.npcId, current.playerId, factId, current.memoryVersion) }
    catch (error) { if (request === mutationRequest && epoch === detailRequest && detailOwner === key) throw error; return false }
    if (request === mutationRequest && epoch === detailRequest && detailOwner === key) { detail.value = result; return true }
    return false
  }

  function invalidate() { listRequest++; detailRequest++; queueRequest++; mutationRequest++; detailOwner = ''; listGame = ''; items.value = []; total.value = 0; detail.value = null; sessions.value = []; listLoading.value = false; detailLoading.value = false; queueLoading.value = false }
  function closeDetail() { detailRequest++; mutationRequest++; detailOwner = ''; detail.value = null; sessions.value = []; detailLoading.value = false }

  return { items, total, detail, sessions, queue, loading, queueLoading, search, open, loadQueue, saveSummary, saveFact, deleteFact, invalidate, closeDetail }
})
