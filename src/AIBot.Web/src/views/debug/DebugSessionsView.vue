<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'
import type { DebugSession, DebugSessionDetail } from '@/types/debug'

const app = useAppStore()
let epoch = 0, loadRequest = 0, detailRequest = 0
onBeforeUnmount(() => { epoch++; loadRequest++; detailRequest++ })
const playerId = ref(localStorage.getItem('aibot.debug.playerId') || 'player-local')
const sessions = ref<DebugSession[]>([])
const detail = ref<DebugSessionDetail | null>(null)
const loading = ref(false)
const npcId = computed(() => app.currentNpcId)

async function load() {
  if (!npcId.value) return
  const request = ++loadRequest, version = epoch
  loading.value = true
  try { const result = await debugApi.sessions(app.gameId, npcId.value, playerId.value); if (request === loadRequest && version === epoch) sessions.value = result.sessions }
  catch (error) { if (request === loadRequest && version === epoch) ElMessage.error(error instanceof Error ? error.message : '会话加载失败') }
  finally { if (request === loadRequest && version === epoch) loading.value = false }
}
async function open(sessionId: string) {
  if (!npcId.value) return
  const request = ++detailRequest, version = epoch
  detail.value = null
  try { const result = await debugApi.session(app.gameId, npcId.value, playerId.value, sessionId); if (request === detailRequest && version === epoch) detail.value = result }
  catch (error) { if (request === detailRequest && version === epoch) ElMessage.error(error instanceof Error ? error.message : '会话详情加载失败') }
}
function openRow(row: DebugSession) { void open(row.sessionId) }

// UTC ISO 转本地短格式，避免长串撑爆表格列
function formatTime(iso?: string) {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}
async function remove(session: DebugSession) {
  const version = epoch, game = app.gameId, player = playerId.value, target = { ...session }
  try { await ElMessageBox.confirm(`确认删除会话 ${session.sessionId}？这会删除短期消息和待摘要队列。`, '删除会话', { type: 'warning' }); if (version !== epoch) return; await debugApi.deleteSession(game, target.npcId, player, target.sessionId); if (version !== epoch) return; detail.value = null; await load(); ElMessage.success('会话已删除') }
  catch (error) { if (version === epoch && error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '会话删除失败') }
}
watch(() => [app.serverBase, app.gameId, app.currentNpcId, playerId.value], () => { epoch++; detailRequest++; detail.value = null; sessions.value = []; loading.value = false; void load() }, { flush: 'sync' })
onMounted(load)
</script>

<template>
  <PageHeader title="会话与记忆调试" description="查看会话消息、待摘要数量、旧兼容字段和模拟状态。玩家长期记忆请使用记忆检查器。"><el-input v-model="playerId" class="player-input" placeholder="Player ID" @change="load" /><el-button :loading="loading" @click="load">刷新</el-button></PageHeader>
  <div class="debug-sessions">
    <div class="panel"><el-table v-loading="loading" :data="sessions" row-key="sessionId" @row-click="openRow"><el-table-column prop="sessionId" label="Session ID" min-width="140" show-overflow-tooltip /><el-table-column prop="messageCount" label="消息" width="70" /><el-table-column prop="pendingSummaryMessages" label="待摘要" width="70" /><el-table-column label="摘要" width="70"><template #default="scope"><el-tag size="small" :type="scope.row.hasSummary ? 'success' : 'info'">{{ scope.row.hasSummary ? '有' : '无' }}</el-tag></template></el-table-column><el-table-column prop="lastActiveUtc" label="最后活跃" min-width="150"><template #default="scope"><span class="time-cell" :title="scope.row.lastActiveUtc">{{ formatTime(scope.row.lastActiveUtc) }}</span></template></el-table-column><el-table-column label="操作" width="70"><template #default="scope"><el-button link type="danger" @click.stop="remove(scope.row as DebugSession)">删除</el-button></template></el-table-column><template #empty><div class="empty-state">暂无会话</div></template></el-table></div>
    <div class="panel detail-panel" v-if="detail"><div class="panel-head"><h3>{{ detail.sessionId }} <small>{{ detail.playerId }}</small></h3><el-button link @click="detail = null">关闭</el-button></div><div class="panel-body"><div v-for="(item, index) in detail.messages" :key="index" class="message-line"><el-tag size="small" effect="plain">{{ item.role }}</el-tag><span>{{ item.content }}</span></div><div class="hint-box section-gap">待摘要消息：{{ detail.pendingSummaryMessages }}<br>兼容摘要：{{ detail.summary || '无' }}<br>兼容事实：{{ detail.facts?.join('；') || '无' }}</div></div></div>
    <div v-else class="panel empty-state detail-panel">点击左侧会话查看详情</div>
  </div>
</template>

<style scoped>
.player-input { width: 220px; margin-right: 8px; }
.debug-sessions { display: grid; grid-template-columns: minmax(0, 1.25fr) minmax(300px, .75fr); gap: 20px; }
.detail-panel { min-height: 300px; }
.time-cell { white-space: nowrap; }
.panel-head small { color: var(--graphite); font-weight: normal; margin-left: 8px; }
.message-line { display: flex; gap: 10px; align-items: flex-start; padding: 10px 0; border-bottom: 1px solid #edf0f5; white-space: pre-wrap; line-height: 1.55; }
@media (max-width: 1200px) { .debug-sessions { grid-template-columns: 1fr; } }
</style>

