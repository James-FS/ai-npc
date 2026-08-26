import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { MemoryAuditEntry } from '@/types/memory'

export const useMemoryAuditStore = defineStore('memory-audit', () => {
  const items = ref<MemoryAuditEntry[]>([])
  const total = ref(0)
  const loading = ref(false)

  async function load(gameId: string, query: Parameters<typeof memoryApi.audit>[1]) {
    loading.value = true
    try {
      const result = await memoryApi.audit(gameId, query)
      items.value = result.items
      total.value = result.total
    } finally { loading.value = false }
  }

  return { items, total, loading, load }
})
