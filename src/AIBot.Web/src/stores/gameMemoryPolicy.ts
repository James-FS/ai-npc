import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { EffectiveMemoryPolicy, MemoryPolicy, MemoryPolicyLimits } from '@/types/memory'

function cloneJson<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

export const useGameMemoryPolicyStore = defineStore('game-memory-policy', () => {
  const policy = ref<MemoryPolicy | null>(null)
  const limits = ref<MemoryPolicyLimits | null>(null)
  const effective = ref<EffectiveMemoryPolicy | null>(null)
  const loading = ref(false)
  const saving = ref(false)
  const contextEpoch = ref(0)
  let loadRequest = 0, saveRequest = 0
  let owner: string | null = null

  async function load(gameId: string) {
    const request = ++loadRequest
    contextEpoch.value++
    saveRequest++
    owner = gameId
    policy.value = null; effective.value = null
    saving.value = false
    loading.value = true
    try {
      const result = await memoryApi.gamePolicy(gameId)
      if (request !== loadRequest || owner !== gameId) return
      policy.value = cloneJson(result.policy)
      limits.value = result.limits
      effective.value = null
    } catch (error) {
      if (request === loadRequest && owner === gameId) throw error
    } finally { if (request === loadRequest) loading.value = false }
  }

  async function save(gameId: string, value = policy.value, expectedEpoch = contextEpoch.value) {
    if (!value || owner !== gameId || expectedEpoch !== contextEpoch.value) return false
    const request = ++saveRequest, epoch = loadRequest, snapshot = cloneJson(value)
    saving.value = true
    try {
      let result
      try { result = await memoryApi.saveGamePolicy(gameId, snapshot) }
      catch (error) { if (request === saveRequest && epoch === loadRequest && owner === gameId) throw error; return false }
      if (request === saveRequest && epoch === loadRequest && owner === gameId) { effective.value = result; policy.value = cloneJson(result.policy); return true }
      return false
    } finally { if (request === saveRequest && epoch === loadRequest) saving.value = false }
  }

  function invalidate() { loadRequest++; saveRequest++; contextEpoch.value++; owner = null; policy.value = null; effective.value = null; loading.value = false; saving.value = false }

  return { policy, limits, effective, loading, saving, contextEpoch, load, save, invalidate }
})
