<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { MemoryPolicy, MemoryPolicyLimits, MemorySettings, ModelSettings } from '@/types/memory'

type PolicyKey = keyof MemoryPolicy

const props = defineProps<{
  modelValue: MemoryPolicy | MemorySettings
  mode: 'game' | 'npc'
  effective?: MemoryPolicy | null
  sources?: Record<string, string>
  limits?: MemoryPolicyLimits | null
}>()
const emit = defineEmits<{ 'update:modelValue': [value: MemoryPolicy | MemorySettings] }>()

const isNpc = computed(() => props.mode === 'npc')
const extensionsText = ref('{}')
const extensionsError = ref('')

const defaults: MemoryPolicy = {
  shortTermTurns: 12,
  summaryThreshold: 20,
  summaryTrigger: 'message_count',
  memoryScope: 'session',
  maxFacts: 8,
  rememberPlayerProfile: true,
  rememberPromises: true,
  rememberQuestEvents: true,
  rememberCasualChat: false,
  backgroundSummarization: false,
  summaryModel: null,
  extensions: {},
}

const numericFields: Array<{ key: PolicyKey; label: string; help: string; min: number; max: () => number }> = [
  { key: 'shortTermTurns', label: '短期上下文轮数', help: '每个会话直接保留的最近对话轮数。', min: 1, max: () => props.limits?.maxShortTermTurns ?? 50 },
  { key: 'summaryThreshold', label: '摘要触发阈值', help: '达到阈值后将待处理消息放入摘要队列；0 表示仅手动触发。', min: 0, max: () => props.limits?.maxSummaryThreshold ?? 200 },
  { key: 'maxFacts', label: '最大结构化事实数', help: '固定事实不会被后台摘要自动淘汰。', min: 1, max: () => props.limits?.maxFacts ?? 20 },
]

const booleanFields: Array<{ key: PolicyKey; label: string; help: string }> = [
  { key: 'rememberPlayerProfile', label: '记住玩家档案', help: '姓名、职业、偏好等玩家自述信息。' },
  { key: 'rememberPromises', label: '记住承诺', help: '玩家与 NPC 之间的约定和未完成承诺。' },
  { key: 'rememberQuestEvents', label: '记住任务事件', help: '对话中值得长期保留的任务相关事件。' },
  { key: 'rememberCasualChat', label: '记住日常闲聊', help: '会增加记忆量与摘要成本，通常建议关闭。' },
  { key: 'backgroundSummarization', label: '后台摘要', help: '回复完成后异步更新长期记忆，不阻塞玩家对话。' },
]

function cloneJson<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

function cloneModel() {
  // props.modelValue 可能来自 Pinia，是响应式 Proxy，structuredClone 无法直接克隆。
  return cloneJson(props.modelValue)
}

function fieldValue(key: PolicyKey) {
  const current = (props.modelValue as unknown as Record<string, unknown>)[key]
  if (current !== null && current !== undefined) return current
  return props.effective?.[key] ?? defaults[key]
}

function hasOverride(key: PolicyKey) {
  const value = (props.modelValue as unknown as Record<string, unknown>)[key]
  return value !== null && value !== undefined
}

function setField(key: PolicyKey, value: unknown) {
  const next = cloneModel() as unknown as Record<string, unknown>
  next[key] = value
  emit('update:modelValue', next as unknown as MemoryPolicy | MemorySettings)
}

function toggleOverride(key: PolicyKey, enabled: boolean) {
  setField(key, enabled ? fieldValue(key) : null)
}

function source(key: string) {
  return props.sources?.[key] || (isNpc.value ? '继承' : 'game')
}

function newSummaryModel(): ModelSettings {
  return {
    baseUrl: 'https://api.deepseek.com',
    model: 'deepseek-chat',
    temperature: 0.3,
    maxTokens: 500,
    timeoutMs: 20000,
  }
}

function summaryOverridden() {
  if (!isNpc.value) return props.modelValue.summaryModel !== null
  const npc = props.modelValue as MemorySettings
  return npc.useMainSummaryModel !== null && npc.useMainSummaryModel !== undefined
    || npc.summaryModel !== null && npc.summaryModel !== undefined
}

function toggleSummaryOverride(enabled: boolean) {
  const next = cloneModel() as MemorySettings
  if (!enabled) {
    next.summaryModel = null
    next.useMainSummaryModel = null
  } else {
    next.useMainSummaryModel = true
    next.summaryModel = null
  }
  emit('update:modelValue', next)
}

function setSummaryMode(mode: 'main' | 'dedicated') {
  const next = cloneModel() as MemorySettings
  next.useMainSummaryModel = mode === 'main'
  next.summaryModel = mode === 'dedicated' ? newSummaryModel() : null
  emit('update:modelValue', next)
}

function toggleGameSummaryModel(enabled: boolean) {
  setField('summaryModel', enabled ? newSummaryModel() : null)
}

function updateSummaryModel(key: keyof ModelSettings, value: unknown) {
  const next = cloneModel()
  const model = cloneJson(next.summaryModel ?? newSummaryModel()) as unknown as Record<string, unknown>
  model[key] = value
  next.summaryModel = model as unknown as ModelSettings
  emit('update:modelValue', next)
}

function extensionsOverridden() {
  return !isNpc.value || (props.modelValue as MemorySettings).extensions !== null && (props.modelValue as MemorySettings).extensions !== undefined
}

function toggleExtensions(enabled: boolean) {
  setField('extensions', enabled ? {} : null)
}

function updateExtensions(value: string) {
  extensionsText.value = value
  try {
    const parsed: unknown = JSON.parse(value || '{}')
    if (!parsed || Array.isArray(parsed) || typeof parsed !== 'object') throw new Error('必须是 JSON 对象')
    extensionsError.value = ''
    setField('extensions', parsed)
  } catch (error) {
    extensionsError.value = error instanceof Error ? error.message : 'JSON 格式错误'
  }
}

watch(() => props.modelValue.extensions, value => {
  const next = JSON.stringify(value ?? {}, null, 2)
  if (!extensionsError.value && extensionsText.value !== next) extensionsText.value = next
}, { immediate: true, deep: true })
</script>

<template>
  <div class="policy-form" :class="`policy-form--${mode}`">
    <section class="form-section">
      <div class="form-section-title"><div><h4>上下文与摘要</h4><p>控制短期窗口、摘要时机和长期事实容量。</p></div></div>
      <div v-for="field in numericFields" :key="field.key" class="policy-row compact-row">
        <div class="field-copy"><strong>{{ field.label }}</strong><span>{{ field.help }}</span></div>
        <el-switch v-if="isNpc" :model-value="hasOverride(field.key)" inline-prompt active-text="覆盖" inactive-text="继承" @update:model-value="toggleOverride(field.key, Boolean($event))" />
        <el-input-number :model-value="Number(fieldValue(field.key))" :min="field.min" :max="field.max()" :disabled="isNpc && !hasOverride(field.key)" controls-position="right" @update:model-value="setField(field.key, $event)" />
        <span class="source-tag">{{ source(field.key) }}</span>
      </div>
      <div class="policy-row compact-row">
        <div class="field-copy"><strong>摘要触发方式</strong><span>Server 只接受声明为支持的触发器。</span></div>
        <el-switch v-if="isNpc" :model-value="hasOverride('summaryTrigger')" inline-prompt active-text="覆盖" inactive-text="继承" @update:model-value="toggleOverride('summaryTrigger', Boolean($event))" />
        <el-select :model-value="String(fieldValue('summaryTrigger'))" :disabled="isNpc && !hasOverride('summaryTrigger')" @update:model-value="setField('summaryTrigger', $event)">
          <el-option v-for="item in limits?.supportedSummaryTriggers || ['message_count']" :key="item" :value="item" :label="item" />
        </el-select>
        <span class="source-tag">{{ source('summaryTrigger') }}</span>
      </div>
      <div class="policy-row compact-row">
        <div class="field-copy"><strong>长期记忆范围</strong><span>player_npc 可跨 session 识别同一玩家；切换前请确认迁移影响。</span></div>
        <el-switch v-if="isNpc" :model-value="hasOverride('memoryScope')" inline-prompt active-text="覆盖" inactive-text="继承" @update:model-value="toggleOverride('memoryScope', Boolean($event))" />
        <el-select :model-value="String(fieldValue('memoryScope'))" :disabled="isNpc && !hasOverride('memoryScope')" @update:model-value="setField('memoryScope', $event)">
          <el-option v-for="item in limits?.supportedMemoryScopes || ['session']" :key="item" :value="item" :label="item" />
        </el-select>
        <span class="source-tag">{{ source('memoryScope') }}</span>
      </div>
    </section>

    <section class="form-section">
      <div class="form-section-title"><div><h4>记忆内容</h4><p>决定后台摘要允许提取哪些类别的信息。</p></div></div>
      <div v-for="field in booleanFields" :key="field.key" class="policy-row compact-row boolean-row">
        <div class="field-copy"><strong>{{ field.label }}</strong><span>{{ field.help }}</span></div>
        <el-switch v-if="isNpc" :model-value="hasOverride(field.key)" inline-prompt active-text="覆盖" inactive-text="继承" @update:model-value="toggleOverride(field.key, Boolean($event))" />
        <el-switch :model-value="Boolean(fieldValue(field.key))" :disabled="isNpc && !hasOverride(field.key) || field.key === 'backgroundSummarization' && limits?.allowBackgroundSummarization === false" @update:model-value="setField(field.key, $event)" />
        <span class="source-tag">{{ source(field.key) }}</span>
      </div>
    </section>

    <section class="form-section">
      <div class="form-section-title"><div><h4>摘要模型</h4><p>默认复用 NPC 主模型；也可以为摘要任务配置独立模型。</p></div></div>
      <div v-if="isNpc" class="policy-row summary-model-row">
        <div class="field-copy"><strong>覆盖摘要模型选择</strong><span>关闭时继承 Game 策略。</span></div>
        <el-switch :model-value="summaryOverridden()" inline-prompt active-text="覆盖" inactive-text="继承" @update:model-value="toggleSummaryOverride(Boolean($event))" />
        <el-radio-group :model-value="(modelValue as MemorySettings).useMainSummaryModel === false ? 'dedicated' : 'main'" :disabled="!summaryOverridden()" @update:model-value="setSummaryMode($event as 'main' | 'dedicated')">
          <el-radio-button value="main">复用主模型</el-radio-button><el-radio-button value="dedicated">独立模型</el-radio-button>
        </el-radio-group>
        <span class="source-tag">{{ source('summaryModel') }}</span>
      </div>
      <div v-else class="policy-row summary-model-row">
        <div class="field-copy"><strong>使用独立摘要模型</strong><span>关闭时由运行端复用 NPC 主模型。</span></div>
        <el-switch :model-value="modelValue.summaryModel !== null" @update:model-value="toggleGameSummaryModel(Boolean($event))" /><span class="source-tag">game</span>
      </div>
      <div v-if="modelValue.summaryModel" class="model-grid">
        <el-form-item label="Base URL"><el-input :model-value="modelValue.summaryModel.baseUrl" @update:model-value="updateSummaryModel('baseUrl', $event)" /></el-form-item>
        <el-form-item label="模型名"><el-input :model-value="modelValue.summaryModel.model" @update:model-value="updateSummaryModel('model', $event)" /></el-form-item>
        <el-form-item label="Temperature"><el-input-number :model-value="modelValue.summaryModel.temperature" :min="0" :max="2" :step="0.1" @update:model-value="updateSummaryModel('temperature', $event)" /></el-form-item>
        <el-form-item label="Max Tokens"><el-input-number :model-value="modelValue.summaryModel.maxTokens" :min="64" :max="8192" @update:model-value="updateSummaryModel('maxTokens', $event)" /></el-form-item>
        <el-form-item label="超时毫秒"><el-input-number :model-value="modelValue.summaryModel.timeoutMs" :min="1000" :max="120000" :step="1000" @update:model-value="updateSummaryModel('timeoutMs', $event)" /></el-form-item>
        <el-form-item label="API Key"><el-input type="password" show-password placeholder="留空表示保留服务端现有值" :model-value="modelValue.summaryModel.apiKey || ''" @update:model-value="updateSummaryModel('apiKey', $event)" /></el-form-item>
      </div>
    </section>

    <section class="form-section">
      <div class="form-section-title"><div><h4>高级扩展字段</h4><p>使用 JSON 对象保存游戏专属选项；服务端会统一持久化和合并。</p></div></div>
      <div v-if="isNpc" class="extension-toggle"><el-switch :model-value="extensionsOverridden()" inline-prompt active-text="覆盖" inactive-text="继承" @update:model-value="toggleExtensions(Boolean($event))" /><span class="source-tag">{{ source('extensions') }}</span></div>
      <el-input :model-value="extensionsText" type="textarea" :rows="7" resize="vertical" :disabled="!extensionsOverridden()" @update:model-value="updateExtensions" />
      <div v-if="extensionsError" class="field-error">{{ extensionsError }}</div>
    </section>
  </div>
</template>

<style scoped>
.policy-form { display: grid; gap: 18px; }
.form-section { border: 1px solid #e4eaf2; border-radius: 12px; overflow: hidden; }
.form-section-title { padding: 16px 18px; background: #f8fafc; border-bottom: 1px solid #e9edf3; }
.form-section-title h4 { margin: 0; font-size: 14px; }
.form-section-title p { margin: 5px 0 0; color: #7a879b; font-size: 11px; }
.policy-row { min-height: 68px; padding: 12px 16px; display: grid; grid-template-columns: minmax(0, 1fr) 86px minmax(150px, 210px) 66px; align-items: center; gap: 12px; border-bottom: 1px solid #edf0f5; position: relative; isolation: isolate; }
.policy-row > * { min-width: 0; }
.policy-form--game .policy-row.compact-row { grid-template-columns: minmax(0, 1fr) minmax(150px, 210px) 58px; }
.policy-form--game .policy-row.boolean-row { grid-template-columns: minmax(0, 1fr) auto 60px; }
.policy-form--game .policy-row.summary-model-row { grid-template-columns: minmax(0, 1fr) auto 60px; }
.policy-row :deep(.el-input-number), .policy-row :deep(.el-select) { width: 100%; min-width: 0; position: relative; z-index: 0; }
.policy-row :deep(.el-input-number .el-input__wrapper) { padding-left: 10px; padding-right: 42px; }
.source-tag { justify-self: end; width: 60px; min-width: 0; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; text-align: center; box-sizing: border-box; font-size: 11px; padding: 3px 7px; }
.policy-row:last-child { border-bottom: 0; }
.field-copy strong, .field-copy span { display: block; }
.field-copy strong { font-size: 13px; }
.field-copy span { color: #8792a5; font-size: 11px; margin-top: 5px; line-height: 1.45; }
.model-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0 20px; padding: 18px; border-top: 1px solid #edf0f5; }
.extension-toggle { display: flex; justify-content: flex-end; align-items: center; gap: 10px; padding: 12px 16px 0; }
.form-section > .el-textarea { padding: 14px 16px 16px; }
.field-error { color: #d94b53; font-size: 11px; padding: 0 16px 14px; }
@media (max-width: 1280px) {
  .policy-row { grid-template-columns: minmax(0, 1fr) 78px minmax(140px, 180px) 62px; gap: 10px; }
  .policy-form--game .policy-row.compact-row { grid-template-columns: minmax(0, 1fr) minmax(140px, 180px) 54px; }
  .policy-form--game .policy-row.boolean-row { grid-template-columns: minmax(0, 1fr) auto 60px; }
  .policy-form--game .policy-row.summary-model-row { grid-template-columns: minmax(0, 1fr) auto 60px; }
}
@media (max-width: 760px) {
  .policy-row, .policy-form--game .policy-row.compact-row { grid-template-columns: minmax(0, 1fr) minmax(120px, 1fr) 56px; }
  .policy-form--game .policy-row.boolean-row { grid-template-columns: minmax(0, 1fr) auto 56px; }
  .policy-form--game .policy-row.summary-model-row { grid-template-columns: minmax(0, 1fr) auto 56px; }
  .policy-form--npc .policy-row { grid-template-columns: minmax(0, 1fr) 78px minmax(120px, 1fr) 56px; }
}
</style>
