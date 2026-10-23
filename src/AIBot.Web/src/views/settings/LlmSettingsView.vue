<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import ModelConnectionsPanel from '@/components/ModelConnectionsPanel.vue'
import { settingsApi, type LlmKeyStatus } from '@/api/settings'

const status = ref<LlmKeyStatus | null>(null)
const loading = ref(false)
async function load() {
  loading.value = true
  try { status.value = await settingsApi.llmKeyStatus() }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '模型连接状态加载失败') }
  finally { loading.value = false }
}
onMounted(load)
</script>

<template>
  <PageHeader title="模型连接" description="集中维护 Server 的模型地址、模型 ID 与 Key，并为对话和摘要任务分配连接。">
    <el-button :loading="loading" @click="load">刷新</el-button>
  </PageHeader>
  <div v-loading="loading">
    <ModelConnectionsPanel :key-status="status" @changed="load" />
    <el-alert type="info" show-icon :closable="false" class="section-gap"
      title="NPC 与 Game 配置只保留模型连接分配和生成参数"
      description="启动 Server 时会把旧配置中的地址、模型 ID 与 Key 幂等迁移到连接库；API Key 不会回显。"
    />
  </div>
</template>

<style scoped>
.section-gap { margin-top: 18px; }
</style>
