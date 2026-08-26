<script setup lang="ts">
import { onBeforeUnmount, onMounted, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import MemoryPolicyForm from '@/components/memory/MemoryPolicyForm.vue'
import EffectivePolicyPanel from '@/components/memory/EffectivePolicyPanel.vue'
import { useAppStore } from '@/stores/app'
import { useNpcMemoryPolicyStore } from '@/stores/npcMemoryPolicy'

const app = useAppStore()
const route = useRoute()
const store = useNpcMemoryPolicyStore()
const { settings, effective } = storeToRefs(store)
let previewTimer: number | undefined
let suppressPreview = false

function npcId() { return String(route.params.id || app.currentNpcId || '') }

async function load() {
  if (!npcId() || npcId() === 'none') return
  suppressPreview = true
  try { await store.load(app.gameId, npcId()) } catch (error) { ElMessage.error(error instanceof Error ? error.message : 'NPC 策略加载失败') }
  finally { window.setTimeout(() => { suppressPreview = false }, 0) }
}

async function preview() {
  try { await store.preview(app.gameId, npcId()) } catch (error) { ElMessage.error(error instanceof Error ? error.message : '最终策略预览失败') }
}

async function save() {
  try {
    if (settings.value?.memoryScope === 'player_npc') {
      await ElMessageBox.confirm('该 NPC 将按 playerId 跨 Session 保存长期记忆，请确认调用端提供稳定 playerId。', '确认覆盖', { type: 'warning' })
    }
    await store.save(app.gameId, npcId())
    ElMessage.success('NPC 记忆覆盖已保存')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '保存失败')
  }
}

watch([() => app.gameId, () => route.params.id], load)
watch(settings, () => {
  if (suppressPreview || !settings.value) return
  window.clearTimeout(previewTimer)
  previewTimer = window.setTimeout(preview, 400)
}, { deep: true })
onMounted(load)
onBeforeUnmount(() => window.clearTimeout(previewTimer))
</script>

<template>
  <PageHeader title="NPC 记忆覆盖" :description="`为 ${npcId()} 配置按字段覆盖；关闭覆盖开关即可恢复 Game 继承。`">
    <el-button :loading="store.loading" @click="load">重置未保存修改</el-button>
    <el-button type="primary" :loading="store.saving" :disabled="!settings" @click="save">保存 NPC 覆盖</el-button>
  </PageHeader>

  <div class="two-col" v-loading="store.loading">
    <div class="panel">
      <div class="panel-head"><h3>{{ npcId() }} / memory</h3><el-tag :type="settings?.inheritGameDefaults ? 'success' : 'warning'">{{ settings?.inheritGameDefaults ? '继承 Game' : '从 Core 开始' }}</el-tag></div>
      <div v-if="settings" class="panel-body">
        <div class="inherit-line"><div><strong>继承 Game 默认策略</strong><span>关闭后，未覆盖字段将从 Core 默认值开始解析。</span></div><el-switch v-model="settings.inheritGameDefaults" /></div>
        <MemoryPolicyForm v-model="settings" mode="npc" :effective="effective?.policy" :sources="effective?.sources" :limits="effective?.limits" />
      </div>
      <div v-else class="empty-state">请选择有效 NPC</div>
    </div>
    <div><EffectivePolicyPanel :value="effective" /><div class="hint-box section-gap">表单修改后约 400ms 自动预览。来源为 <b>game</b> 表示继承，<b>npc</b> 表示当前 NPC 显式覆盖，<b>server-limit:*</b> 表示被安全边界修正。</div></div>
  </div>
</template>

<style scoped>
.inherit-line { display: flex; align-items: center; justify-content: space-between; padding: 16px 18px; margin-bottom: 18px; border: 1px solid #cfe0ff; background: #f3f7ff; border-radius: 12px; }
.inherit-line strong, .inherit-line span { display: block; }
.inherit-line strong { font-size: 13px; }
.inherit-line span { margin-top: 5px; color: #70809a; font-size: 11px; }
</style>
