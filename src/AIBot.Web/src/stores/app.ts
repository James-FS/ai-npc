import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { memoryApi } from '@/api/memory'

export const useAppStore = defineStore('app', () => {
  const serverBase = ref(localStorage.getItem('aibot.web.serverBase') || '')
  const adminToken = ref(localStorage.getItem('aibot.web.adminToken') || '')
  const auditActor = ref(localStorage.getItem('aibot.web.auditActor') || 'admin')
  const gameId = ref(localStorage.getItem('aibot.web.gameId') || 'default')
  const npcIds = ref<string[]>([])
  const selectedNpcId = ref(localStorage.getItem('aibot.web.npcId') || '')
  const loadingNpcs = ref(false)

  const currentNpcId = computed(() => selectedNpcId.value || npcIds.value[0] || '')

  watch(serverBase, value => localStorage.setItem('aibot.web.serverBase', value))
  watch(adminToken, value => localStorage.setItem('aibot.web.adminToken', value))
  watch(auditActor, value => localStorage.setItem('aibot.web.auditActor', value))
  watch(gameId, value => localStorage.setItem('aibot.web.gameId', value))
  watch(selectedNpcId, value => localStorage.setItem('aibot.web.npcId', value))

  async function loadNpcs() {
    loadingNpcs.value = true
    try {
      const result = await memoryApi.npcIds(gameId.value)
      npcIds.value = result.npcs
      if (!npcIds.value.includes(selectedNpcId.value)) selectedNpcId.value = npcIds.value[0] || ''
    } finally {
      loadingNpcs.value = false
    }
  }

  return {
    serverBase, adminToken, auditActor, gameId, npcIds, selectedNpcId,
    currentNpcId, loadingNpcs, loadNpcs,
  }
})
