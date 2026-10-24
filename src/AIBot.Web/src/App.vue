<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { memoryApi } from '@/api/memory'
import { useAppStore } from '@/stores/app'

import { confirmDiscard } from '@/utils/unsaved'
const app = useAppStore()
const connectionDialog = ref(false)
const connectionDraft = reactive({ serverBase: '', adminToken: '', auditActor: '' })
function openConnection() { Object.assign(connectionDraft, { serverBase: app.serverBase, adminToken: app.adminToken, auditActor: app.auditActor }); connectionDialog.value = true }
async function applyConnection() {
  try { const url = new URL(connectionDraft.serverBase.trim() || location.origin); if (!['http:', 'https:'].includes(url.protocol)) throw new Error(); connectionDraft.serverBase = connectionDraft.serverBase.trim().replace(/\/+$/, '') } catch { ElMessage.error('请填写有效的 HTTP/HTTPS Server 地址'); return }
  if (!await confirmDiscard()) return
  Object.assign(app, connectionDraft); connectionDialog.value = false
  await Promise.all([refreshNpcs(), app.loadStorage()])
}
async function selectGame(value: string) { if (value !== app.gameId && await confirmDiscard()) app.gameId = value }
async function selectNpc(value: string) { if (value !== app.selectedNpcId && await confirmDiscard()) app.selectedNpcId = value }

const route = useRoute()
const router = useRouter()
const npcMemoryRoute = computed(() => `/npc/${encodeURIComponent(app.currentNpcId || 'none')}/memory`)
const navGroups = [
  { id: 'debug', title: '调试', home: '/debug/chat', items: [['对话', '/debug/chat'], ['Prompt', '/debug/prompt'], ['会话', '/debug/sessions']] },
  { id: 'content', title: '内容', home: '/debug/npc', items: [['NPC 配置', '/debug/npc'], ['世界观', '/debug/world']] },
  { id: 'memory', title: '记忆', home: '/memories', items: [['记忆检查', '/memories'], ['Game 策略', '/game/memory-policy'], ['NPC 覆盖', npcMemoryRoute], ['系统边界', '/settings/memory'], ['审计', '/memory-audit']] },
  { id: 'models', title: '模型', home: '/settings/llm', items: [['连接管理', '/settings/llm']] },
  { id: 'ops', title: '运维', home: '/debug/logs', items: [['日志', '/debug/logs'], ['统计', '/debug/stats'], ['旧记忆迁移', '/memory-migrations']] },
]
const activeGroup = computed(() => navGroups.find(group => group.items.some(([, path]) => route.path === (typeof path === 'string' ? path : path.value))) || navGroups[0])

async function refreshNpcs() {
  try { await Promise.all([app.loadNpcs(), app.loadGames(), app.loadReadiness()]) } catch (error) { ElMessage.error(error instanceof Error ? error.message : 'NPC 列表加载失败') }
}

async function createGame() {
  let gameId = ''
  try {
    const { value } = await ElMessageBox.prompt(
      '将创建 world.json 与 memory-policy.json 骨架；NPC 之后在「NPC 配置」页添加。',
      '新建 Game',
      {
        inputPattern: /^[a-zA-Z0-9_.:-]{1,64}$/,
        inputErrorMessage: 'Game ID 仅允许字母数字与 _ . : -（1~64 位）',
        confirmButtonText: '创建',
        cancelButtonText: '取消',
      },
    )
    gameId = value.trim()
    await memoryApi.createGame(gameId)
    ElMessage.success(`Game「${gameId}」已创建`)
    if (!await confirmDiscard()) return
    app.gameId = gameId   // 触发 NPC 列表与 Game 列表刷新
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : 'Game 创建失败')
  }
}

watch(() => [app.serverBase, app.gameId], async () => {
  await refreshNpcs()
  if (route.path.startsWith('/npc/')) await router.replace(npcMemoryRoute.value)
})

watch(() => app.selectedNpcId, async () => {
  if (route.path.startsWith('/npc/') && app.currentNpcId) await router.replace(npcMemoryRoute.value)
})

onMounted(() => { app.loadStorage(); refreshNpcs() })
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-mark">AI</div>
        <div><strong>NPC Memory</strong><span>运营控制台</span></div>
      </div>
      <nav class="nav-list">
        <RouterLink v-for="group in navGroups" :key="group.id" :to="group.home" :class="{ 'router-link-active': activeGroup.id === group.id }">{{ group.title }}</RouterLink>
      </nav>
      <div class="sidebar-foot">
        <span class="status-dot" :class="`is-${app.health}`" :title="app.healthLabel"></span>
        <div>
          <strong>AIBot.Server</strong>
          <small>管理 API · v0.3<template v-if="app.storageLabel"> · {{ app.storageLabel }}</template></small>
          <small class="health-line">{{ app.healthLabel }}</small>
        </div>
      </div>
    </aside>

    <main class="main-shell">
      <header class="topbar">
        <div class="topbar-brand">
          <span class="eyebrow">AIBot.Server</span>
          <strong>统一管理台</strong>
        </div>
        <div class="context-bar"><el-button @click="openConnection">连接设置</el-button>
          <label>Game</label>
          <el-select :model-value="app.gameId" @change="selectGame" class="compact-input" filterable allow-create default-first-option title="选择或输入 Game ID">
            <el-option v-for="id in app.gameIds" :key="id" :label="id" :value="id" />
          </el-select>
          <el-button circle title="新建 Game" @click="createGame"><el-icon><Plus /></el-icon></el-button>
          <label>NPC</label>
          <el-select :model-value="app.selectedNpcId" @change="selectNpc" class="npc-select" :loading="app.loadingNpcs">
            <el-option v-for="id in app.npcIds" :key="id" :label="id" :value="id" />
          </el-select>
          <el-button circle title="刷新 NPC" @click="refreshNpcs"><el-icon><Refresh /></el-icon></el-button>
        </div>
      </header>
      <!-- 单项分组（如“模型”）不再重复渲染子导航 -->
      <nav v-if="activeGroup.items.length > 1" class="section-tabs" aria-label="当前入口">
        <RouterLink v-for="([label, path], index) in activeGroup.items" :key="index" :to="typeof path === 'string' ? path : path.value">{{ label }}</RouterLink>
      </nav>
      <section class="workspace"><RouterView :key="app.connectionEpoch" /></section>
    </main>
    <el-dialog v-model="connectionDialog" title="连接设置" width="min(540px, 94vw)">
      <el-form label-position="top"><el-form-item label="Server Base URL"><el-input v-model="connectionDraft.serverBase" placeholder="留空使用当前站点" /></el-form-item><el-form-item label="管理 Bearer Token"><el-input v-model="connectionDraft.adminToken" type="password" show-password /></el-form-item><el-form-item label="审计操作人"><el-input v-model="connectionDraft.auditActor" maxlength="80" /></el-form-item></el-form>
      <p>连接信息保存在当前浏览器。遇到 401/403 时请核对管理 Token。</p>
      <template #footer><el-button @click="connectionDialog = false">取消</el-button><el-button type="primary" @click="applyConnection">应用连接</el-button></template>
    </el-dialog>
  </div>
</template>

