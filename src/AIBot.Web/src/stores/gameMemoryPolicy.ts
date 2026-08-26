import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { EffectiveMemoryPolicy, MemoryPolicy, MemoryPolicyLimits } from '@/types/memory'

export const useGameMemoryPolicyStore = defineStore('game-memory-policy', () => {
  const policy = ref<MemoryPolicy | null>(null)
  const limits = ref<MemoryPolicyLimits | null>(null)
  const effective = ref<EffectiveMemoryPolicy | null>(null)
  const loading = ref(false)
  const saving = ref(false)

  async function load(gameId: string) {
    loading.value = true
    try {
      const result = await memoryApi.gamePolicy(gameId)
      policy.value = structuredClone(result.policy)
      limits.value = result.limits
      effective.value = null
    } finally { loading.value = false }
  }

  async function save(gameId: string) {
    if (!policy.value) return
    saving.value = true
    try {
      effective.value = await memoryApi.saveGamePolicy(gameId, policy.value)
      policy.value = structuredClone(effective.value.policy)
    } finally { saving.value = false }
  }

  return { policy, limits, effective, loading, saving, load, save }
})
