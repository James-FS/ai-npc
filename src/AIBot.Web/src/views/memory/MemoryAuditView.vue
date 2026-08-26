<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { memoryApi } from '@/api/memory'
import { useAppStore } from '@/stores/app'
import { useMemoryAuditStore } from '@/stores/memoryAudit'

const app = useAppStore()
const store = useMemoryAuditStore()
const filter = reactive({ npcId: '', playerId: '', action: '', date: new Date().toISOString().slice(0, 10) })
const page = ref(1)
const pageSize = ref(50)
const cleanupDays = ref(90)
const cleanupLoading = ref(false)
const cleanupResult = ref<Record<string, unknown> | null>(null)
const cleanupDialog = ref(false)
const cleanupPreview = ref<{ gameId: string; inactiveDays: number } | null>(null)

const cleanupCandidateCount = computed(() => Number(cleanupResult.value?.candidateCount ?? 0))
const cleanupHasMore = computed(() => cleanupResult.value?.hasMoreCandidates === true)
const cleanupPreviewFresh = computed(() => cleanupResult.value?.dryRun === true
  && cleanupPreview.value?.gameId === app.gameId
  && cleanupPreview.value?.inactiveDays === cleanupDays.value)

function formatDate(value: string) { return new Date(value).toLocaleString() }
function pretty(value: unknown) { return JSON.stringify(value, null, 2) }

async function load(reset = false) {
  if (reset) page.value = 1
  try { await store.load(app.gameId, { ...filter, limit: pageSize.value, offset: (page.value - 1) * pageSize.value }) }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '审计记录加载失败') }
}

async function cleanup(dryRun: boolean) {
  try {
    if (!dryRun) {
      if (!cleanupPreviewFresh.value) {
        ElMessage.warning('清理条件已经变化，请先重新运行预演')
        return
      }
      await ElMessageBox.confirm(`将删除 ${cleanupCandidateCount.value} 份超过 ${cleanupDays.value} 天未更新的长期记忆，并清空这些玩家与 NPC 的相关 Session 消息及待摘要队列。版本冲突项会跳过。`, '执行数据清理', { type: 'error', confirmButtonText: '确认删除' })
    }
    cleanupLoading.value = true
    cleanupResult.value = await memoryApi.cleanup(app.gameId, cleanupDays.value, dryRun)
    cleanupPreview.value = dryRun
      ? { gameId: app.gameId, inactiveDays: cleanupDays.value }
      : null
    cleanupDialog.value = true
    ElMessage.success(dryRun ? '清理预演完成，未删除数据' : '清理执行完成')
    if (!dryRun) await load(true)
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '数据清理失败')
  } finally { cleanupLoading.value = false }
}

function resetCleanupPreview() {
  cleanupResult.value = null
  cleanupPreview.value = null
}

watch(cleanupDays, resetCleanupPreview)
watch(() => app.gameId, () => {
  resetCleanupPreview()
  load(true)
})
onMounted(() => load(true))
</script>

<template>
  <PageHeader title="记忆审计" description="查询 Game 策略、NPC 覆盖、人工记忆修改、迁移和保留期清理记录，并查看修改前后快照。">
    <el-button type="warning" plain @click="cleanupDialog = true">数据保留清理</el-button><el-button :loading="store.loading" @click="load()">刷新</el-button>
  </PageHeader>

  <div class="panel">
    <div class="filter-bar">
      <el-date-picker v-model="filter.date" value-format="YYYY-MM-DD" placeholder="审计日期" style="width:160px" />
      <el-select v-model="filter.npcId" clearable filterable placeholder="全部 NPC" style="width:190px"><el-option v-for="id in app.npcIds" :key="id" :value="id" :label="id" /></el-select>
      <el-input v-model="filter.playerId" clearable placeholder="playerId" style="width:190px" />
      <el-input v-model="filter.action" clearable placeholder="action，例如 memory.fact" style="width:230px" @keyup.enter="load(true)" />
      <el-button type="primary" @click="load(true)">查询</el-button>
      <span class="audit-count">{{ store.total }} 条</span>
    </div>
    <el-table v-loading="store.loading" :data="store.items" row-key="id">
      <el-table-column type="expand"><template #default="scope"><div class="diff-grid"><div><h4>Before</h4><pre class="code-block">{{ pretty(scope.row.before) }}</pre></div><div><h4>After</h4><pre class="code-block">{{ pretty(scope.row.after) }}</pre></div></div><div v-if="scope.row.metadata" class="metadata"><b>Metadata</b><pre class="code-block">{{ pretty(scope.row.metadata) }}</pre></div></template></el-table-column>
      <el-table-column label="时间" min-width="180"><template #default="scope">{{ formatDate(scope.row.ts) }}</template></el-table-column>
      <el-table-column prop="actor" label="操作人" min-width="140" />
      <el-table-column prop="action" label="Action" min-width="210"><template #default="scope"><el-tag effect="plain">{{ scope.row.action }}</el-tag></template></el-table-column>
      <el-table-column prop="npcId" label="NPC" min-width="150" />
      <el-table-column prop="playerId" label="Player ID" min-width="170" />
      <el-table-column prop="id" label="审计 ID" min-width="240" show-overflow-tooltip />
      <template #empty><div class="empty-state">该日期没有符合条件的审计记录</div></template>
    </el-table>
    <div class="pagination"><el-pagination v-model:current-page="page" v-model:page-size="pageSize" layout="total, sizes, prev, pager, next" :page-sizes="[20, 50, 100]" :total="store.total" @change="load()" /></div>
  </div>

  <el-dialog v-model="cleanupDialog" title="长期记忆与关联 Session 保留期清理" width="700px">
    <el-alert type="warning" :closable="false" title="先运行预演确认候选范围。执行时将删除长期记忆，并清空对应玩家与 NPC 的 Session 消息和待摘要队列；每条记录都会检查 memoryVersion 并写入审计。" />
    <el-form label-position="top" class="cleanup-form"><el-form-item label="未活跃天数"><el-input-number v-model="cleanupDays" :min="1" :max="3650" /><span class="retention-note">Server 建议值可在“系统边界”查看</span></el-form-item></el-form>
    <pre v-if="cleanupResult" class="code-block cleanup-result">{{ pretty(cleanupResult) }}</pre>
    <el-alert v-if="cleanupHasMore" class="cleanup-more" type="info" :closable="false" title="本次最多处理一个批次；完成后请重新预演，继续处理剩余的更旧记录。" />
    <template #footer><el-button @click="cleanupDialog = false">关闭</el-button><el-button :loading="cleanupLoading" @click="cleanup(true)">运行预演</el-button><el-button type="danger" :loading="cleanupLoading" :disabled="!cleanupPreviewFresh || cleanupCandidateCount < 1" @click="cleanup(false)">删除 {{ cleanupCandidateCount }} 项</el-button></template>
  </el-dialog>
</template>

<style scoped>
.audit-count { margin-left: auto; color: #7d899c; font-size: 12px; }
.pagination { display: flex; justify-content: flex-end; padding: 16px 18px; border-top: 1px solid #edf0f5; }
.diff-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 18px; padding: 4px 22px 16px; }
.diff-grid h4 { margin: 0 0 8px; color: #526078; }
.metadata { padding: 0 22px 18px; }
.metadata > b { display: block; margin-bottom: 8px; }
.cleanup-form { margin-top: 20px; }
.retention-note { margin-left: 12px; color: #8994a7; font-size: 11px; }
.cleanup-result { max-height: 300px; }
.cleanup-more { margin-top: 12px; }
</style>
