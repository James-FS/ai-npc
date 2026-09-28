<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { settingsApi, type LlmKeyStatus, type ModelConnectionInput, type ModelConnectionState } from '@/api/settings'

defineProps<{ keyStatus: LlmKeyStatus | null }>()
const emit = defineEmits<{ (event: 'changed'): void }>()
const state = ref<ModelConnectionState | null>(null)
const loading = ref(false)
const saving = ref(false)
const testing = ref('')
const dialog = ref(false)
const creating = ref(false)
const editingHasKey = ref(false)
const form = ref<ModelConnectionInput>(blank())
const testResults = ref<Record<string, string>>({})
const editingNpcSummary = ref<string | null>(null)

function npcKey(gameId: string, npcId: string) { return `${gameId}/${npcId}` }
function npcSummaryConnection(gameId: string, npcId: string) {
  return state.value?.bindings.npcSummary[npcKey(gameId, npcId)] || ''
}
function connectionName(id: string) {
  return state.value?.connections.find(item => item.id === id)?.name || '已分配的连接'
}
function assignmentCount(id: string) {
  const bindings = state.value?.bindings
  if (!bindings) return 0
  return [bindings.globalMain, ...Object.values(bindings.npcMain), ...Object.values(bindings.npcSummary),
    ...Object.values(bindings.gameSummary)].filter(value => value === id).length
}

function blank(): ModelConnectionInput {
  return { id: '', name: '', baseUrl: '', apiFormat: 'openai_chat_completions', model: '', apiKey: '' }
}
async function load() {
  loading.value = true
  try { state.value = await settingsApi.modelConnections() }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '连接配置加载失败') }
  finally { loading.value = false }
}
function openCreate() { creating.value = true; editingHasKey.value = false; form.value = blank(); dialog.value = true }
function openEdit(row: any) {
  creating.value = false
  editingHasKey.value = row.hasApiKey
  form.value = { id: row.id, name: row.name, baseUrl: row.baseUrl,
    apiFormat: 'openai_chat_completions', model: row.model, apiKey: '' }
  dialog.value = true
}
async function save() {
  if (!form.value.name.trim() || !form.value.baseUrl.trim() || !form.value.model.trim()
    || ((!editingHasKey.value) && !form.value.apiKey?.trim())) {
    ElMessage.warning('请填写名称、Base URL、模型 ID 和 API Key')
    return
  }
  saving.value = true
  try {
    state.value = creating.value ? await settingsApi.createModelConnection(form.value)
      : await settingsApi.saveModelConnection(form.value)
    emit('changed')
    dialog.value = false
    ElMessage.success(creating.value ? '连接已保存；请在下方分配给控制台、Game 或 NPC 后生效' : '模型连接已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '保存失败') }
  finally { saving.value = false }
}
async function remove(id: string, name: string) {
  try { await ElMessageBox.confirm(`删除连接「${name}」？使用中的连接需先解除分配。`, '删除模型连接', { type: 'warning' }) }
  catch { return }
  saving.value = true
  try { state.value = await settingsApi.deleteModelConnection(id); emit('changed'); ElMessage.success('模型连接已删除') }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '删除失败') }
  finally { saving.value = false }
}
async function test(id: string) {
  testing.value = id
  try {
    const result = await settingsApi.testModelConnection(id)
    testResults.value[id] = result.ok ? `连接成功 · ${result.latencyMs ?? 0} ms` : `${result.code || '连接失败'}：${result.diagnosis || '请检查配置'}`
    if (result.ok) ElMessage.success('连接测试成功')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '连接测试失败') }
  finally { testing.value = '' }
}
async function bind(scope: 'global_main' | 'npc_main' | 'npc_summary' | 'game_summary', gameId: string | null, npcId: string | null, connectionId: string) {
  saving.value = true
  try {
    state.value = await settingsApi.bindModelConnection(scope, gameId, npcId, connectionId || null)
    emit('changed')
    ElMessage.success('模型连接分配已保存；下一次请求生效')
    return true
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '分配失败'); return false }
  finally { saving.value = false }
}
async function saveNpcSummary(gameId: string, npcId: string, connectionId: string) {
  if (await bind('npc_summary', gameId, npcId, connectionId)) editingNpcSummary.value = null
}
onMounted(load)
</script>

<template>
  <div class="panel section-gap" v-loading="loading">
    <div class="panel-head"><h3>模型连接配置</h3><div class="head-actions"><el-button @click="load">刷新</el-button><el-button type="primary" @click="openCreate">新增连接</el-button></div></div>
    <div class="panel-body">
      <p class="explain">填写连接名称、Base URL、模型 ID 和 API Key。当前使用 OpenAI 兼容 Chat Completions 格式。NPC 选用连接后，整套配置一起生效；未分配时继续使用下方原有配置。</p>
      <el-table :data="state?.connections || []" empty-text="暂无连接配置，请先新增">
        <el-table-column label="名称" min-width="170"><template #default="{ row }"><strong>{{ row.name }}</strong><div class="subtle">{{ assignmentCount(row.id) ? `已分配 ${assignmentCount(row.id)} 处` : '尚未分配，不会生效' }}</div></template></el-table-column>
        <el-table-column label="连接" min-width="270"><template #default="{ row }"><div class="mono">{{ row.baseUrl }}</div><div class="subtle">OpenAI 兼容 Chat Completions</div></template></el-table-column>
        <el-table-column label="模型 ID" min-width="170"><template #default="{ row }"><div class="mono">{{ row.model }}</div></template></el-table-column>
        <el-table-column label="API Key" min-width="150"><template #default="{ row }"><span class="mono">{{ row.maskedKey || '未配置' }}</span></template></el-table-column>
        <el-table-column label="操作" min-width="230"><template #default="{ row }"><el-button size="small" @click="openEdit(row)">编辑</el-button><el-button size="small" :loading="testing === row.id" @click="test(row.id)">测试</el-button><el-button size="small" type="danger" plain :disabled="saving" @click="remove(row.id, row.name)">删除</el-button><div v-if="testResults[row.id]" class="subtle">{{ testResults[row.id] }}</div></template></el-table-column>
      </el-table>

      <h4 class="subhead">连接分配</h4>
      <p class="explain">先选控制台默认连接，所有未单独分配的 NPC 对话都会使用它。需要例外时，在 NPC 行另选连接。Game 摘要连接仅供继承 Game 记忆设置且未指定摘要模型的 NPC 使用。</p>
      <div class="default-connection"><strong>控制台默认模型</strong><el-select :model-value="state?.bindings.globalMain || ''" :disabled="saving" @change="bind('global_main', null, null, String($event))"><el-option label="未选择（沿用旧配置）" value="" /><el-option v-for="item in state?.connections || []" :key="item.id" :label="`${item.name} · ${item.model}${item.hasApiKey ? '' : ' · 缺少 Key'}`" :value="item.id" :disabled="!item.hasApiKey" /></el-select></div>
      <el-table :data="keyStatus?.games || []" empty-text="暂无 Game">
        <el-table-column prop="gameId" label="Game" min-width="170" />
        <el-table-column label="Game 摘要模型" min-width="300"><template #default="{ row }"><el-select :model-value="state?.bindings.gameSummary[row.gameId] || ''" :disabled="saving" @change="bind('game_summary', row.gameId, null, String($event))"><el-option label="沿用原记忆策略" value="" /><el-option v-for="item in state?.connections || []" :key="item.id" :label="`${item.name} · ${item.model}${item.hasApiKey ? '' : ' · 缺少 Key'}`" :value="item.id" :disabled="!item.hasApiKey" /></el-select></template></el-table-column>
      </el-table>
      <el-table :data="keyStatus?.npcs || []" empty-text="暂无 NPC">
        <el-table-column label="NPC" min-width="180"><template #default="{ row }"><strong>{{ row.displayName }}</strong><div class="subtle">{{ row.gameId }} / {{ row.npcId }}</div></template></el-table-column>
        <el-table-column label="对话主模型" min-width="300"><template #default="{ row }"><el-select :model-value="state?.bindings.npcMain[`${row.gameId}/${row.npcId}`] || ''" :disabled="saving" @change="bind('npc_main', row.gameId, row.npcId, String($event))"><el-option :label="state?.bindings.globalMain ? '沿用控制台默认连接' : '沿用旧配置'" value="" /><el-option v-for="item in state?.connections || []" :key="item.id" :label="`${item.name} · ${item.model}${item.hasApiKey ? '' : ' · 缺少 Key'}`" :value="item.id" :disabled="!item.hasApiKey" /></el-select></template></el-table-column>
        <el-table-column label="记忆摘要模型" min-width="300">
          <template #default="{ row }">
            <div v-if="editingNpcSummary === npcKey(row.gameId, row.npcId)" class="summary-editor">
              <el-select :model-value="npcSummaryConnection(row.gameId, row.npcId)" :disabled="saving" placeholder="选择专用连接" @change="saveNpcSummary(row.gameId, row.npcId, String($event))">
                <el-option v-for="item in state?.connections || []" :key="item.id" :label="`${item.name} · ${item.model}${item.hasApiKey ? '' : ' · 缺少 Key'}`" :value="item.id" :disabled="!item.hasApiKey" />
              </el-select>
              <el-button size="small" :disabled="saving" @click="editingNpcSummary = null">取消</el-button>
            </div>
            <div v-else class="summary-assignment">
              <span>{{ npcSummaryConnection(row.gameId, row.npcId) ? connectionName(npcSummaryConnection(row.gameId, row.npcId)) : '沿用默认' }}</span>
              <el-button size="small" :disabled="saving" @click="editingNpcSummary = npcKey(row.gameId, row.npcId)">{{ npcSummaryConnection(row.gameId, row.npcId) ? '修改' : '单独设置' }}</el-button>
              <el-button v-if="npcSummaryConnection(row.gameId, row.npcId)" size="small" :disabled="saving" @click="saveNpcSummary(row.gameId, row.npcId, '')">恢复默认</el-button>
            </div>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>

  <el-dialog v-model="dialog" :title="creating ? '新增模型连接' : `编辑连接：${form.name}`" width="580px">
    <el-form label-position="top">
      <el-form-item label="名称"><el-input v-model="form.name" placeholder="例如 主对话模型" /></el-form-item>
      <el-form-item label="Base URL"><el-input v-model="form.baseUrl" placeholder="例如 https://api.example.com/v1" /><div class="subtle">填写接口前缀；Server 自动追加 /chat/completions。</div></el-form-item>
      <el-form-item label="模型 ID"><el-input v-model="form.model" placeholder="接口要求的模型名称" /></el-form-item>
      <el-form-item :label="editingHasKey ? 'API Key（留空保留原值）' : 'API Key'"><el-input v-model="form.apiKey" type="password" show-password autocomplete="new-password" :placeholder="editingHasKey ? '已有 Key 不会回显；留空保留原值' : '填写服务商提供的 API Key'" /></el-form-item>
    </el-form>
    <template #footer><el-button @click="dialog = false">取消</el-button><el-button type="primary" :loading="saving" @click="save">保存连接</el-button></template>
  </el-dialog>
</template>

<style scoped>
.section-gap { margin-bottom: 18px; }
.head-actions { display: flex; gap: 8px; }
.explain, .subtle { color: var(--graphite); font-size: 12px; line-height: 1.5; }
.explain { margin: 0 0 16px; }
.mono { font-family: var(--font-mono); overflow-wrap: anywhere; }
.subhead { margin: 24px 0 10px; font-size: 14px; }
.default-connection { display: flex; align-items: center; gap: 16px; margin-bottom: 18px; }
.default-connection strong { flex: 0 0 130px; }
.default-connection .el-select { max-width: 450px; }
.summary-assignment, .summary-editor { display: flex; align-items: center; gap: 8px; }
.summary-assignment .el-button { margin-left: 0; }
.summary-editor .el-select { min-width: 180px; }
.el-select { width: 100%; }
</style>
