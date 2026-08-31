<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'

const app = useAppStore()
type LogMode = 'chat' | 'runtime'
const mode = ref<LogMode>('chat')
const date = ref(new Date().toISOString().slice(0, 10))
const onlyCurrentNpc = ref(true)
const runtimeLevel = ref('')
const runtimeCategory = ref('')
const runtimeRequestId = ref('')
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
// 存储为 UTC ISO；表格显示本地时间，原始值可在展开行查看
function formatTime(value: unknown) {
  const s = text(value)
  if (!s) return ''
  const d = new Date(s)
  if (Number.isNaN(d.getTime())) return s.replace('T', ' ').slice(0, 19)
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

async function load(pageDelta = 0) {
  offset.value = pageDelta === 0 ? 0 : Math.max(0, offset.value + pageDelta * limit)
  loading.value = true
  try {
    const result = mode.value === 'chat'
      ? await debugApi.logs(app.gameId, date.value, npcId.value, limit, offset.value)
      : await debugApi.runtimeLogs({ date: date.value, level: runtimeLevel.value,
        category: runtimeCategory.value, requestId: runtimeRequestId.value,
        limit, offset: offset.value })
    items.value = result.items || []; total.value = result.total || 0
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '日志加载失败') }
  finally { loading.value = false }
}
function pretty(item: Record<string, unknown>) { return JSON.stringify(item, null, 2) }
watch(() => [app.gameId, app.selectedNpcId, mode.value], () => load())
onMounted(() => load())
</script>

<template>
  <PageHeader :title="mode === 'chat' ? '请求日志' : 'Server 运行日志'" :description="mode === 'chat' ? '按日期和 NPC 查看对话请求、兜底、注入标记、Token 用量与耗时。' : '查看 Server 运行事件、错误级别和 requestId；日志内容已默认脱敏。'">
    <el-radio-group v-model="mode" size="small">
      <el-radio-button label="chat">对话日志</el-radio-button>
      <el-radio-button label="runtime">运行日志</el-radio-button>
    </el-radio-group>
    <el-date-picker v-model="date" type="date" value-format="YYYY-MM-DD" placeholder="日期" @change="() => load()" />
    <template v-if="mode === 'chat'"><el-checkbox v-model="onlyCurrentNpc" @change="() => load()">仅当前 NPC</el-checkbox></template>
    <template v-else>
      <el-select v-model="runtimeLevel" clearable placeholder="级别" class="runtime-filter" @change="() => load()"><el-option label="Error" value="Error" /><el-option label="Warning" value="Warning" /><el-option label="Info" value="Info" /><el-option label="Debug" value="Debug" /></el-select>
      <el-input v-model="runtimeCategory" clearable placeholder="类别" class="runtime-filter" @keyup.enter="() => load()" />
      <el-input v-model="runtimeRequestId" clearable placeholder="requestId" class="runtime-filter request-filter" @keyup.enter="() => load()" />
    </template>
    <el-button :loading="loading" type="primary" @click="() => load()">查询日志</el-button>
  </PageHeader>
  <div class="panel">
    <div class="panel-head"><h3>{{ date }} 日志</h3><span class="panel-note">共 {{ total }} 条 · {{ total ? `${offset + 1}-${Math.min(offset + limit, total)}` : '0' }}</span></div>
    <el-table v-loading="loading" :data="items" stripe>
      <el-table-column type="expand"><template #default="scope"><pre class="code-block log-detail">{{ pretty(scope.row) }}</pre></template></el-table-column>
      <template v-if="mode === 'chat'">
        <el-table-column label="时间" width="170"><template #default="scope">{{ formatTime(scope.row.ts || scope.row.timestamp) }}</template></el-table-column>
        <el-table-column prop="npcId" label="NPC" width="150" />
        <el-table-column label="玩家说" min-width="190"><template #default="scope">{{ preview(scope.row.userMessage) }}</template></el-table-column>
        <el-table-column label="回复" min-width="210"><template #default="scope"><span>{{ preview(scope.row.say) }}</span><el-tag v-if="scope.row.fallback" size="small" type="warning" class="flag">兜底</el-tag><el-tag v-if="scope.row.injection" size="small" type="danger" class="flag">注入</el-tag></template></el-table-column>
        <el-table-column label="Tokens" width="115"><template #default="scope">{{ number(scope.row.promptTokens) }} / {{ number(scope.row.completionTokens) }}</template></el-table-column>
        <el-table-column label="耗时" width="85"><template #default="scope">{{ elapsed(scope.row.elapsedMs) }}</template></el-table-column>
        <el-table-column label="工具" min-width="120"><template #default="scope">{{ tools(scope.row.tools) }}</template></el-table-column>
      </template>
      <template v-else>
        <el-table-column label="时间" width="180"><template #default="scope">{{ formatTime(scope.row.tsUtc) }}</template></el-table-column>
        <el-table-column prop="level" label="级别" width="90" />
        <el-table-column prop="category" label="类别" width="145" />
        <el-table-column prop="event" label="事件" width="180" />
        <el-table-column label="消息" min-width="280"><template #default="scope">{{ preview(scope.row.message, 120) }}</template></el-table-column>
        <el-table-column prop="status" label="状态" width="75" />
        <el-table-column prop="requestId" label="requestId" min-width="180" />
        <el-table-column label="耗时" width="90"><template #default="scope">{{ scope.row.durationMs == null ? '-' : `${number(scope.row.durationMs)}ms` }}</template></el-table-column>
      </template>
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
.runtime-filter { width: 120px; }
.request-filter { width: 180px; }
</style>

