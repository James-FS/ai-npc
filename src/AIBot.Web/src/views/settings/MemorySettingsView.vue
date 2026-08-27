<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { memoryApi } from '@/api/memory'
import { useAppStore } from '@/stores/app'
import { useMemoryLimitsStore } from '@/stores/memoryLimits'

const app = useAppStore()
const store = useMemoryLimitsStore()
const retentionScopeLabel = computed(() => store.retention.clearsRelatedSessions
  ? '长期记忆 + 关联 Session'
  : store.retention.scope)

async function load() {
  try { await store.load() } catch (error) { ElMessage.error(error instanceof Error ? error.message : '系统边界加载失败') }
}

async function retryFailed() {
  try {
    const result = await memoryApi.retrySummaryQueue()
    ElMessage.success(result.retried > 0 ? `已重新排队 ${result.retried} 个失败任务` : '当前没有可重试的失败任务')
    await load()
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '摘要重试失败') }
}

onMounted(load)
</script>

<template>
  <PageHeader title="系统记忆边界" description="这些边界由 Server 配置控制，控制台只读展示，Game 与 NPC 策略无法突破。">
    <el-button :loading="store.loading" @click="load">刷新状态</el-button>
  </PageHeader>

  <div class="metric-grid" v-loading="store.loading">
    <div class="metric-card"><div class="metric-label">最大短期轮数</div><div class="metric-value">{{ store.limits?.maxShortTermTurns ?? '—' }}</div><div class="metric-note">Server 安全上限</div></div>
    <div class="metric-card"><div class="metric-label">最大摘要阈值</div><div class="metric-value">{{ store.limits?.maxSummaryThreshold ?? '—' }}</div><div class="metric-note">消息/事件触发边界</div></div>
    <div class="metric-card"><div class="metric-label">最大事实数</div><div class="metric-value">{{ store.limits?.maxFacts ?? '—' }}</div><div class="metric-note">每位玩家 × NPC</div></div>
    <div class="metric-card"><div class="metric-label">长期记忆保留</div><div class="metric-value">{{ store.retention.retentionDays }}<small> 天</small></div><div class="metric-note">{{ retentionScopeLabel }}</div></div>
  </div>

  <div class="two-col section-gap">
    <div class="panel">
      <div class="panel-head"><h3>能力与队列状态</h3></div>
      <div class="panel-body settings-list">
        <div><span>后台摘要能力</span><el-tag :type="store.limits?.allowBackgroundSummarization ? 'success' : 'info'">{{ store.limits?.allowBackgroundSummarization ? '允许' : '禁用' }}</el-tag></div>
        <div><span>支持的摘要触发器</span><span><el-tag v-for="item in store.limits?.supportedSummaryTriggers" :key="item" class="tag-gap" effect="plain">{{ item }}</el-tag></span></div>
        <div><span>支持的记忆范围</span><span><el-tag v-for="item in store.limits?.supportedMemoryScopes" :key="item" class="tag-gap" effect="plain">{{ item }}</el-tag></span></div>
        <div><span>摘要队列等待任务</span><strong>{{ store.queue.pending }}</strong></div>
        <div><span>摘要当前失败</span><strong :class="{ danger: store.queue.failedCurrent > 0 }">{{ store.queue.failedCurrent }}</strong></div>
        <div><span>摘要失败累计</span><strong :class="{ danger: store.queue.failedTotal > 0 }">{{ store.queue.failedTotal }}</strong><el-button size="small" type="warning" plain :disabled="store.queue.failedCurrent < 1" @click="retryFailed">重试失败任务</el-button></div>
      </div>
    </div>

    <div class="panel">
      <div class="panel-head"><h3>控制台连接设置</h3></div>
      <div class="panel-body">
        <el-form label-position="top">
          <el-form-item label="Server Base URL"><el-input v-model="app.serverBase" placeholder="留空表示当前站点，例如 http://127.0.0.1:5000" /></el-form-item>
          <el-form-item label="管理 Bearer Token"><el-input v-model="app.adminToken" type="password" show-password placeholder="本地未启用鉴权时可留空" /></el-form-item>
          <el-form-item label="审计操作人"><el-input v-model="app.auditActor" maxlength="80" /></el-form-item>
        </el-form>
        <div class="hint-box">连接信息仅保存在当前浏览器 localStorage。服务端不会通过管理 API 返回管理 Token、模型密钥或数据路径。</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.metric-value small { font-size: 13px; letter-spacing: 0; color: #738099; }
.settings-list { display: grid; }
.settings-list > div { min-height: 52px; display: flex; align-items: center; justify-content: space-between; gap: 20px; border-bottom: 1px solid #edf0f5; font-size: 13px; }
.settings-list > div:last-child { border-bottom: 0; }
.settings-list > div > span:first-child { color: #66748b; }
.tag-gap { margin-left: 6px; }
.danger { color: #c8414b; }
</style>
