<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import ModelConnectionsPanel from '@/components/ModelConnectionsPanel.vue'
import { settingsApi, type LlmKeyStatus } from '@/api/settings'

const status = ref<LlmKeyStatus | null>(null)
const panel = ref<InstanceType<typeof ModelConnectionsPanel> | null>(null)
const loading = ref(false)
// 面板正在编辑连接或专用摘要草稿时禁止刷新，避免静默丢弃草稿
const panelDraft = computed(() => panel.value?.hasDraft ?? false)
async function loadStatus() {
  try { status.value = await settingsApi.llmKeyStatus() }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '模型连接状态加载失败') }
}
async function refresh() {
  loading.value = true
  try { await Promise.all([loadStatus(), panel.value?.load()]) }
  finally { loading.value = false }
}
onMounted(refresh)
</script>

<template>
  <PageHeader title="模型连接" description="集中维护 Server 的模型地址、模型 ID 与 Key，并为对话和摘要任务分配连接。">
    <el-tooltip :content="panelDraft ? '正在编辑连接或摘要选择，完成或取消后再刷新' : '同时重新加载连接列表、绑定与生效诊断'">
      <span><el-button :loading="loading" :disabled="panelDraft" @click="refresh">刷新</el-button></span>
    </el-tooltip>
  </PageHeader>
  <div v-loading="loading">
    <ModelConnectionsPanel ref="panel" :key-status="status" @changed="loadStatus" />
    <el-alert type="info" show-icon :closable="false" class="section-gap"
      title="NPC 与 Game 配置只保留模型连接分配和生成参数"
      description="启动 Server 时会把旧配置中的地址、模型 ID 与 Key 幂等迁移到连接库；API Key 不会回显。"
    />
  </div>
</template>

<style scoped>
.section-gap { margin-top: 18px; }
</style>
