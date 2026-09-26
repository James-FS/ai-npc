import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { memoryApi, type Readiness } from '@/api/memory'
import type { StorageInfo } from '@/types/memory'

export const useAppStore = defineStore('app', () => {
  const serverBase = ref(localStorage.getItem('aibot.web.serverBase') || '')
  const adminToken = ref(localStorage.getItem('aibot.web.adminToken') || '')
  const auditActor = ref(localStorage.getItem('aibot.web.auditActor') || 'admin')
  const gameId = ref(localStorage.getItem('aibot.web.gameId') || 'default')
  const gameIds = ref<string[]>([gameId.value])
  const npcIds = ref<string[]>([])
  const selectedNpcId = ref(localStorage.getItem('aibot.web.npcId') || '')
  const loadingNpcs = ref(false)
  const storageInfo = ref<StorageInfo | null>(null)
  /// null = 尚未探测；探测失败或未就绪都为 false，避免状态灯谎报健康。
  const readiness = ref<Readiness | null>(null)
  const readinessReachable = ref<boolean | null>(null)

  const currentNpcId = computed(() => selectedNpcId.value || npcIds.value[0] || '')
  const storageLabel = computed(() => {
    const s = storageInfo.value
    if (!s) return ''
    const started = s.startedAt ? ` · 启动 ${s.startedAt.slice(11, 16)}` : ''
    return `存储 ${s.provider}${started}`
  })
  /// 三段式健康：未知（未探测）/ 就绪 / 异常，供状态灯使用。
  const health = computed<'unknown' | 'ready' | 'down'>(() => {
    if (readinessReachable.value === null) return 'unknown'
    if (readinessReachable.value === false) return 'down'
    return readiness.value?.ready ? 'ready' : 'down'
  })
  const healthLabel = computed(() => {
    if (health.value === 'unknown') return '正在探测'
    if (health.value === 'ready') return '就绪'
    const failed = readiness.value?.checks
      ? Object.entries(readiness.value.checks).filter(([, check]) => check && check.ok === false).map(([key]) => key)
      : []
    return failed.length ? `异常：${failed.join(' / ')}` : '不可用'
  })

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

  /// <summary>加载可选 Game 列表；接口不可用（旧版 Server）时静默保留当前值。</summary>
  async function loadGames() {
    try {
      const result = await memoryApi.games()
      const merged = new Set(result.games)
      merged.add(gameId.value)
      gameIds.value = [...merged]
    } catch { /* 旧版 Server 无 /api/games：下拉仅显示当前 Game，仍可手动输入 */ }
  }

  /// <summary>加载存储模式展示信息；接口不可用（旧版 Server）时静默留空。</summary>
  async function loadStorage() {
    try { storageInfo.value = await memoryApi.storage() } catch { /* 旧版 Server 无此接口 */ }
  }

  /// <summary>探测 /api/ready；未就绪属正常结果，由 api 层取回响应体。</summary>
  async function loadReadiness() {
    const { reachable, data } = await memoryApi.readiness()
    readiness.value = data
    readinessReachable.value = reachable
  }

  return {
    serverBase, adminToken, auditActor, gameId, gameIds, npcIds, selectedNpcId,
    currentNpcId, loadingNpcs, loadNpcs, loadGames, storageInfo, storageLabel, loadStorage,
    readiness, readinessReachable, health, healthLabel, loadReadiness,
  }
})

