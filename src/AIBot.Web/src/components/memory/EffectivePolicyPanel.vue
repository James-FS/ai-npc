<script setup lang="ts">
import type { EffectiveMemoryPolicy } from '@/types/memory'

defineProps<{ value: EffectiveMemoryPolicy | null }>()

const fields = [
  ['shortTermTurns', '短期轮数'],
  ['summaryThreshold', '摘要阈值'],
  ['summaryTrigger', '摘要触发器'],
  ['memoryScope', '记忆范围'],
  ['maxFacts', '最大事实数'],
  ['rememberPlayerProfile', '玩家档案'],
  ['rememberPromises', '承诺'],
  ['rememberQuestEvents', '任务事件'],
  ['rememberCasualChat', '闲聊'],
  ['backgroundSummarization', '后台摘要'],
] as const

function display(value: unknown) {
  if (typeof value === 'boolean') return value ? '开启' : '关闭'
  return String(value ?? '—')
}
</script>

<template>
  <div class="panel effective-panel">
    <div class="panel-head"><h3>最终生效策略</h3><el-tag type="info" effect="plain">实时解析</el-tag></div>
    <div v-if="value" class="panel-body effective-list">
      <div v-for="field in fields" :key="field[0]" class="effective-row">
        <span>{{ field[1] }}</span>
        <strong>{{ display(value.policy[field[0]]) }}</strong>
        <span class="source-tag">{{ value.sources[field[0]] || 'core' }}</span>
      </div>
      <el-alert v-if="value.adjustments.length" class="section-gap" type="warning" :closable="false">
        <template #title>Server 已应用 {{ value.adjustments.length }} 项安全修正</template>
        <div v-for="item in value.adjustments" :key="item">{{ item }}</div>
      </el-alert>
    </div>
    <div v-else class="empty-state">加载策略后显示最终值与来源</div>
  </div>
</template>

<style scoped>
.effective-list { display: grid; gap: 1px; }
.effective-row { display: grid; grid-template-columns: 1fr auto auto; align-items: center; gap: 10px; min-height: 38px; border-bottom: 1px solid #f0f2f6; font-size: 12px; }
.effective-row:last-child { border-bottom: 0; }
.effective-row > span:first-child { color: #66748b; }
.effective-row strong { font-size: 12px; }
</style>
