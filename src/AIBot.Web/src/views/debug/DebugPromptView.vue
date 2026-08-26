<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'
import type { PromptPreview, SimGameState } from '@/types/debug'

const app = useAppStore()
const playerId = ref(localStorage.getItem('aibot.debug.playerId') || 'player-local')
const sessionId = ref(localStorage.getItem('aibot.debug.sessionId') || '')
const stage = ref(0)
const favorability = ref(30)
const preview = ref<PromptPreview | null>(null)
const loading = ref(false)
const npcId = computed(() => app.currentNpcId)
const state = computed<SimGameState>(() => ({ stage: stage.value, favorability: favorability.value, extras: {}, items: {} }))

async function load() {
  if (!npcId.value) return
  loading.value = true
  try { preview.value = await debugApi.previewPrompt(app.gameId, npcId.value, { playerId: playerId.value, sessionId: sessionId.value || undefined, simState: state.value }) }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : 'Prompt 预览失败') }
  finally { loading.value = false }
}
watch(() => [app.gameId, app.selectedNpcId], () => { preview.value = null })
onMounted(load)
</script>

<template>
  <PageHeader title="Prompt 分层预览" description="查看当前 NPC、世界观、模拟状态和玩家记忆合并后的最终 System Prompt，并估算 token 使用量"><el-button type="primary" :loading="loading" :disabled="!npcId" @click="load">生成预览</el-button></PageHeader>
  <div class="two-col">
    <div class="panel panel-body"><el-form inline><el-form-item label="Player ID"><el-input v-model="playerId" /></el-form-item><el-form-item label="Session ID"><el-input v-model="sessionId" placeholder="可选" /></el-form-item><el-form-item label="阶段"><el-input-number v-model="stage" :min="0" /></el-form-item><el-form-item label="好感"><el-input-number v-model="favorability" :min="-100" :max="100" /></el-form-item></el-form><div v-if="preview" class="meter"><i :style="{ width: `${Math.min(100, preview.totalEstTokens / preview.budget * 100)}%` }"></i></div><div v-if="preview" class="hint-box">预计 {{ preview.totalEstTokens }} tokens / 预算 {{ preview.budget }}</div></div>
    <div class="panel panel-body"><pre v-if="preview" class="code-block prompt-full">{{ preview.systemPrompt }}</pre><div v-else class="empty-state">点击“生成预览”查看完整 Prompt</div></div>
  </div>
  <div v-if="preview" class="section-gap"><div v-for="layer in preview.layers" :key="layer.name" class="prompt-layer" :style="{ borderLeftColor: layer.color }"><div class="layer-head"><b>{{ layer.name }}</b><span>{{ layer.estTokens }} tokens</span></div><pre>{{ layer.text }}</pre></div></div>
</template>

<style scoped>
.meter { height: 10px; background: #e8edf4; border-radius: 6px; margin-top: 16px; overflow: hidden; }
.meter i { display: block; height: 100%; background: linear-gradient(90deg, #3478f6, #13b8a6); }
.prompt-full { max-height: 500px; }
.prompt-layer { background: white; border-left: 5px solid; border-radius: 10px; padding: 14px 18px; margin-bottom: 12px; box-shadow: 0 6px 18px rgba(23,35,60,.04); }
.layer-head { display: flex; justify-content: space-between; color: #6d7b90; font-size: 12px; margin-bottom: 8px; }
.prompt-layer pre { margin: 0; white-space: pre-wrap; line-height: 1.6; font: 13px/1.6 inherit; }
</style>
