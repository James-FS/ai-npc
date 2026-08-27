import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { MemoryPolicyLimits, MemorySummaryQueueState } from '@/types/memory'

export const useMemoryLimitsStore = defineStore('memory-limits', () => {
  const limits = ref<MemoryPolicyLimits | null>(null)
  const queue = ref<MemorySummaryQueueState>({ pending: 0, failed: 0, failedCurrent: 0, failedTotal: 0, failures: [] })
  const retention = ref({
    retentionDays: 90,
    scope: 'player_long_term_memory_and_related_sessions',
    clearsRelatedSessions: true,
  })
  const loading = ref(false)

  async function load() {
    loading.value = true
    try {
      const [limitResult, queueResult, retentionResult] = await Promise.all([
        memoryApi.limits(), memoryApi.queue(), memoryApi.retention(),
      ])
      limits.value = limitResult
      queue.value = {
        pending: queueResult.pending ?? 0,
        failed: queueResult.failed ?? 0,
        failedCurrent: queueResult.failedCurrent ?? 0,
        failedTotal: queueResult.failedTotal ?? queueResult.failed ?? 0,
        failures: queueResult.failures ?? [],
      }
      retention.value = retentionResult
    } finally { loading.value = false }
  }

  return { limits, queue, retention, loading, load }
})
