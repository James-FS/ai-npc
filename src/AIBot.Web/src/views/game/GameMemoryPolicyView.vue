<script setup lang="ts">
import { onMounted, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import MemoryPolicyForm from '@/components/memory/MemoryPolicyForm.vue'
import EffectivePolicyPanel from '@/components/memory/EffectivePolicyPanel.vue'
import { useAppStore } from '@/stores/app'
import { useGameMemoryPolicyStore } from '@/stores/gameMemoryPolicy'
import type { MemoryPolicy } from '@/types/memory'

const app = useAppStore()
const store = useGameMemoryPolicyStore()
const { policy, limits, effective } = storeToRefs(store)

const presets: Record<string, Partial<MemoryPolicy>> = {
  '低成本': { shortTermTurns: 6, summaryThreshold: 40, maxFacts: 5, rememberCasualChat: false, backgroundSummarization: true },
  '标准': { shortTermTurns: 12, summaryThreshold: 20, maxFacts: 8, rememberPlayerProfile: true, rememberPromises: true, rememberQuestEvents: true, rememberCasualChat: false, backgroundSummarization: true },
  '剧情强化': { shortTermTurns: 20, summaryThreshold: 12, maxFacts: 16, rememberPlayerProfile: true, rememberPromises: true, rememberQuestEvents: true, rememberCasualChat: true, backgroundSummarization: true },
  '无长期记忆': { shortTermTurns: 8, summaryThreshold: 0, memoryScope: 'session', maxFacts: 1, rememberPlayerProfile: false, rememberPromises: false, rememberQuestEvents: false, rememberCasualChat: false, backgroundSummarization: false },
}

async function load() {
  try { await store.load(app.gameId) } catch (error) { ElMessage.error(error instanceof Error ? error.message : 'Game 策略加载失败') }
}

async function applyPreset(name: string) {
  if (!policy.value) return
  try {
    await ElMessageBox.confirm(`将“${name}”预设应用到当前表单？保存前仍可调整。`, '应用预设', { type: 'info' })
    policy.value = { ...JSON.parse(JSON.stringify(policy.value)), ...presets[name] }
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '预设应用失败')
  }
}

async function save() {
  try {
    if (policy.value?.memoryScope === 'player_npc') {
      await ElMessageBox.confirm('player_npc 会让同一玩家跨 Session 共享长期记忆。请确认游戏请求会稳定传入 playerId。', '确认记忆范围', { type: 'warning' })
    }
    await store.save(app.gameId)
    ElMessage.success('Game 记忆策略已保存')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '保存失败')
  }
}

watch(() => app.gameId, load)
onMounted(load)
</script>

<template>
  <PageHeader title="Game 记忆策略" description="为当前 Game 定义所有 NPC 的默认记忆行为；NPC 页面可以按字段继承或覆盖。">
    <el-dropdown @command="applyPreset"><el-button>应用预设<span class="dropdown-caret">▼</span></el-button><template #dropdown><el-dropdown-menu><el-dropdown-item v-for="(_, name) in presets" :key="name" :command="name">{{ name }}</el-dropdown-item></el-dropdown-menu></template></el-dropdown>
    <el-button type="primary" :loading="store.saving" :disabled="!policy" @click="save">保存策略</el-button>
  </PageHeader>

  <div class="two-col" v-loading="store.loading">
    <div class="panel"><div class="panel-head"><h3>{{ app.gameId }} / memory-policy.json</h3><el-tag effect="plain">Game 默认值</el-tag></div><div v-if="policy" class="panel-body"><MemoryPolicyForm v-model="policy" mode="game" :effective="effective?.policy" :sources="effective?.sources" :limits="limits" /></div><div v-else class="empty-state">暂无策略数据</div></div>
    <div><EffectivePolicyPanel :value="effective" /><div class="hint-box section-gap">保存后 Server 会重新解析并应用安全上限。右侧最终策略显示实际持久化后的值；API Key 永远不会回显。</div></div>
  </div>
</template>

<style scoped>
.dropdown-caret { font-size: 10px; margin-left: 4px; vertical-align: 1px; }
</style>

