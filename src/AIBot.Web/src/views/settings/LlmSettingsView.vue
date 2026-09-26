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
  const replacing = !!status.value?.hasConsoleKey
  saving.value = true
  try {
    status.value = await settingsApi.updateLlmKey(key)
    newKey.value = ''
    ElMessage.success(replacing
      ? '全局 API Key 已替换，对所有未单独配置 Key 的 NPC 生效'
      : '全局 API Key 已添加，对所有未单独配置 Key 的 NPC 生效')
  }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '保存失败') }
  finally { saving.value = false }
}

async function clearKey() {
  try {
    await ElMessageBox.confirm('将删除控制台保存的全局 API Key。删除后未单独配置 Key 的 NPC 将回退到环境变量或 appsettings 配置；随时可在本页重新添加。', '删除全局 Key', { type: 'warning', confirmButtonText: '确认删除' })
  } catch { return }
  saving.value = true
  try {
    status.value = await settingsApi.clearLlmKey()
    ElMessage.success('全局 API Key 已删除，可随时重新添加')
  }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '删除失败') }
  finally { saving.value = false }
}

onMounted(load)
</script>

<template>
  <PageHeader title="模型 Key 管理" description="控制台集中保存的全局 LLM API Key，对所有未单独配置 Key 的 NPC 生效；页面只回显脱敏串，明文永不出后端。">
    <el-button :loading="loading" @click="load">刷新状态</el-button>
  </PageHeader>

  <div class="panel" v-loading="loading">
    <div class="panel-body">
      <div class="status-grid">
        <div class="status-card" :class="{ active: status?.hasConsoleKey }">
          <div class="status-label">控制台全局 Key</div>
          <div class="status-value mono" v-if="status?.hasConsoleKey">{{ status.maskedKey || '已配置' }}</div>
          <div class="status-value muted" v-else>未配置</div>
          <div class="status-note">保存在 Server 的 data/system-settings.json（不入 Git）</div>
        </div>
        <div class="status-card" :class="{ active: status?.envConfigured }">
          <div class="status-label">环境变量 AIBOT_LLM_KEY</div>
          <!-- 兜底：服务端未返回脱敏串时（如前后端版本不一致）显示「已配置」，避免卡片一片空白 -->
          <div class="status-value mono" v-if="status?.envConfigured">{{ status.envMaskedKey || '已配置' }}</div>
          <div class="status-value muted" v-else>未配置</div>
          <div class="status-note">由部署环境注入，控制台不可修改</div>
        </div>
      </div>

      <el-alert type="info" show-icon :closable="false" class="priority-tip"
        title="生效优先级：NPC 单独配置 > 控制台全局 Key > 环境变量 > appsettings"
        description="公共部署务必设置 AIBOT_ADMIN_TOKEN 保护管理 API，否则任何能访问 Server 的人都可以修改此 Key。" />

      <el-form label-position="top" class="key-form">
        <el-form-item :label="status?.hasConsoleKey ? '替换全局 API Key' : '添加全局 API Key'">
          <el-input v-model="newKey" type="password" show-password
            :placeholder="status?.hasConsoleKey ? '粘贴新 Key 保存即覆盖当前 Key' : '粘贴 API Key（保存后只回显脱敏串）'" />
        </el-form-item>
        <div class="key-actions">
          <el-button type="primary" :loading="saving" @click="saveKey">
            {{ status?.hasConsoleKey ? '替换 Key' : '保存 Key' }}
          </el-button>
          <el-button type="danger" plain :disabled="!status?.hasConsoleKey || saving" @click="clearKey">删除 Key</el-button>
        </div>
        <div class="mask-hint">
          脱敏显示规则：前 7 位 + <code>*****</code> + 末 4 位（如 <code>sk-ca1a2*****bd31</code>），星号数量固定、不暴露密钥长度；过短的 Key 显示 <code>***</code>。删除后可随时重新添加。
        </div>
      </el-form>
    </div>
  </div>
</template>

<style scoped>
.status-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-bottom: 18px; }
.status-card { padding: 16px; border: 1px solid #e6ebf2; border-radius: var(--radius-inset); background: var(--surface-inset); }
.status-card.active { border-color: #8b95a8; }
.status-label { font-size: 13px; font-weight: 600; color: var(--graphite); }
.status-value { font-size: 20px; font-weight: 700; margin: 6px 0 4px; }
.status-value.mono { font-family: var(--font-mono); font-size: 17px; letter-spacing: 0.02em; word-break: break-all; }
.status-value.muted { color: var(--graphite); }
.status-note { font-size: 12px; color: var(--graphite); }
.priority-tip { margin-bottom: 18px; }
.key-form { max-width: 520px; }
.key-actions { display: flex; gap: 10px; }
.mask-hint { margin-top: 14px; font-size: 12px; line-height: 1.7; color: var(--graphite); }
.mask-hint code { font-family: var(--font-mono); background: var(--surface-track); padding: 1px 5px; border-radius: var(--radius-chip); }
@media (max-width: 900px) { .status-grid { grid-template-columns: 1fr; } }
</style>
