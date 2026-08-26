import { defineStore } from 'pinia'
import { ref } from 'vue'
import { memoryApi } from '@/api/memory'
import type { EffectiveMemoryPolicy, MemorySettings } from '@/types/memory'

export const useNpcMemoryPolicyStore = defineStore('npc-memory-policy', () => {
  const settings = ref<MemorySettings | null>(null)
  const effective = ref<EffectiveMemoryPolicy | null>(null)
  const loading = ref(false)
  const saving = ref(false)

  async function load(gameId: string, npcId: string) {
    if (!npcId) return
    loading.value = true
    try {
      const result = await memoryApi.npcPolicy(gameId, npcId)
      settings.value = structuredClone(result.npc)
      effective.value = result.effective
    } finally { loading.value = false }
  }

  async function preview(gameId: string, npcId: string) {
    if (!settings.value) return
    effective.value = await memoryApi.previewNpcPolicy(gameId, npcId, settings.value)
  }

  async function save(gameId: string, npcId: string) {
    if (!settings.value) return
    saving.value = true
    try {
      effective.value = await memoryApi.saveNpcPolicy(gameId, npcId, settings.value)
    } finally { saving.value = false }
  }

  return { settings, effective, loading, saving, load, preview, save }
})
