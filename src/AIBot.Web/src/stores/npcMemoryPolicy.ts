import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { EffectiveMemoryPolicy, MemorySettings } from '@/types/memory'

function cloneJson<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

export const useNpcMemoryPolicyStore = defineStore('npc-memory-policy', () => {
  const settings = ref<MemorySettings | null>(null)
  const effective = ref<EffectiveMemoryPolicy | null>(null)
  const loading = ref(false)
  const saving = ref(false)
  const contextEpoch = ref(0)
  let loadRequest = 0, saveRequest = 0, previewRequest = 0
  let owner = ''

  async function load(gameId: string, npcId: string) {
    const request = ++loadRequest
    contextEpoch.value++
    saveRequest++; previewRequest++
    owner = `${gameId}/${npcId}`
    settings.value = null; effective.value = null
    saving.value = false
    if (!npcId) { loading.value = false; return }
    loading.value = true
    try {
      const result = await memoryApi.npcPolicy(gameId, npcId)
      if (request !== loadRequest || owner !== `${gameId}/${npcId}`) return
      settings.value = cloneJson(result.npc)
      effective.value = result.effective
    } catch (error) {
      if (request === loadRequest && owner === `${gameId}/${npcId}`) throw error
    } finally { if (request === loadRequest) loading.value = false }
  }

  async function preview(gameId: string, npcId: string) {
    if (!settings.value || owner !== `${gameId}/${npcId}`) return
    const request = ++previewRequest, epoch = loadRequest, snapshot = cloneJson(settings.value)
    let result
    try { result = await memoryApi.previewNpcPolicy(gameId, npcId, snapshot) }
    catch (error) { if (request === previewRequest && epoch === loadRequest) throw error; return }
    if (request === previewRequest && epoch === loadRequest && owner === `${gameId}/${npcId}`) effective.value = result
  }

  async function save(gameId: string, npcId: string, value = settings.value, expectedEpoch = contextEpoch.value) {
    if (!value || owner !== `${gameId}/${npcId}` || expectedEpoch !== contextEpoch.value) return false
    const request = ++saveRequest, epoch = loadRequest, snapshot = cloneJson(value)
    saving.value = true
    try {
      let result
      try { result = await memoryApi.saveNpcPolicy(gameId, npcId, snapshot) }
      catch (error) { if (request === saveRequest && epoch === loadRequest && owner === `${gameId}/${npcId}`) throw error; return false }
      if (request === saveRequest && epoch === loadRequest && owner === `${gameId}/${npcId}`) { effective.value = result; return true }
      return false
    } finally { if (request === saveRequest && epoch === loadRequest) saving.value = false }
  }

  function invalidate() { loadRequest++; saveRequest++; previewRequest++; contextEpoch.value++; owner = ''; settings.value = null; effective.value = null; loading.value = false; saving.value = false }

  return { settings, effective, loading, saving, contextEpoch, load, preview, save, invalidate }
})
