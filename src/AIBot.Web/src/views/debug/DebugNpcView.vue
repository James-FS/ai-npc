<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'
import type { DebugAgentConfig, LoreBlock } from '@/types/debug'

const app = useAppStore()
const config = ref<DebugAgentConfig | null>(null)
const loading = ref(false)
const saving = ref(false)
const testResult = ref<Record<string, unknown> | null>(null)
const testing = ref(false)
const npcId = computed(() => app.currentNpcId)

function blankLore(): LoreBlock { return { title: '', content: '', unlockStage: 0, isSecret: false, enabled: true } }
function normalize(value: DebugAgentConfig): DebugAgentConfig {
  return { ...value, loreBlocks: value.loreBlocks || [], enabledToolIds: value.enabledToolIds || [], fallbackReplies: value.fallbackReplies || [], model: value.model || { baseUrl: '', model: '', temperature: .8, maxTokens: 500, timeoutMs: 20000 }, output: value.output || { emotions: [], actions: [] } }
}

async function load() {
  if (!npcId.value) { config.value = null; return }
  loading.value = true
  const requested = { game: app.gameId, npc: npcId.value }
  try { config.value = normalize(await debugApi.npc(app.gameId, npcId.value)) } catch (error) {
    // 切换 Game 的瞬间会用旧 npcId 发一次请求；响应回来时上下文已变即视为过期，静默丢弃
    const stale = app.gameId !== requested.game || app.currentNpcId !== requested.npc
    if (!stale) ElMessage.error(error instanceof Error ? error.message : 'NPC 配置加载失败')
  }
  finally { loading.value = false }
}

async function save() {
  if (!config.value || !npcId.value) return
  saving.value = true
  try { await debugApi.saveNpc(app.gameId, npcId.value, config.value); ElMessage.success('NPC 配置已保存'); await load() }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : 'NPC 配置保存失败') }
  finally { saving.value = false }
}

async function createNpc() {
  let id = ''
  try {
    const { value } = await ElMessageBox.prompt(
      '将按内置模板创建 NPC 配置，创建后可在当前页完善人设与模型设置。',
      '新建 NPC',
      {
        inputPattern: /^[a-zA-Z0-9_.:-]{1,64}$/,
        inputErrorMessage: 'NPC ID 仅允许字母数字与 _ . : -（1~64 位）',
        confirmButtonText: '创建',
        cancelButtonText: '取消',
      },
    )
    id = value.trim()
    await debugApi.createNpc(app.gameId, id)
    await app.loadNpcs()
    app.selectedNpcId = id
    ElMessage.success(`NPC「${id}」已创建`)
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : 'NPC 创建失败')
  }
}

async function deleteNpc() {
  if (!npcId.value) return
  try {
    await ElMessageBox.confirm(`确认删除 NPC「${npcId.value}」？配置文件将被移除。`, '删除 NPC', { type: 'error', confirmButtonText: '确认删除' })
    await debugApi.deleteNpc(app.gameId, npcId.value); await app.loadNpcs(); ElMessage.success('NPC 已删除')
  } catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : 'NPC 删除失败') }
}

async function testConnection() {
  if (!config.value || !npcId.value) return
  testing.value = true
  try { testResult.value = await debugApi.testConnection(app.gameId, npcId.value, { baseUrl: config.value.model.baseUrl, model: config.value.model.model }); ElMessage.success(testResult.value.ok ? '连接测试成功' : '连接测试完成，请查看诊断') }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '连接测试失败') }
  finally { testing.value = false }
}

function addLore() { config.value?.loreBlocks.push(blankLore()) }
function removeLore(index: number) { config.value?.loreBlocks.splice(index, 1) }
function addReply() { config.value?.fallbackReplies.push('（沉默片刻）……') }
function removeReply(index: number) { config.value?.fallbackReplies.splice(index, 1) }
function addTag(kind: 'emotions' | 'actions') { config.value?.output[kind].push(kind === 'emotions' ? 'neutral' : 'idle') }
function removeTag(kind: 'emotions' | 'actions', index: number) { config.value?.output[kind].splice(index, 1) }

watch(() => [app.gameId, app.selectedNpcId], load)
onMounted(load)
</script>

<template>
  <PageHeader title="NPC 配置调试" description="迁移原调试台的人设、剧情块、模型、兜底台词和连接测试能力；记忆策略在 NPC 覆盖页面维护。">
    <el-button @click="createNpc">＋ 新建 NPC</el-button><el-button type="danger" plain :disabled="!config" @click="deleteNpc">删除 NPC</el-button><el-button type="primary" :loading="saving" :disabled="!config" @click="save">保存配置</el-button>
  </PageHeader>
  <div v-loading="loading" class="panel" v-if="config">
    <div class="panel-body editor-grid">
      <div class="editor-column">
        <el-form label-position="top">
          <el-form-item label="NPC ID"><el-input v-model="config.npcId" disabled /></el-form-item>
          <el-form-item label="显示名称"><el-input v-model="config.displayName" /></el-form-item>
          <el-form-item label="Persona"><el-input v-model="config.persona" type="textarea" :rows="5" /></el-form-item>
          <el-form-item label="Backstory"><el-input v-model="config.backstory" type="textarea" :rows="5" /></el-form-item>
          <el-form-item label="World ID"><el-input v-model="config.worldId" /></el-form-item>
        </el-form>
      </div>
      <div class="editor-column">
        <div class="section-title">模型设置</div>
        <el-form label-position="top"><el-form-item label="Base URL"><el-input v-model="config.model.baseUrl" /></el-form-item><el-form-item label="Model"><el-input v-model="config.model.model" /></el-form-item><el-form-item label="API Key"><el-input model-value="已配置的密钥不会回显；留空表示不修改" disabled /></el-form-item><el-form-item label="Temperature"><el-input-number v-model="config.model.temperature" :min="0" :max="2" :step="0.1" /></el-form-item><el-form-item label="Max Tokens"><el-input-number v-model="config.model.maxTokens" :min="1" :max="10000" /></el-form-item><el-form-item label="Timeout (ms)"><el-input-number v-model="config.model.timeoutMs" :min="1000" :max="120000" /></el-form-item></el-form>
        <el-button :loading="testing" @click="testConnection">⚡ 测试连接</el-button>
        <pre v-if="testResult" class="code-block result-block">{{ JSON.stringify(testResult, null, 2) }}</pre>
      </div>
    </div>
    <div class="panel-body section-border"><div class="section-heading"><h3>剧情知识块</h3><el-button size="small" @click="addLore">＋ 添加</el-button></div><div v-for="(lore, index) in config.loreBlocks" :key="index" class="lore-card"><div class="lore-head"><el-input v-model="lore.title" placeholder="标题" /><el-input-number v-model="lore.unlockStage" :min="0" controls-position="right" /><el-switch v-model="lore.enabled" active-text="启用" /><el-switch v-model="lore.isSecret" active-text="秘密" /><el-button link type="danger" @click="removeLore(index)">删除</el-button></div><el-input v-model="lore.content" type="textarea" :rows="3" placeholder="知识内容" /></div><div v-if="!config.loreBlocks.length" class="empty-state">暂无剧情知识块</div></div>
    <div class="panel-body section-border"><div class="section-heading"><h3>兜底台词</h3><el-button size="small" @click="addReply">＋ 添加</el-button></div><div v-for="(_, index) in config.fallbackReplies" :key="index" class="reply-row"><el-input v-model="config.fallbackReplies[index]" /><el-button link type="danger" @click="removeReply(index)">删除</el-button></div></div>
    <div class="panel-body section-border"><div class="section-heading"><h3>输出枚举</h3></div><div class="tag-section"><b>情绪</b><el-tag v-for="(tag, index) in config.output.emotions" :key="index" closable @close="removeTag('emotions', index)">{{ tag }}</el-tag><el-button size="small" @click="addTag('emotions')">添加</el-button></div><div class="tag-section"><b>动作</b><el-tag v-for="(tag, index) in config.output.actions" :key="index" closable @close="removeTag('actions', index)">{{ tag }}</el-tag><el-button size="small" @click="addTag('actions')">添加</el-button></div></div>
  </div>
  <div v-else class="panel empty-state">当前 Game 没有可编辑的 NPC，请先创建或刷新 NPC 列表。</div>
</template>

<style scoped>
.editor-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 28px; }
.section-border { border-top: 1px solid #edf0f5; }
.section-title, .section-heading h3 { font-size: 15px; font-weight: 700; margin: 0 0 14px; }
.section-heading { display: flex; justify-content: space-between; align-items: center; }
.lore-card { padding: 12px; background: #f7f9fc; border: 1px solid #e6ebf2; border-radius: 10px; margin-bottom: 10px; }
.lore-head { display: grid; grid-template-columns: 1fr 120px auto auto auto; gap: 8px; align-items: center; margin-bottom: 8px; }
.lore-head :deep(.el-input-number) { width: 100%; }
.reply-row { display: flex; gap: 8px; margin-bottom: 8px; }
.reply-row .el-input { flex: 1; }
.tag-section { display: flex; align-items: center; gap: 8px; margin: 12px 0; flex-wrap: wrap; }
.tag-section b { width: 40px; color: #65738a; font-size: 13px; }
.result-block { margin-top: 14px; max-height: 220px; }
@media (max-width: 1100px) { .editor-grid { grid-template-columns: 1fr; } .lore-head { grid-template-columns: 1fr 100px auto auto auto; } }
</style>

