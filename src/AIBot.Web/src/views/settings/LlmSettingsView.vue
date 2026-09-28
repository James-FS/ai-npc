<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import ModelConnectionsPanel from '@/components/ModelConnectionsPanel.vue'
import { settingsApi, type LlmKeyStatus } from '@/api/settings'

const status = ref<LlmKeyStatus | null>(null)
const loading = ref(false)
const gameFilter = ref('')
const visibleGames = computed(() => (status.value?.games || []).filter(game => !gameFilter.value || game.gameId === gameFilter.value))
const visibleNpcs = computed(() => (status.value?.npcs || []).filter(npc => !gameFilter.value || npc.gameId === gameFilter.value))
const sourceLabel: Record<string, string> = { connection: 'NPC 专用连接', global_connection: '控制台默认连接', npc: '旧 NPC 独立 Key', game: 'Game 摘要模型', main: '主模型 Key', none: '未配置', core: '默认策略' }
function label(source?: string | null) { return sourceLabel[source || 'none'] || source || '未配置' }

async function load() {
  loading.value = true
  try { status.value = await settingsApi.llmKeyStatus() }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : 'Key 状态加载失败') }
  finally { loading.value = false }
}
onMounted(load)
</script>

<template>
  <PageHeader title="模型连接管理" description="统一创建模型连接，并分配给控制台默认模型、NPC 和摘要模型；Key 仅脱敏显示。">
    <el-button :loading="loading" @click="load">刷新状态</el-button>
  </PageHeader>
  <div v-loading="loading">
    <ModelConnectionsPanel :key-status="status" @changed="load" />
    <el-alert type="info" show-icon :closable="false" class="section-gap"
      title="主模型优先级：NPC 专用连接 > 控制台默认连接"
      description="未分配连接的旧 NPC 配置暂作兼容；建议在上方选择控制台默认连接。摘要连接可单独分配。" />

    <div class="panel section-gap">
      <div class="panel-head"><h3>Game 摘要模型 Key</h3></div>
      <div class="panel-body">
        <p class="key-note">连接分配在上方操作；旧记忆策略 Key 仅用于未分配连接时的兼容回退。</p>
        <el-select v-model="gameFilter" placeholder="全部 Game" clearable class="game-filter"><el-option v-for="game in status?.games || []" :key="game.gameId" :label="game.gameId" :value="game.gameId" /></el-select>
        <el-table :data="visibleGames" empty-text="暂无 Game">
          <el-table-column prop="gameId" label="Game" min-width="140" />
          <el-table-column label="已分配的连接" min-width="220"><template #default="{ row }"><div v-if="row.connectionName"><strong>{{ row.connectionName }}</strong><div class="key-note">Key：{{ row.connectionMaskedKey }}</div></div><span v-else class="key-note">未分配连接</span></template></el-table-column>
          <el-table-column label="原记忆策略 Key" min-width="220"><template #default="{ row }">{{ row.hasSummaryModel ? (row.summaryMaskedKey || '未配置') : '未启用独立摘要模型' }}</template></el-table-column>
        </el-table>
      </div>
    </div>

    <div class="panel section-gap">
      <div class="panel-head"><h3>NPC Key 与最终来源</h3></div>
      <div class="panel-body"><el-table :data="visibleNpcs" empty-text="暂无 NPC">
        <el-table-column label="NPC" min-width="180"><template #default="{ row }"><strong>{{ row.displayName }}</strong><div class="key-note">{{ row.gameId }} / {{ row.npcId }}</div></template></el-table-column>
        <el-table-column label="对话主模型" min-width="250"><template #default="{ row }"><div class="key-value small">当前 Key：{{ row.mainEffectiveMaskedKey || '无 Key' }}</div><div class="key-note">来源：{{ row.mainConnectionName ? `${row.mainSource === 'global_connection' ? '控制台默认' : 'NPC 专用'}连接「${row.mainConnectionName}」` : label(row.mainSource) }}</div><div v-if="!row.mainConnectionName" class="key-note">旧 NPC Key：{{ row.mainMaskedKey || '未配置' }}</div></template></el-table-column>
        <el-table-column label="记忆摘要模型" min-width="300"><template #default="{ row }"><div class="key-value small">当前 Key：{{ row.summaryMaskedKey || '无可用 Key' }}</div><div class="key-note">来源：{{ row.summaryConnectionName ? `${row.summaryConnectionScope === 'game' ? 'Game 默认' : 'NPC 专用'}连接「${row.summaryConnectionName}」` : row.summarySource === 'main' ? `主模型（${label(row.summaryMainSource)}）` : label(row.summarySource) }}</div><div v-if="!row.summaryConnectionName" class="key-note">旧 NPC 摘要 Key：{{ row.npcSummaryMaskedKey || '未配置' }}</div></template></el-table-column>
      </el-table></div>
    </div>
  </div>
</template>

<style scoped>
.section-gap { margin-bottom: 18px; }
.key-value { font-family: var(--font-mono); font-size: 17px; font-weight: 700; margin: 8px 0; overflow-wrap: anywhere; }
.key-value.small { font-size: 13px; margin: 0 0 4px; }
.key-note { color: var(--graphite); font-size: 12px; line-height: 1.5; }
.actions { display: flex; gap: 6px; margin-top: 12px; flex-wrap: wrap; }
.actions :deep(.el-button + .el-button) { margin-left: 0; }
.game-filter { width: 230px; margin-bottom: 14px; }
@media (max-width: 900px) { .source-grid { grid-template-columns: 1fr; } }
</style>
