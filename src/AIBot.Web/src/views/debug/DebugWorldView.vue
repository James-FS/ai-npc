<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'
import type { DebugWorldConfig } from '@/types/debug'

import { useUnsaved, confirmDiscard } from '@/utils/unsaved'
const app = useAppStore()
const world = ref<DebugWorldConfig | null>(null)
const { dirty, markSaved } = useUnsaved(world)
const loading = ref(false)
const saving = ref(false)
let loadSeq = 0, saveSeq = 0, owner = ''
const clone = <T,>(x: T): T => JSON.parse(JSON.stringify(x)) as T

async function reset() { if (await confirmDiscard()) await load() }
async function load() {
  const seq = ++loadSeq, game = app.gameId
  owner = game; world.value = null
  saving.value = false
  loading.value = true
  try { const result = await debugApi.world(game); if (seq === loadSeq && game === app.gameId) { world.value = result; world.value.extraRules ||= []; markSaved() } }
  catch (error) { if (seq === loadSeq && game === app.gameId) ElMessage.error(error instanceof Error ? error.message : '世界观加载失败') }
  finally { if (seq === loadSeq) loading.value = false }
}
async function save() {
  if (!world.value || owner !== app.gameId) return
  const game = app.gameId, epoch = loadSeq, payload = clone(world.value), seq = ++saveSeq
  saving.value = true
  try { await debugApi.saveWorld(game, payload); if (seq === saveSeq && epoch === loadSeq && owner === app.gameId) { markSaved(payload); ElMessage.success('世界观已保存') } }
  catch (error) { if (seq === saveSeq && epoch === loadSeq && owner === app.gameId) ElMessage.error(error instanceof Error ? error.message : '世界观保存失败') }
  finally { if (seq === saveSeq && epoch === loadSeq) saving.value = false }
}
function addRule() { world.value?.extraRules.push('') }
function removeRule(index: number) { world.value?.extraRules.splice(index, 1) }
// 保存栏状态由现有加载/数据/脏/保存标记派生；未加载或加载失败不得显示“已保存”
const statusText = computed(() => saving.value ? '保存中' : loading.value ? '加载中' : !world.value ? '未加载' : dirty.value ? '未保存修改' : '已保存')
watch(() => [app.serverBase, app.gameId], load)
onMounted(load)
onBeforeUnmount(() => { loadSeq++; saveSeq++ })
</script>

<template>
  <fieldset class="editor-fields" :disabled="saving">
  <PageHeader title="世界观调试" description="编辑当前 Game 共享的世界描述和规则，保存后所有 NPC 的 Prompt 预览都会使用最新内容。"><el-button @click="reset">刷新</el-button></PageHeader>
  <div v-loading="loading" class="two-col" v-if="world">
    <div class="panel panel-body"><el-form label-position="top"><el-form-item label="World ID"><el-input v-model="world.worldId" /></el-form-item><el-form-item label="世界描述"><el-input v-model="world.description" type="textarea" :rows="14" placeholder="描述当前游戏世界、时代和基本设定" /></el-form-item></el-form></div>
    <div class="panel panel-body"><div class="section-heading"><h3>额外规则</h3><el-button size="small" :icon="Plus" @click="addRule">添加规则</el-button></div><div v-for="(_, index) in world.extraRules" :key="index" class="rule-row"><el-input v-model="world.extraRules[index]" type="textarea" :autosize="{ minRows: 2, maxRows: 6 }" :placeholder="`规则 ${index + 1}`" /><el-button link type="danger" @click="removeRule(index)">删除</el-button></div><div v-if="!world.extraRules.length" class="empty-state">暂无额外规则</div></div>
  </div>
  <div v-else class="panel empty-state" v-loading="loading">世界观尚未加载，或当前 Game 没有可编辑的世界观。<div class="empty-actions"><el-button :loading="loading" @click="reset">重新加载</el-button></div></div>
  </fieldset>
  <div class="save-bar"><span>{{ app.gameId }} · {{ statusText }}</span><el-button :disabled="!dirty || saving || loading" @click="reset">撤销未保存修改</el-button><el-button type="primary" :loading="saving" :disabled="!world || !dirty || saving || loading" @click="save">保存</el-button></div>
</template>

<style scoped>
.section-heading { display: flex; align-items: center; justify-content: space-between; margin-bottom: 14px; }
.section-heading h3 { margin: 0; font-size: 15px; }
.rule-row { display: flex; gap: 8px; margin-bottom: 12px; align-items: flex-start; }
.rule-row .el-input { flex: 1; }
.empty-actions { margin-top: 12px; }
</style>

