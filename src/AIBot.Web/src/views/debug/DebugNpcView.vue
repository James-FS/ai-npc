<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { onBeforeUnmount } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Connection } from '@element-plus/icons-vue'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'
import type { DebugAgentConfig, LoreBlock } from '@/types/debug'

import { useUnsaved, confirmDiscard } from '@/utils/unsaved'
const app = useAppStore()
const config = ref<DebugAgentConfig | null>(null)
const { dirty, markSaved } = useUnsaved(config)
const loading = ref(false)
const saving = ref(false)
const testResult = ref<Record<string, unknown> | null>(null)
const testing = ref(false)
const npcId = computed(() => app.currentNpcId)
let loadSeq = 0, saveSeq = 0, testSeq = 0
let owner = ''
const clone = <T,>(x: T): T => JSON.parse(JSON.stringify(x)) as T

function blankLore(): LoreBlock { return { title: '', content: '', unlockStage: 0, isSecret: false, enabled: true } }
function normalize(value: DebugAgentConfig): DebugAgentConfig {
  return { ...value, loreBlocks: value.loreBlocks || [], enabledToolIds: value.enabledToolIds || [], fallbackReplies: value.fallbackReplies || [], model: value.model || { baseUrl: '', model: '', temperature: .8, maxTokens: 500, timeoutMs: 20000 }, output: value.output || { emotions: [], actions: [] } }
}

async function reset() { if (await confirmDiscard()) await load() }
async function load() {
  const seq = ++loadSeq, requested = { game: app.gameId, npc: npcId.value }
  owner = `${requested.game}/${requested.npc}`
  config.value = null; testResult.value = null
  saving.value = false; testing.value = false
  if (!requested.npc) { loading.value = false; return }
  loading.value = true
  try { const result = normalize(await debugApi.npc(requested.game, requested.npc)); if (seq === loadSeq && owner === `${app.gameId}/${npcId.value}`) { config.value = result; markSaved() } } catch (error) {
    // 切换 Game 的瞬间会用旧 npcId 发一次请求；响应回来时上下文已变即视为过期，静默丢弃
    const stale = seq !== loadSeq || app.gameId !== requested.game || app.currentNpcId !== requested.npc
    if (!stale) ElMessage.error(error instanceof Error ? error.message : 'NPC 配置加载失败')
  }
  finally { if (seq === loadSeq) loading.value = false }
}

async function save() {
  if (!config.value || !npcId.value || owner !== `${app.gameId}/${npcId.value}`) return
  const target = { game: app.gameId, npc: npcId.value }, epoch = loadSeq, payload = clone(config.value), seq = ++saveSeq
  saving.value = true
  try { await debugApi.saveNpc(target.game, target.npc, payload); if (seq === saveSeq && epoch === loadSeq && owner === `${app.gameId}/${npcId.value}`) { markSaved(payload); ElMessage.success('NPC 配置已保存') } }
  catch (error) { if (seq === saveSeq && epoch === loadSeq && owner === `${app.gameId}/${npcId.value}`) ElMessage.error(error instanceof Error ? error.message : 'NPC 配置保存失败') }
  finally { if (seq === saveSeq && epoch === loadSeq) saving.value = false }
}

async function createNpc() {
  if (!await confirmDiscard()) return
  const game = app.gameId
  let id = ''
  try {
    const { value } = await ElMessageBox.prompt(
      '将按内置模板创建 NPC 配置，创建后可在当前页完善人设；模型连接在模型连接管理页分配。',
      '新建 NPC',
      {
        inputPattern: /^[a-zA-Z0-9_.:-]{1,64}$/,
        inputErrorMessage: 'NPC ID 仅允许字母数字与 _ . : -（1~64 位）',
        confirmButtonText: '创建',
        cancelButtonText: '取消',
      },
    )
    id = value.trim()
    await debugApi.createNpc(game, id)
    if (app.gameId === game) { await app.loadNpcs(); if (app.gameId === game) { app.selectedNpcId = id; ElMessage.success(`NPC「${id}」已创建`) } }
  } catch (error) {
    if (error !== 'cancel' && error !== 'close' && app.gameId === game) ElMessage.error(error instanceof Error ? error.message : 'NPC 创建失败')
  }
}

async function deleteNpc() {
  if (!npcId.value) return
  const target = { game: app.gameId, npc: npcId.value }
  try {
    await ElMessageBox.confirm(`确认删除 NPC「${target.npc}」？配置文件将被移除。`, '删除 NPC', { type: 'error', confirmButtonText: '确认删除' })
    await debugApi.deleteNpc(target.game, target.npc); if (app.gameId === target.game && npcId.value === target.npc) { await app.loadNpcs(); ElMessage.success('NPC 已删除') }
  } catch (error) { if (error !== 'cancel' && error !== 'close' && app.gameId === target.game) ElMessage.error(error instanceof Error ? error.message : 'NPC 删除失败') }
}

async function testConnection() {
  if (!config.value || !npcId.value) return
  const target = { game: app.gameId, npc: npcId.value }, epoch = loadSeq, seq = ++testSeq
  testing.value = true
  try { const result = await debugApi.testConnection(target.game, target.npc, {}); if (seq === testSeq && epoch === loadSeq && owner === `${app.gameId}/${npcId.value}`) { testResult.value = result; ElMessage.success(result.ok ? '连接测试成功' : '连接测试完成，请查看诊断') } }
  catch (error) { if (seq === testSeq && epoch === loadSeq && owner === `${app.gameId}/${npcId.value}`) ElMessage.error(error instanceof Error ? error.message : '连接测试失败') }
  finally { if (seq === testSeq && epoch === loadSeq) testing.value = false }
}

function addLore() { config.value?.loreBlocks.push(blankLore()) }
function removeLore(index: number) { config.value?.loreBlocks.splice(index, 1) }
function addReply() { config.value?.fallbackReplies.push('（沉默片刻）……') }
function removeReply(index: number) { config.value?.fallbackReplies.splice(index, 1) }
function addTag(kind: 'emotions' | 'actions') { config.value?.output[kind].push(kind === 'emotions' ? 'neutral' : 'idle') }
function removeTag(kind: 'emotions' | 'actions', index: number) { config.value?.output[kind].splice(index, 1) }

// 保存栏状态由现有加载/数据/脏/保存标记派生；未加载或加载失败不得显示“已保存”
const statusText = computed(() => saving.value ? '保存中' : loading.value ? '加载中' : !config.value ? '未加载' : dirty.value ? '未保存修改' : '已保存')
function onHeaderCommand(command: string | number | object) { if (command === 'delete') void deleteNpc() }

watch(() => [app.serverBase, app.gameId, app.selectedNpcId], load, { flush: 'sync' })
onMounted(load)
onBeforeUnmount(() => { loadSeq++; saveSeq++; testSeq++ })
</script>

<template>
  <fieldset class="editor-fields" :disabled="saving">
  <PageHeader title="NPC 配置调试" description="维护人设、剧情块与兜底台词；模型连接在模型连接管理页统一分配。">
    <el-button :icon="Plus" @click="createNpc">新建 NPC</el-button>
    <el-dropdown @command="onHeaderCommand"><el-button>更多操作<span class="dropdown-caret">▼</span></el-button><template #dropdown><el-dropdown-menu><el-dropdown-item command="delete" :disabled="!config" style="color: var(--el-color-danger)">删除 NPC</el-dropdown-item></el-dropdown-menu></template></el-dropdown>
  </PageHeader>
  <div v-loading="loading" class="panel" v-if="config">
    <div class="panel-body editor-grid">
      <div class="editor-column">
        <el-form label-position="top">
          <el-form-item label="NPC ID"><el-input v-model="config.npcId" disabled /></el-form-item>
          <el-form-item label="显示名称"><el-input v-model="config.displayName" /></el-form-item>
          <el-form-item label="人设"><el-input v-model="config.persona" type="textarea" :rows="5" /></el-form-item>
          <el-form-item label="背景故事"><el-input v-model="config.backstory" type="textarea" :rows="5" /></el-form-item>
          <el-form-item label="World ID"><el-input v-model="config.worldId" /></el-form-item>
        </el-form>
      </div>
      <details class="editor-column"><summary>生成参数与连接测试</summary>
        <p class="model-note">Base URL、模型 ID 和 API Key 在 <router-link to="/settings/llm">模型连接管理</router-link>中统一配置。这里保留每个 NPC 的生成参数。</p>
        <el-form label-position="top"><el-form-item label="Temperature"><el-input-number v-model="config.model.temperature" :min="0" :max="2" :step="0.1" /></el-form-item><el-form-item label="Max Tokens"><el-input-number v-model="config.model.maxTokens" :min="1" :max="10000" /></el-form-item><el-form-item label="Timeout (ms)"><el-input-number v-model="config.model.timeoutMs" :min="1000" :max="120000" /></el-form-item></el-form>
        <el-button :icon="Connection" :loading="testing" @click="testConnection">测试连接</el-button>
        <pre v-if="testResult" class="code-block result-block">{{ JSON.stringify(testResult, null, 2) }}</pre>
      </details>
    </div>
    <div class="panel-body section-border"><div class="section-heading"><h3>剧情知识块</h3><el-button size="small" :icon="Plus" @click="addLore">添加</el-button></div><div v-for="(lore, index) in config.loreBlocks" :key="index" class="lore-card"><div class="lore-head"><el-input v-model="lore.title" placeholder="标题" /><el-input-number v-model="lore.unlockStage" :min="0" controls-position="right" /><el-switch v-model="lore.enabled" active-text="启用" /><el-switch v-model="lore.isSecret" active-text="秘密" /><el-button link type="danger" @click="removeLore(index)">删除</el-button></div><el-input v-model="lore.content" type="textarea" :rows="3" placeholder="知识内容" /></div><div v-if="!config.loreBlocks.length" class="empty-state">暂无剧情知识块</div></div>
    <details class="panel-body section-border"><summary>兜底台词</summary><div class="section-heading"><el-button size="small" :icon="Plus" @click="addReply">添加</el-button></div><div v-for="(_, index) in config.fallbackReplies" :key="index" class="reply-row"><el-input v-model="config.fallbackReplies[index]" /><el-button link type="danger" @click="removeReply(index)">删除</el-button></div></details>
    <details class="panel-body section-border"><summary>输出枚举</summary><div class="tag-section"><b>情绪</b><el-tag v-for="(tag, index) in config.output.emotions" :key="index" closable @close="removeTag('emotions', index)">{{ tag }}</el-tag><el-button size="small" @click="addTag('emotions')">添加</el-button></div><div class="tag-section"><b>动作</b><el-tag v-for="(tag, index) in config.output.actions" :key="index" closable @close="removeTag('actions', index)">{{ tag }}</el-tag><el-button size="small" @click="addTag('actions')">添加</el-button></div></details>
  </div>
  <div v-else class="panel empty-state" v-loading="loading">当前 Game 没有可编辑的 NPC，请先创建或刷新 NPC 列表。<div class="empty-actions"><el-button :loading="loading" @click="reset">重新加载</el-button></div></div>
  </fieldset>
  <div class="save-bar"><span>{{ app.gameId }} · {{ statusText }}</span><el-button :disabled="!dirty || saving || loading" @click="reset">撤销未保存修改</el-button><el-button type="primary" :loading="saving" :disabled="!config || !dirty || saving || loading" @click="save">保存</el-button></div>
</template>

<style scoped>
.editor-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 28px; }
.section-border { border-top: 1px solid #edf0f5; }
.section-title, .section-heading h3 { font-size: 15px; font-weight: 700; margin: 0 0 14px; }
.model-note { color: var(--graphite); font-size: 13px; line-height: 1.5; }
.section-heading { display: flex; justify-content: space-between; align-items: center; }
.lore-card { padding: 12px; background: var(--surface-inset); border: 1px solid #e6ebf2; border-radius: var(--radius-inset); margin-bottom: 10px; }
.lore-head { display: grid; grid-template-columns: 1fr 120px auto auto auto; gap: 8px; align-items: center; margin-bottom: 8px; }
.lore-head :deep(.el-input-number) { width: 100%; }
.reply-row { display: flex; gap: 8px; margin-bottom: 8px; }
.reply-row .el-input { flex: 1; }
.tag-section { display: flex; align-items: center; gap: 8px; margin: 12px 0; flex-wrap: wrap; }
.tag-section b { width: 40px; color: var(--graphite); font-size: 13px; }
.result-block { margin-top: 14px; max-height: 220px; }
.dropdown-caret { font-size: 10px; margin-left: 4px; vertical-align: 1px; }
.empty-actions { margin-top: 12px; }
.clear-key-hint { margin-top: 6px; font-size: 12px; color: var(--graphite); line-height: 1.6; }
@media (max-width: 1100px) { .editor-grid { grid-template-columns: 1fr; } .lore-head { grid-template-columns: 1fr 100px auto auto auto; } }
</style>

