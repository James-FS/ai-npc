<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { memoryApi } from '@/api/memory'
import { useAppStore } from '@/stores/app'
import type { MigrationCandidate } from '@/types/memory'

const app = useAppStore()
const items = ref<MigrationCandidate[]>([])
const loading = ref(false)
const filterNpcId = ref('')
const playerIds = reactive<Record<string, string>>({})

function rowKey(row: MigrationCandidate) { return `${row.npcId}/${row.sessionId}` }
function formatDate(value: string) { return new Date(value).toLocaleString() }

async function load() {
  loading.value = true
  try {
    const result = await memoryApi.migrations(app.gameId, filterNpcId.value || undefined)
    items.value = result.items
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '迁移候选加载失败') }
  finally { loading.value = false }
}

async function migrate(row: MigrationCandidate) {
  const playerId = playerIds[rowKey(row)]?.trim()
  if (!playerId) return ElMessage.warning('请填写目标 playerId')
  try {
    await ElMessageBox.confirm(`把 Session ${row.sessionId} 的旧摘要与事实迁移到 ${row.npcId} × ${playerId}？`, '确认显式迁移', { type: 'warning' })
    const result = await memoryApi.migrate(app.gameId, row.npcId, playerId, row.sessionId)
    ElMessage.success(result.migrated ? `迁移完成，长期记忆版本 ${result.memory.memoryVersion}` : '该 Session 已迁移，无需重复处理')
    await load()
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '迁移失败')
  }
}

watch(() => app.gameId, load)
onMounted(load)
</script>

<template>
  <PageHeader title="旧记忆迁移" description="将未绑定 playerId 的旧 Session 摘要和字符串事实显式归属到稳定玩家身份；迁移是幂等的并写入审计。">
    <el-button :loading="loading" @click="load">刷新候选</el-button>
  </PageHeader>

  <div class="hint-box warning-box migration-warning">迁移不会猜测玩家身份。请使用游戏内部稳定 ID，不要使用昵称。迁移成功后旧 Session 仅保留短期消息和迁移标记。</div>
  <div class="panel section-gap">
    <div class="filter-bar"><el-select v-model="filterNpcId" clearable filterable placeholder="全部 NPC" style="width:240px"><el-option v-for="id in app.npcIds" :key="id" :value="id" :label="id" /></el-select><el-button type="primary" @click="load">筛选</el-button><span class="count">{{ items.length }} 个待处理 Session</span></div>
    <el-table v-loading="loading" :data="items">
      <el-table-column prop="npcId" label="NPC" min-width="170" />
      <el-table-column prop="sessionId" label="旧 Session ID" min-width="220" />
      <el-table-column label="摘要" width="90" align="center"><template #default="scope"><el-tag :type="scope.row.hasSummary ? 'success' : 'info'" effect="plain">{{ scope.row.hasSummary ? '有' : '无' }}</el-tag></template></el-table-column>
      <el-table-column prop="factCount" label="旧事实" width="90" align="center" />
      <el-table-column label="最后活跃" min-width="180"><template #default="scope">{{ formatDate(scope.row.lastActiveUtc) }}</template></el-table-column>
      <el-table-column label="目标 Player ID" min-width="240"><template #default="scope"><el-input v-model="playerIds[rowKey(scope.row as MigrationCandidate)]" placeholder="例如 player-001" clearable /></template></el-table-column>
      <el-table-column label="操作" width="110" fixed="right"><template #default="scope"><el-button type="primary" link @click="migrate(scope.row as MigrationCandidate)">执行迁移</el-button></template></el-table-column>
      <template #empty><div class="empty-state">当前没有需要显式迁移的旧记忆</div></template>
    </el-table>
  </div>
</template>

<style scoped>
.migration-warning { padding: 16px 18px; }
.count { margin-left: auto; color: #7d899c; font-size: 12px; }
</style>
