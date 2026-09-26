<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { memoryApi } from '@/api/memory'
import { useAppStore } from '@/stores/app'

const app = useAppStore()
const route = useRoute()
const router = useRouter()
const npcMemoryRoute = computed(() => `/npc/${encodeURIComponent(app.currentNpcId || 'none')}/memory`)

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
    app.gameId = gameId   // 触发 NPC 列表与 Game 列表刷新
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : 'Game 创建失败')
  }
}

watch(() => app.gameId, async () => {
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
        <div class="nav-divider">记忆治理</div>
        <RouterLink to="/settings/memory">系统边界</RouterLink>
        <RouterLink to="/game/memory-policy">Game 策略</RouterLink>
        <RouterLink :to="npcMemoryRoute">NPC 覆盖</RouterLink>
        <RouterLink to="/memories">记忆检查器</RouterLink>
        <RouterLink to="/memory-migrations">旧记忆迁移</RouterLink>
        <RouterLink to="/memory-audit">审计记录</RouterLink>
        <div class="nav-divider">调试工作台</div>
        <RouterLink to="/debug/chat">流式对话</RouterLink>
        <RouterLink to="/debug/npc">NPC 配置</RouterLink>
        <RouterLink to="/debug/world">世界观</RouterLink>
        <RouterLink to="/debug/prompt">Prompt 预览</RouterLink>
        <RouterLink to="/debug/sessions">会话调试</RouterLink>
        <RouterLink to="/debug/logs">请求日志</RouterLink>
        <RouterLink to="/debug/stats">用量统计</RouterLink>
        <div class="nav-divider">系统设置</div>
        <RouterLink to="/settings/llm">模型 Key 管理</RouterLink>
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
        <div class="context-bar">
          <label>Game</label>
          <el-select v-model="app.gameId" class="compact-input" filterable allow-create default-first-option title="选择或输入 Game ID">
            <el-option v-for="id in app.gameIds" :key="id" :label="id" :value="id" />
          </el-select>
          <el-button circle title="新建 Game" @click="createGame"><el-icon><Plus /></el-icon></el-button>
          <label>NPC</label>
          <el-select v-model="app.selectedNpcId" class="npc-select" :loading="app.loadingNpcs">
            <el-option v-for="id in app.npcIds" :key="id" :label="id" :value="id" />
          </el-select>
          <el-button circle title="刷新 NPC" @click="refreshNpcs"><el-icon><Refresh /></el-icon></el-button>
        </div>
      </header>
      <section class="workspace"><RouterView /></section>
    </main>
  </div>
</template>

