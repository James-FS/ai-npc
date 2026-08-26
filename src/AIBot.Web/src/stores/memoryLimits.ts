import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { MemoryPolicyLimits } from '@/types/memory'

export const useMemoryLimitsStore = defineStore('memory-limits', () => {
  const limits = ref<MemoryPolicyLimits | null>(null)
  const queue = ref({ pending: 0, failed: 0 })
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
      queue.value = queueResult
      retention.value = retentionResult
    } finally { loading.value = false }
  }

  return { limits, queue, retention, loading, load }
})
