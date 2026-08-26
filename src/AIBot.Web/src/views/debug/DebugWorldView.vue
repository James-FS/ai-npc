<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { debugApi } from '@/api/debug'
import { useAppStore } from '@/stores/app'
import type { DebugWorldConfig } from '@/types/debug'

const app = useAppStore()
const world = ref<DebugWorldConfig | null>(null)
const loading = ref(false)
const saving = ref(false)

async function load() {
  loading.value = true
  try { world.value = await debugApi.world(app.gameId); world.value.extraRules ||= [] }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '世界观加载失败') }
  finally { loading.value = false }
}
async function save() {
  if (!world.value) return
  saving.value = true
  try { await debugApi.saveWorld(app.gameId, world.value); ElMessage.success('世界观已保存') }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '世界观保存失败') }
  finally { saving.value = false }
}
function addRule() { world.value?.extraRules.push('') }
function removeRule(index: number) { world.value?.extraRules.splice(index, 1) }
watch(() => app.gameId, load)
onMounted(load)
</script>

<template>
  <PageHeader title="世界观调试" description="编辑当前 Game 共享的世界描述和规则，保存后所有 NPC 的 Prompt 预览都会使用最新内容。"><el-button :loading="saving" :disabled="!world" type="primary" @click="save">保存世界观</el-button><el-button @click="load">刷新</el-button></PageHeader>
  <div v-loading="loading" class="two-col" v-if="world">
    <div class="panel panel-body"><el-form label-position="top"><el-form-item label="World ID"><el-input v-model="world.worldId" /></el-form-item><el-form-item label="世界描述"><el-input v-model="world.description" type="textarea" :rows="14" placeholder="描述当前游戏世界、时代和基本设定" /></el-form-item></el-form></div>
    <div class="panel panel-body"><div class="section-heading"><h3>额外规则</h3><el-button size="small" @click="addRule">＋ 添加规则</el-button></div><div v-for="(_, index) in world.extraRules" :key="index" class="rule-row"><el-input v-model="world.extraRules[index]" type="textarea" :rows="2" :placeholder="`规则 ${index + 1}`" /><el-button link type="danger" @click="removeRule(index)">删除</el-button></div><div v-if="!world.extraRules.length" class="empty-state">暂无额外规则</div></div>
  </div>
</template>

<style scoped>
.section-heading { display: flex; align-items: center; justify-content: space-between; margin-bottom: 14px; }
.section-heading h3 { margin: 0; font-size: 15px; }
.rule-row { display: flex; gap: 8px; margin-bottom: 12px; align-items: flex-start; }
.rule-row .el-input { flex: 1; }
</style>
