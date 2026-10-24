<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import MemoryPolicyForm from '@/components/memory/MemoryPolicyForm.vue'
import EffectivePolicyPanel from '@/components/memory/EffectivePolicyPanel.vue'
import { useAppStore } from '@/stores/app'
import { useNpcMemoryPolicyStore } from '@/stores/npcMemoryPolicy'

import { useUnsaved, confirmDiscard } from '@/utils/unsaved'
const app = useAppStore()
const route = useRoute()
const store = useNpcMemoryPolicyStore()
const { settings, effective } = storeToRefs(store)
const { dirty, markSaved } = useUnsaved(settings)
let previewTimer: number | undefined
let suppressPreview = false
let suppressionEpoch = -1

function npcId() { return String(route.params.id || app.currentNpcId || '') }

async function reset() { if (await confirmDiscard()) await load() }
async function load() {
  window.clearTimeout(previewTimer)
  if (!npcId() || npcId() === 'none') { store.invalidate(); suppressPreview = false; return }
  suppressPreview = true
  const game = app.gameId, npc = npcId(), promise = store.load(game, npc)
  const epoch = store.contextEpoch; suppressionEpoch = epoch
  try { await promise; if (epoch === store.contextEpoch) markSaved() } catch (error) { if (epoch === store.contextEpoch) ElMessage.error(error instanceof Error ? error.message : 'NPC 策略加载失败') }
  finally { window.setTimeout(() => { if (suppressionEpoch === epoch && epoch === store.contextEpoch) suppressPreview = false }, 0) }
}

async function preview() {
  const game = app.gameId, npc = npcId()
  try { await store.preview(game, npc) } catch (error) { if (game === app.gameId && npc === npcId()) ElMessage.error(error instanceof Error ? error.message : '最终策略预览失败') }
}

async function save() {
  const game = app.gameId, npc = npcId()
  const epoch = store.contextEpoch, payload = settings.value && JSON.parse(JSON.stringify(settings.value))
  if (!payload) return
  try {
    if (payload.memoryScope === 'player_npc') {
      await ElMessageBox.confirm('该 NPC 将按 playerId 跨 Session 保存长期记忆，请确认调用端提供稳定 playerId。', '确认覆盖', { type: 'warning' })
    }
    if (game !== app.gameId || npc !== npcId() || epoch !== store.contextEpoch) return
    if (await store.save(game, npc, payload, epoch)) { markSaved(payload); ElMessage.success('NPC 记忆覆盖已保存') }
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    if (game === app.gameId && npc === npcId() && epoch === store.contextEpoch) ElMessage.error(error instanceof Error ? error.message : '保存失败')
  }
}

watch([() => app.serverBase, () => app.gameId, () => route.params.id], load)
watch(settings, () => {
  if (suppressPreview || !settings.value) return
  window.clearTimeout(previewTimer)
  previewTimer = window.setTimeout(preview, 400)
}, { deep: true })
onMounted(load)
onBeforeUnmount(() => { window.clearTimeout(previewTimer); store.invalidate() })
// 保存栏状态由现有加载/数据/脏/保存标记派生；未加载或加载失败不得显示“已保存”
const statusText = computed(() => store.saving ? '保存中' : store.loading ? '加载中' : !settings.value ? '未加载' : dirty.value ? '未保存修改' : '已保存')
</script>

<template>
  <fieldset class="editor-fields" :disabled="store.saving">
  <PageHeader title="NPC 记忆覆盖" :description="`为 ${npcId()} 配置按字段覆盖；关闭覆盖开关即可恢复 Game 继承。`">
    <el-button :loading="store.loading" @click="reset">撤销未保存修改</el-button>

  </PageHeader>

  <div class="two-col" v-loading="store.loading">
    <div class="panel">
      <div class="panel-head"><h3>{{ npcId() }} / memory</h3><el-tag :type="settings?.inheritGameDefaults ? 'success' : 'warning'">{{ settings?.inheritGameDefaults ? '继承 Game' : '从 Core 开始' }}</el-tag></div>
      <div v-if="settings" class="panel-body">
        <div class="inherit-line"><div><strong>继承 Game 默认策略</strong><span>关闭后，未覆盖字段将从 Core 默认值开始解析。</span></div><el-switch v-model="settings.inheritGameDefaults" /></div>
        <MemoryPolicyForm v-model="settings" mode="npc" :effective="effective?.policy" :sources="effective?.sources" :limits="effective?.limits" />
      </div>
      <div v-else class="empty-state">尚未加载 NPC 覆盖，或未选择有效 NPC。<el-button :loading="store.loading" @click="reset">重新加载</el-button></div>
    </div>
    <div><EffectivePolicyPanel :value="effective" /><div class="hint-box section-gap">表单修改后约 400ms 自动预览。来源为 <b>game</b> 表示继承，<b>npc</b> 表示当前 NPC 显式覆盖，<b>server-limit:*</b> 表示被安全边界修正。</div></div>
  </div>
  </fieldset>
  <div class="save-bar"><span>{{ app.gameId }} · {{ statusText }}</span><el-button :disabled="!dirty || store.saving || store.loading" @click="reset">撤销未保存修改</el-button><el-button type="primary" :loading="store.saving" :disabled="!settings || !dirty || store.saving || store.loading" @click="save">保存</el-button></div>
</template>

<style scoped>
.inherit-line { display: flex; align-items: center; justify-content: space-between; padding: 16px 18px; margin-bottom: 18px; border: 1px solid var(--line); background: var(--surface-inset); border-radius: var(--radius-inset); }
.inherit-line strong, .inherit-line span { display: block; }
.inherit-line strong { font-size: 13px; }
.inherit-line span { margin-top: 5px; color: var(--graphite); font-size: 11px; }
</style>
