<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'

const app = useAppStore()
const date = ref(new Date().toISOString().slice(0, 10))
const onlyCurrentNpc = ref(true)
const items = ref<Record<string, unknown>[]>([])
const total = ref(0)
const offset = ref(0)
const limit = 50
const loading = ref(false)
const npcId = computed(() => onlyCurrentNpc.value ? app.currentNpcId : '')

function text(value: unknown) { return value == null ? '' : String(value) }
function number(value: unknown) { return typeof value === 'number' ? value : Number(value || 0) }
function tools(value: unknown) { return Array.isArray(value) ? value.map(text).join(', ') || '-' : text(value) || '-' }
function preview(value: unknown, max = 60) { const s = text(value); return s.length > max ? `${s.slice(0, max)}…` : s }
function elapsed(value: unknown) { return `${(number(value) / 1000).toFixed(1)}s` }

async function load(pageDelta = 0) {
  offset.value = pageDelta === 0 ? 0 : Math.max(0, offset.value + pageDelta * limit)
  loading.value = true
  try {
    const result = await debugApi.logs(app.gameId, date.value, npcId.value, limit, offset.value)
    items.value = result.items || []; total.value = result.total || 0
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '日志加载失败') }
  finally { loading.value = false }
}
function pretty(item: Record<string, unknown>) { return JSON.stringify(item, null, 2) }
watch(() => [app.gameId, app.selectedNpcId], () => load())
onMounted(() => load())
</script>

<template>
  <PageHeader title="请求日志" description="按日期和 NPC 查看对话请求、兜底、注入标记、Token 用量与耗时。">
    <el-date-picker v-model="date" type="date" value-format="YYYY-MM-DD" placeholder="日期" @change="() => load()" />
    <el-checkbox v-model="onlyCurrentNpc" @change="() => load()">仅当前 NPC</el-checkbox>
    <el-button :loading="loading" type="primary" @click="() => load()">查询日志</el-button>
  </PageHeader>
  <div class="panel">
    <div class="panel-head"><h3>{{ date }} 日志</h3><span class="panel-note">共 {{ total }} 条 · {{ total ? `${offset + 1}-${Math.min(offset + limit, total)}` : '0' }}</span></div>
    <el-table v-loading="loading" :data="items" stripe>
      <el-table-column type="expand"><template #default="scope"><pre class="code-block log-detail">{{ pretty(scope.row) }}</pre></template></el-table-column>
      <el-table-column label="时间" width="170"><template #default="scope">{{ text(scope.row.ts || scope.row.timestamp).replace('T', ' ').slice(0, 19) }}</template></el-table-column>
      <el-table-column prop="npcId" label="NPC" width="150" />
      <el-table-column label="玩家说" min-width="190"><template #default="scope">{{ preview(scope.row.userMessage) }}</template></el-table-column>
      <el-table-column label="回复" min-width="210"><template #default="scope"><span>{{ preview(scope.row.say) }}</span><el-tag v-if="scope.row.fallback" size="small" type="warning" class="flag">兜底</el-tag><el-tag v-if="scope.row.injection" size="small" type="danger" class="flag">注入</el-tag></template></el-table-column>
      <el-table-column label="Tokens" width="115"><template #default="scope">{{ number(scope.row.promptTokens) }} / {{ number(scope.row.completionTokens) }}</template></el-table-column>
      <el-table-column label="耗时" width="85"><template #default="scope">{{ elapsed(scope.row.elapsedMs) }}</template></el-table-column>
      <el-table-column label="工具" min-width="120"><template #default="scope">{{ tools(scope.row.tools) }}</template></el-table-column>
      <template #empty><div class="empty-state">暂无日志记录</div></template>
    </el-table>
    <div class="pager"><el-button :disabled="offset === 0 || loading" @click="load(-1)">上一页</el-button><el-button :disabled="offset + limit >= total || loading" @click="load(1)">下一页</el-button></div>
  </div>
</template>

<style scoped>
.panel-note { color: #8994a7; font-size: 12px; }
.flag { margin-left: 6px; }
.pager { display: flex; justify-content: flex-end; gap: 8px; padding: 14px 18px; border-top: 1px solid #edf0f5; }
.log-detail { max-height: 300px; margin: 8px 0; }
</style>
