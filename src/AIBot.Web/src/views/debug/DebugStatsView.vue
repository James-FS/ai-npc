<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'
import type { DebugStats } from '@/types/debug'

const app = useAppStore()
const stats = ref<DebugStats | null>(null)
const loading = ref(false)
const raw = computed<Record<string, unknown>>(() => stats.value || {})
function value(...keys: string[]) { for (const key of keys) if (raw.value[key] != null) return raw.value[key] as number; return 0 }
function seconds(ms: unknown) { return `${(Number(ms || 0) / 1000).toFixed(1)}s` }
function byNpc() {
  const source = raw.value.byNpc
  if (Array.isArray(source)) return source as Record<string, unknown>[]
  if (source && typeof source === 'object') return Object.entries(source as Record<string, Record<string, unknown>>).map(([npcId, data]) => ({ npcId, ...data }))
  return []
}
async function load() {
  loading.value = true
  try { stats.value = await debugApi.stats(app.gameId) }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '统计加载失败') }
  finally { loading.value = false }
}
watch(() => app.gameId, load)
onMounted(load)
</script>

<template>
  <PageHeader title="用量统计" description="查看对话请求、兜底、注入、Token 与响应耗时，定位模型和 NPC 的运行情况。"><el-button type="primary" :loading="loading" @click="load">刷新统计</el-button></PageHeader>
  <div v-loading="loading">
    <div class="metric-grid stats-metrics">
      <div class="metric-card"><div class="metric-label">总请求</div><div class="metric-value">{{ value('totalRequests') }}</div></div>
      <div class="metric-card"><div class="metric-label">兜底次数</div><div class="metric-value">{{ value('totalFallbacks', 'fallbackRequests') }}</div></div>
      <div class="metric-card"><div class="metric-label">输入 Tokens</div><div class="metric-value">{{ value('totalPromptTokens', 'promptTokens') }}</div></div>
      <div class="metric-card"><div class="metric-label">输出 Tokens</div><div class="metric-value">{{ value('totalCompletionTokens', 'completionTokens') }}</div></div>
      <div class="metric-card"><div class="metric-label">平均耗时</div><div class="metric-value">{{ seconds(value('avgMs', 'averageElapsedMs')) }}</div></div>
      <div class="metric-card"><div class="metric-label">注入尝试</div><div class="metric-value">{{ value('injectionAttempts') }}</div></div>
    </div>
    <div class="panel section-gap"><div class="panel-head"><h3>按 NPC 统计</h3></div><el-table :data="byNpc()" stripe><el-table-column prop="npcId" label="NPC" /><el-table-column prop="requests" label="请求" /><el-table-column prop="fallbacks" label="兜底" /><el-table-column prop="promptTokens" label="输入 tokens" /><el-table-column prop="completionTokens" label="输出 tokens" /><el-table-column label="平均耗时"><template #default="scope">{{ seconds(scope.row.avgMs ?? scope.row.averageElapsedMs) }}</template></el-table-column><template #empty><div class="empty-state">暂无 NPC 统计数据</div></template></el-table></div>
    <p v-if="raw.note" class="hint-box section-gap">{{ raw.note }}</p>
  </div>
</template>

<style scoped>
.stats-metrics { grid-template-columns: repeat(3, minmax(0, 1fr)); }
@media (max-width: 1280px) { .stats-metrics { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
</style>
