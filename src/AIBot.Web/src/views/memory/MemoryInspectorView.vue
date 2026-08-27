<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { ApiError } from '@/api/http'
import { memoryApi } from '@/api/memory'
import { useAppStore } from '@/stores/app'
import { useMemoryInspectorStore } from '@/stores/memoryInspector'
import type { MemoryFact, MemoryListItem } from '@/types/memory'

const app = useAppStore()
const store = useMemoryInspectorStore()
const filter = reactive({ npcId: '', playerId: '' })
const page = ref(1)
const pageSize = ref(20)
const drawerOpen = ref(false)
const summaryDraft = ref('')
const factDialogOpen = ref(false)
const editingFactId = ref('')
const factForm = reactive<Partial<MemoryFact>>({ category: 'general', key: '', value: '', confidence: 0.8, pinned: false, sourceSessionId: null, expiresUtc: null })

const detailTitle = computed(() => store.detail ? `${store.detail.npcId} × ${store.detail.playerId}` : '长期记忆')

function formatDate(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—'
}

async function search(reset = false) {
  if (reset) page.value = 1
  try {
    await store.search(app.gameId, { ...filter, limit: pageSize.value, offset: (page.value - 1) * pageSize.value })
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '记忆列表加载失败') }
}

async function refreshQueue() {
  try { await store.loadQueue() }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '摘要队列状态加载失败') }
}

async function retryFailedSummaries() {
  try {
    const result = await memoryApi.retrySummaryQueue()
    ElMessage.success(result.retried > 0 ? `已重新排队 ${result.retried} 个失败任务` : '当前没有可重试的失败任务')
    await refreshQueue()
    if (store.detail) await openDetail(store.detail.npcId, store.detail.playerId)
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '摘要重试失败') }
}

async function openDetail(npcId: string, playerId: string) {
  try {
    await store.open(app.gameId, npcId, playerId)
    summaryDraft.value = store.detail?.summary || ''
    drawerOpen.value = true
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '记忆详情加载失败') }
}

async function reloadAfterConflict(error: unknown) {
  if (!(error instanceof ApiError) || error.status !== 409 || !store.detail) return false
  ElMessage.warning('记忆版本已变化，已刷新为最新内容，请确认后重试')
  await openDetail(store.detail.npcId, store.detail.playerId)
  return true
}

async function saveSummary() {
  try {
    await store.saveSummary(app.gameId, summaryDraft.value)
    summaryDraft.value = store.detail?.summary || ''
    ElMessage.success('长期摘要已保存')
    await search()
  } catch (error) {
    if (!await reloadAfterConflict(error)) ElMessage.error(error instanceof Error ? error.message : '摘要保存失败')
  }
}

function newFact() {
  editingFactId.value = ''
  Object.assign(factForm, { id: undefined, category: 'general', key: '', value: '', confidence: 0.8, pinned: false, sourceSessionId: null, expiresUtc: null })
  factDialogOpen.value = true
}

function editFact(fact: MemoryFact) {
  editingFactId.value = fact.id
  Object.assign(factForm, structuredClone(fact))
  factDialogOpen.value = true
}

async function saveFact() {
  if (!factForm.value?.trim()) return ElMessage.warning('事实内容不能为空')
  try {
    await store.saveFact(app.gameId, structuredClone(factForm), editingFactId.value || undefined)
    factDialogOpen.value = false
    ElMessage.success(editingFactId.value ? '事实已更新' : '事实已新增')
    await search()
  } catch (error) {
    if (!await reloadAfterConflict(error)) ElMessage.error(error instanceof Error ? error.message : '事实保存失败')
  }
}

async function removeFact(fact: MemoryFact) {
  try {
    await ElMessageBox.confirm(`确定删除事实“${fact.value.slice(0, 40)}”吗？`, '删除事实', { type: 'warning' })
    await store.deleteFact(app.gameId, fact.id)
    ElMessage.success('事实已删除')
    await search()
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    if (!await reloadAfterConflict(error)) ElMessage.error(error instanceof Error ? error.message : '删除失败')
  }
}

async function togglePinned(fact: MemoryFact, pinned: boolean) {
  try {
    await store.saveFact(app.gameId, { ...structuredClone(fact), pinned }, fact.id)
    ElMessage.success(pinned ? '事实已固定' : '事实已取消固定')
  } catch (error) {
    if (!await reloadAfterConflict(error)) ElMessage.error(error instanceof Error ? error.message : '固定状态更新失败')
  }
}

async function summarize(sessionId: string) {
  if (!store.detail) return
  try {
    const result = await memoryApi.summarize(app.gameId, store.detail.npcId, store.detail.playerId, sessionId)
    ElMessage.success(`摘要任务已排队，待处理 ${result.pendingMessages} 条消息`)
    await openDetail(store.detail.npcId, store.detail.playerId)
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '摘要任务提交失败') }
}

async function exportMemory() {
  if (!store.detail) return
  try { await memoryApi.exportMemory(app.gameId, store.detail.npcId, store.detail.playerId) }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '导出失败') }
}

async function deleteMemory() {
  if (!store.detail) return
  try {
    await ElMessageBox.confirm(`将永久清空 ${detailTitle.value} 的长期摘要、全部事实，以及该玩家与 NPC 的所有 Session 消息和待摘要队列。此操作会写入审计。`, '清空整份记忆', { type: 'error', confirmButtonText: '确认清空' })
    await memoryApi.deleteMemory(app.gameId, store.detail.npcId, store.detail.playerId, store.detail.memoryVersion)
    drawerOpen.value = false
    store.detail = null
    await search(true)
    ElMessage.success('长期记忆与关联 Session 已清空')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    if (!await reloadAfterConflict(error)) ElMessage.error(error instanceof Error ? error.message : '清空失败')
  }
}

watch(() => app.gameId, async () => { await search(true); await refreshQueue() })
onMounted(async () => { await search(true); await refreshQueue() })
</script>

<template>
  <PageHeader title="玩家记忆检查器" description="按 NPC 与 playerId 查看、纠正和删除长期摘要及结构化事实；所有人工写操作使用版本控制并记录审计。">
    <el-button :loading="store.loading" @click="search()">刷新</el-button>
    <el-button :loading="store.queueLoading" @click="refreshQueue">刷新队列</el-button>
    <el-button type="warning" plain :disabled="store.queue.failedCurrent < 1" @click="retryFailedSummaries">重试失败任务</el-button>
  </PageHeader>

  <div class="queue-strip">
    <span class="queue-label">摘要队列</span>
    <el-tag type="info" effect="plain">待处理 {{ store.queue.pending }}</el-tag>
    <el-tag :type="store.queue.failedCurrent > 0 ? 'danger' : 'success'" effect="plain">当前失败 {{ store.queue.failedCurrent }}</el-tag>
    <span class="queue-note">累计失败 {{ store.queue.failedTotal }}；失败不会删除待摘要消息</span>
  </div>

  <div class="panel">
    <div class="filter-bar">
      <el-select v-model="filter.npcId" clearable filterable placeholder="全部 NPC" style="width:220px"><el-option v-for="id in app.npcIds" :key="id" :label="id" :value="id" /></el-select>
      <el-input v-model="filter.playerId" clearable placeholder="playerId 精确筛选" style="width:240px" @keyup.enter="search(true)" />
      <el-button type="primary" @click="search(true)">查询</el-button><el-button @click="Object.assign(filter, { npcId: '', playerId: '' }); search(true)">重置</el-button>
      <span class="filter-count">共 {{ store.total }} 份长期记忆</span>
    </div>
    <el-table v-loading="store.loading" :data="store.items" @row-dblclick="(row: MemoryListItem) => openDetail(row.npcId, row.playerId)">
      <el-table-column prop="npcId" label="NPC" min-width="170" />
      <el-table-column prop="playerId" label="Player ID" min-width="190" />
      <el-table-column prop="memoryVersion" label="版本" width="80" align="center" />
      <el-table-column prop="factCount" label="事实" width="80" align="center" />
      <el-table-column label="摘要" width="90" align="center"><template #default="scope"><el-tag :type="scope.row.hasSummary ? 'success' : 'info'" effect="plain">{{ scope.row.hasSummary ? '已有' : '无' }}</el-tag></template></el-table-column>
      <el-table-column label="最近摘要" min-width="180"><template #default="scope">{{ formatDate(scope.row.lastSummarizedUtc) }}</template></el-table-column>
      <el-table-column label="更新时间" min-width="180"><template #default="scope">{{ formatDate(scope.row.updatedUtc) }}</template></el-table-column>
      <el-table-column label="操作" width="100" fixed="right"><template #default="scope"><el-button link type="primary" @click="openDetail(scope.row.npcId, scope.row.playerId)">查看详情</el-button></template></el-table-column>
      <template #empty><div class="empty-state">没有符合条件的长期记忆</div></template>
    </el-table>
    <div class="pagination"><el-pagination v-model:current-page="page" v-model:page-size="pageSize" layout="total, sizes, prev, pager, next" :page-sizes="[20, 50, 100]" :total="store.total" @change="search()" /></div>
  </div>

  <el-drawer v-model="drawerOpen" size="78%" destroy-on-close>
    <template #header><div class="drawer-title"><strong>{{ detailTitle }}</strong><small>memoryVersion {{ store.detail?.memoryVersion }}</small></div></template>
    <div v-if="store.detail" class="detail-stack" v-loading="store.loading">
      <div class="detail-toolbar"><el-tag effect="plain">{{ store.detail.gameId }}</el-tag><span>最近摘要：{{ formatDate(store.detail.lastSummarizedUtc) }}</span><span class="toolbar-spacer"></span><el-button @click="exportMemory">导出 JSON</el-button><el-button type="danger" plain @click="deleteMemory">清空记忆</el-button></div>

      <div class="panel"><div class="panel-head"><h3>长期摘要</h3><el-button type="primary" :disabled="summaryDraft === (store.detail.summary || '')" @click="saveSummary">保存摘要</el-button></div><div class="panel-body"><el-input v-model="summaryDraft" type="textarea" :rows="7" maxlength="4000" show-word-limit placeholder="该玩家与 NPC 的滚动关系摘要" /></div></div>

      <div class="panel"><div class="panel-head"><h3>结构化事实（{{ store.detail.facts.length }}）</h3><el-button type="primary" @click="newFact">新增事实</el-button></div>
        <el-table :data="store.detail.facts">
          <el-table-column label="固定" width="70" align="center"><template #default="scope"><el-switch :model-value="scope.row.pinned" @change="togglePinned(scope.row as MemoryFact, Boolean($event))" /></template></el-table-column>
          <el-table-column prop="category" label="类别" width="130" />
          <el-table-column prop="key" label="Key" min-width="150" />
          <el-table-column label="内容" min-width="300"><template #default="scope"><div class="fact-value">{{ scope.row.value }}</div></template></el-table-column>
          <el-table-column label="可信度" width="100"><template #default="scope">{{ Math.round(scope.row.confidence * 100) }}%</template></el-table-column>
          <el-table-column prop="source" label="来源" width="110" />
          <el-table-column label="更新时间" min-width="170"><template #default="scope">{{ formatDate(scope.row.updatedUtc) }}</template></el-table-column>
          <el-table-column label="操作" width="120" fixed="right"><template #default="scope"><el-button link type="primary" @click="editFact(scope.row as MemoryFact)">编辑</el-button><el-button link type="danger" @click="removeFact(scope.row as MemoryFact)">删除</el-button></template></el-table-column>
          <template #empty><div class="empty-state">暂无结构化事实</div></template>
        </el-table>
      </div>

      <div class="panel"><div class="panel-head"><h3>相关短期会话</h3><span class="panel-note">只有存在待摘要消息的会话才能手动摘要</span></div>
        <el-table :data="store.sessions">
          <el-table-column prop="sessionId" label="Session ID" min-width="220" />
          <el-table-column prop="messageCount" label="窗口消息" width="100" align="center" />
          <el-table-column prop="pendingSummaryMessages" label="待摘要" width="100" align="center" />
          <el-table-column label="摘要状态" width="110" align="center"><template #default="scope"><el-tooltip v-if="scope.row.summaryError" :content="scope.row.summaryError" placement="top"><el-tag :type="scope.row.summaryStatus === 'failed' ? 'danger' : scope.row.summaryStatus === 'pending' ? 'warning' : 'info'" effect="plain">{{ scope.row.summaryStatus === 'failed' ? '失败' : scope.row.summaryStatus === 'pending' ? '处理中' : scope.row.summaryStatus === 'waiting' ? '待触发' : '空闲' }}</el-tag></el-tooltip><el-tag v-else :type="scope.row.summaryStatus === 'failed' ? 'danger' : scope.row.summaryStatus === 'pending' ? 'warning' : 'info'" effect="plain">{{ scope.row.summaryStatus === 'failed' ? '失败' : scope.row.summaryStatus === 'pending' ? '处理中' : scope.row.summaryStatus === 'waiting' ? '待触发' : '空闲' }}</el-tag></template></el-table-column>
          <el-table-column label="最后活跃" min-width="180"><template #default="scope">{{ formatDate(scope.row.lastActiveUtc) }}</template></el-table-column>
          <el-table-column label="操作" width="120"><template #default="scope"><el-button link type="primary" :disabled="scope.row.pendingSummaryMessages < 1 || scope.row.summaryStatus === 'pending'" @click="summarize(scope.row.sessionId)">{{ scope.row.summaryStatus === 'failed' ? '重试摘要' : '立即摘要' }}</el-button></template></el-table-column>
          <template #empty><div class="empty-state">暂无关联 Session</div></template>
        </el-table>
      </div>
    </div>
  </el-drawer>

  <el-dialog v-model="factDialogOpen" :title="editingFactId ? '编辑结构化事实' : '新增结构化事实'" width="620px">
    <el-form label-position="top">
      <div class="dialog-grid"><el-form-item label="类别"><el-select v-model="factForm.category" allow-create filterable><el-option v-for="item in ['player_profile','promise','quest','relationship','casual','general']" :key="item" :value="item" /></el-select></el-form-item><el-form-item label="唯一 Key"><el-input v-model="factForm.key" placeholder="例如 player.name，可留空" /></el-form-item></div>
      <el-form-item label="事实内容"><el-input v-model="factForm.value" type="textarea" :rows="4" maxlength="1000" show-word-limit /></el-form-item>
      <div class="dialog-grid"><el-form-item label="可信度"><el-slider v-model="factForm.confidence" :min="0" :max="1" :step="0.05" show-input /></el-form-item><el-form-item label="来源 Session"><el-input v-model="factForm.sourceSessionId" clearable /></el-form-item></div>
      <div class="dialog-grid"><el-form-item label="过期时间"><el-date-picker v-model="factForm.expiresUtc" type="datetime" value-format="YYYY-MM-DDTHH:mm:ss.SSSZ" clearable /></el-form-item><el-form-item label="固定事实"><el-switch v-model="factForm.pinned" active-text="固定后不被自动淘汰" /></el-form-item></div>
    </el-form>
    <template #footer><el-button @click="factDialogOpen = false">取消</el-button><el-button type="primary" @click="saveFact">保存</el-button></template>
  </el-dialog>
</template>

<style scoped>
.filter-count { margin-left: auto; color: #7b879a; font-size: 12px; }
.queue-strip { display: flex; align-items: center; gap: 10px; margin: 0 0 14px; padding: 11px 14px; border: 1px solid #e8edf5; border-radius: 8px; background: #fbfcfe; color: #66748b; font-size: 12px; }
.queue-label { color: #35445d; font-weight: 600; }
.queue-note { margin-left: auto; color: #8994a7; }
.pagination { display: flex; justify-content: flex-end; padding: 16px 18px; border-top: 1px solid #edf0f5; }
.detail-stack { display: grid; gap: 18px; }
.detail-toolbar { display: flex; align-items: center; gap: 12px; color: #748096; font-size: 12px; }
.toolbar-spacer { flex: 1; }
.panel-note { color: #8994a7; font-size: 11px; }
.dialog-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 18px; }
</style>
