<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { settingsApi, type LlmKeyStatus } from '@/api/settings'

const status = ref<LlmKeyStatus | null>(null)
const loading = ref(false)
const saving = ref(false)
const newKey = ref('')

async function load() {
  loading.value = true
  try { status.value = await settingsApi.llmKeyStatus() }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : 'Key 状态加载失败') }
  finally { loading.value = false }
}

async function saveKey() {
  const key = newKey.value.trim()
  if (!key) { ElMessage.warning('请输入要保存的 API Key'); return }
  saving.value = true
  try {
    status.value = await settingsApi.updateLlmKey(key)
    newKey.value = ''
    ElMessage.success('全局 API Key 已保存，对所有未单独配置 Key 的 NPC 生效')
  }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '保存失败') }
  finally { saving.value = false }
}

async function clearKey() {
  try {
    await ElMessageBox.confirm('将清除控制台保存的全局 API Key。清除后未单独配置 Key 的 NPC 将回退到环境变量或 appsettings 配置。', '清除全局 Key', { type: 'warning', confirmButtonText: '确认清除' })
  } catch { return }
  saving.value = true
  try {
    status.value = await settingsApi.clearLlmKey()
    ElMessage.success('全局 API Key 已清除')
  }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '清除失败') }
  finally { saving.value = false }
}

onMounted(load)
</script>

<template>
  <PageHeader title="模型 Key 管理" description="控制台集中保存的全局 LLM API Key，对所有未单独配置 Key 的 NPC 生效；密钥永不明文回显。">
    <el-button :loading="loading" @click="load">刷新状态</el-button>
  </PageHeader>

  <div class="panel" v-loading="loading">
    <div class="panel-body">
      <div class="status-grid">
        <div class="status-card" :class="{ active: status?.hasConsoleKey }">
          <div class="status-label">控制台全局 Key</div>
          <div class="status-value" v-if="status?.hasConsoleKey">已配置 {{ status.maskedTail }}</div>
          <div class="status-value muted" v-else>未配置</div>
          <div class="status-note">保存在 Server 的 data/system-settings.json（不入 Git）</div>
        </div>
        <div class="status-card" :class="{ active: status?.envConfigured }">
          <div class="status-label">环境变量 AIBOT_LLM_KEY</div>
          <div class="status-value" v-if="status?.envConfigured">已配置</div>
          <div class="status-value muted" v-else>未配置</div>
          <div class="status-note">由部署环境注入，控制台不可修改</div>
        </div>
      </div>

      <el-alert type="info" show-icon :closable="false" class="priority-tip"
        title="生效优先级：NPC 单独配置 > 控制台全局 Key > 环境变量 > appsettings"
        description="公共部署务必设置 AIBOT_ADMIN_TOKEN 保护管理 API，否则任何能访问 Server 的人都可以修改此 Key。" />

      <el-form label-position="top" class="key-form">
        <el-form-item label="设置新的全局 API Key">
          <el-input v-model="newKey" type="password" show-password placeholder="粘贴新的 API Key（保存后不可回显）" />
        </el-form-item>
        <div class="key-actions">
          <el-button type="primary" :loading="saving" @click="saveKey">保存 Key</el-button>
          <el-button type="danger" plain :disabled="!status?.hasConsoleKey || saving" @click="clearKey">清除 Key</el-button>
        </div>
      </el-form>
    </div>
  </div>
</template>

<style scoped>
.status-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-bottom: 18px; }
.status-card { padding: 16px; border: 1px solid #e6ebf2; border-radius: 10px; background: #f7f9fc; }
.status-card.active { border-color: #b7d7ff; background: #f0f7ff; }
.status-label { font-size: 13px; font-weight: 600; color: #65738a; }
.status-value { font-size: 20px; font-weight: 700; margin: 6px 0 4px; }
.status-value.muted { color: #98a2b3; }
.status-note { font-size: 12px; color: #98a2b3; }
.priority-tip { margin-bottom: 18px; }
.key-form { max-width: 520px; }
.key-actions { display: flex; gap: 10px; }
@media (max-width: 900px) { .status-grid { grid-template-columns: 1fr; } }
</style>
