<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import LayerStack from '@/components/LayerStack.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'
import { percentOf, rampClass, shareOf } from '@/utils/ramp'
import type { PromptPreview, SimGameState } from '@/types/debug'

const app = useAppStore()
const playerId = ref(localStorage.getItem('aibot.debug.playerId') || 'player-local')
// 会话按 NPC 隔离，与流式对话页保持一致；旧的全局键已被对话页主动移除，
// 因此这里不能再读取它，否则只要访问过对话页，本页 Session ID 就会永远为空。
const npcId = computed(() => app.currentNpcId)
function sessionKey() { return `aibot.debug.sessionId.${npcId.value || 'none'}` }
const sessionId = ref(localStorage.getItem(sessionKey()) || '')
const stage = ref(0)
const favorability = ref(30)
const preview = ref<PromptPreview | null>(null)
const loading = ref(false)
const state = computed<SimGameState>(() => ({ stage: stage.value, favorability: favorability.value, extras: {}, items: {} }))

/// 层条只关心「谁在吃这次的用量」，因此相对总量计算，不使用后端给的任意层色。
const layers = computed(() => (preview.value?.layers ?? []).map(layer => ({
  name: layer.name, tokens: layer.estTokens, text: layer.text,
})))

const budgetShare = computed(() => shareOf(preview.value?.totalEstTokens ?? 0, preview.value?.budget ?? 0))
const budgetTone = computed(() => rampClass(budgetShare.value))
const budgetWidth = computed(() => `${Math.min(100, budgetShare.value * 100)}%`)
const budgetPercent = computed(() => percentOf(preview.value?.totalEstTokens ?? 0, preview.value?.budget ?? 0))

function loadSessionId() {
  sessionId.value = localStorage.getItem(sessionKey()) || ''
}
watch(() => npcId.value, loadSessionId)

async function load() {
  if (!npcId.value) return
  loading.value = true
  try { preview.value = await debugApi.previewPrompt(app.gameId, npcId.value, { playerId: playerId.value, sessionId: sessionId.value || undefined, simState: state.value }) }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : 'Prompt 预览失败') }
  finally { loading.value = false }
}
watch(() => [app.gameId, app.selectedNpcId], () => { preview.value = null })
onMounted(() => { loadSessionId(); load() })
</script>

<template>
  <PageHeader title="Prompt 分层预览" description="查看当前 NPC、世界观、模拟状态和玩家记忆合并后的最终 System Prompt，并估算 token 使用量">
    <el-input v-model="playerId" class="ctrl ctrl-player" placeholder="Player ID" />
    <el-input v-model="sessionId" class="ctrl ctrl-session" placeholder="Session ID（可选）" />
    <el-input-number v-model="stage" class="ctrl ctrl-number" :min="0" controls-position="right" />
    <el-input-number v-model="favorability" class="ctrl ctrl-number" :min="-100" :max="100" controls-position="right" />
    <el-button type="primary" :loading="loading" :disabled="!npcId" @click="load">生成预览</el-button>
  </PageHeader>

  <template v-if="preview">
    <div class="budget-hero">
      <div class="budget-figure">
        <strong>{{ preview.totalEstTokens }}</strong>
        <span> / {{ preview.budget }} tokens</span>
      </div>
      <div class="budget-ruler"><i :class="budgetTone" :style="{ width: budgetWidth }"></i></div>
      <div class="budget-percent">{{ budgetPercent }}</div>
    </div>

    <div class="panel">
      <div class="panel-head">
        <h3>分层构成</h3>
        <span class="panel-note">条长与色深表示该层占本次用量的份额，点击可展开原文</span>
      </div>
      <div class="panel-body">
        <LayerStack :layers="layers" :total="preview.totalEstTokens" />
      </div>
    </div>
  </template>
  <div v-else class="panel panel-body empty-state">点击「生成预览」查看分层 Prompt</div>
</template>

<style scoped>
.ctrl { flex: 0 0 auto; }
.ctrl-player { width: 150px; }
.ctrl-session { width: 200px; }
.ctrl-number { width: 108px; }

/* 预算量尺是这一页的论点：先回答「还剩多少」，再让下面的层回答「谁在吃」 */
.budget-hero {
  display: flex;
  align-items: center;
  gap: 22px;
  margin-bottom: 18px;
  padding: 18px 22px;
  background: var(--card);
  border: 1px solid var(--line);
  border-radius: var(--radius-card);
  box-shadow: 0 8px 28px rgba(23,35,60,.045);
}
.budget-figure { white-space: nowrap; }
.budget-figure strong { font: 700 24px/1 var(--font-mono); letter-spacing: -1px; }
.budget-figure span { font-size: 13px; color: var(--graphite); }
.budget-ruler { flex: 1; height: 12px; border-radius: var(--radius-chip); background: var(--surface-track); overflow: hidden; }
.budget-ruler i { display: block; height: 100%; min-width: 4px; border-radius: var(--radius-chip); transition: width .3s ease; }
.budget-ruler i.ramp-cool { background: var(--ramp-cool); }
.budget-ruler i.ramp-warm { background: var(--ramp-warm); }
.budget-ruler i.ramp-hot { background: var(--ramp-hot); }
.budget-ruler i.ramp-peak { background: var(--ramp-peak); }
.budget-percent { min-width: 52px; text-align: right; font-size: 15px; font-weight: 700; }

@media (max-width: 900px) {
  .ctrl-player, .ctrl-session { width: 100%; }
  .budget-hero { flex-wrap: wrap; gap: 12px; }
  .budget-ruler { flex: 1 1 100%; order: 3; }
  .budget-percent { margin-left: auto; }
}
</style>
