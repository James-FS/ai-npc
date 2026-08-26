<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useAppStore } from '@/stores/app'

const app = useAppStore()
const route = useRoute()
const router = useRouter()
const npcMemoryRoute = computed(() => `/npc/${encodeURIComponent(app.currentNpcId || 'none')}/memory`)

async function refreshNpcs() {
  try { await app.loadNpcs() } catch (error) { ElMessage.error(error instanceof Error ? error.message : 'NPC 列表加载失败') }
}

watch(() => app.gameId, async () => {
  await refreshNpcs()
  if (route.path.startsWith('/npc/')) await router.replace(npcMemoryRoute.value)
})

watch(() => app.selectedNpcId, async () => {
  if (route.path.startsWith('/npc/') && app.currentNpcId) await router.replace(npcMemoryRoute.value)
})

onMounted(refreshNpcs)
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-mark">AI</div>
        <div><strong>NPC Memory</strong><span>运营控制台</span></div>
      </div>
      <nav class="nav-list">
        <RouterLink to="/settings/memory"><span>01</span>系统边界</RouterLink>
        <RouterLink to="/game/memory-policy"><span>02</span>Game 策略</RouterLink>
        <RouterLink :to="npcMemoryRoute"><span>03</span>NPC 覆盖</RouterLink>
        <RouterLink to="/memories"><span>04</span>记忆检查器</RouterLink>
        <RouterLink to="/memory-migrations"><span>05</span>旧记忆迁移</RouterLink>
        <RouterLink to="/memory-audit"><span>06</span>审计记录</RouterLink>
        <div class="nav-divider">调试工作台</div>
        <RouterLink to="/debug/chat"><span>07</span>流式对话</RouterLink>
        <RouterLink to="/debug/npc"><span>08</span>NPC 配置</RouterLink>
        <RouterLink to="/debug/world"><span>09</span>世界观</RouterLink>
        <RouterLink to="/debug/prompt"><span>10</span>Prompt 预览</RouterLink>
        <RouterLink to="/debug/sessions"><span>11</span>会话调试</RouterLink>
        <RouterLink to="/debug/logs"><span>12</span>请求日志</RouterLink>
        <RouterLink to="/debug/stats"><span>13</span>用量统计</RouterLink>
      </nav>
      <div class="sidebar-foot">
        <span class="status-dot"></span>
        <div><strong>AIBot.Server</strong><small>管理 API · v0.3</small></div>
      </div>
    </aside>

    <main class="main-shell">
      <header class="topbar">
        <div>
          <div class="eyebrow">AIBot.Server · {{ route.meta.title }}</div>
          <h1>统一管理台</h1>
        </div>
        <div class="context-bar">
          <label>Game</label>
          <el-input v-model="app.gameId" class="compact-input" />
          <label>NPC</label>
          <el-select v-model="app.selectedNpcId" class="npc-select" :loading="app.loadingNpcs">
            <el-option v-for="id in app.npcIds" :key="id" :label="id" :value="id" />
          </el-select>
          <el-button circle title="刷新 NPC" @click="refreshNpcs">↻</el-button>
        </div>
      </header>
      <section class="workspace"><RouterView /></section>
    </main>
  </div>
</template>
