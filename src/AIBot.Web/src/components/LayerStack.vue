<script setup lang="ts">
import { ref } from 'vue'
import { percentOf, rampClass, shareOf } from '@/utils/ramp'

export interface Layer {
  name: string
  tokens: number
  text?: string
}

const props = defineProps<{
  layers: Layer[]
  /// 已用总量：层条的条长与色档都相对它计算，回答「谁在吃这次的用量」。
  total: number
}>()

const expanded = ref<string[]>([])

function isOpen(name: string) { return expanded.value.includes(name) }
function toggle(name: string) {
  expanded.value = isOpen(name) ? expanded.value.filter(item => item !== name) : [...expanded.value, name]
}
function barWidth(tokens: number) { return `${shareOf(tokens, props.total) * 100}%` }
function tone(tokens: number) { return rampClass(shareOf(tokens, props.total)) }
</script>

<template>
  <div class="layer-stack">
    <div v-for="layer in layers" :key="layer.name" class="layer-item">
      <button
        type="button"
        class="layer-row"
        :class="{ 'is-static': layer.text === undefined }"
        :aria-expanded="layer.text !== undefined ? isOpen(layer.name) : undefined"
        :title="layer.text ? '展开该层原文' : undefined"
        @click="layer.text !== undefined && toggle(layer.name)"
      >
        <span class="layer-name">{{ layer.name }}</span>
        <span class="layer-bar"><i :class="tone(layer.tokens)" :style="{ width: barWidth(layer.tokens) }"></i></span>
        <span class="layer-tokens">{{ layer.tokens }}</span>
        <span class="layer-share">{{ percentOf(layer.tokens, total) }}</span>
      </button>
      <pre v-if="isOpen(layer.name)" class="layer-text">{{ layer.text }}</pre>
    </div>
  </div>
</template>

<style scoped>
.layer-stack { display: grid; gap: 2px; }
.layer-row {
  width: 100%;
  display: grid;
  grid-template-columns: minmax(72px, max-content) minmax(0, 1fr) max-content max-content;
  align-items: center;
  gap: 14px;
  padding: 10px 10px;
  border: 0;
  border-radius: var(--radius-chip);
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
  transition: background-color .15s ease;
}
.layer-row:hover { background: var(--surface-inset); }
.layer-row.is-static { cursor: default; }
.layer-row.is-static:hover { background: transparent; }
.layer-name { font-size: 13px; font-weight: 600; white-space: nowrap; }
.layer-bar { height: 10px; border-radius: var(--radius-chip); background: var(--surface-track); overflow: hidden; }
.layer-bar i { display: block; height: 100%; min-width: 3px; border-radius: var(--radius-chip); transition: width .25s ease; }
.layer-bar i.ramp-cool { background: var(--ramp-cool); }
.layer-bar i.ramp-warm { background: var(--ramp-warm); }
.layer-bar i.ramp-hot { background: var(--ramp-hot); }
.layer-bar i.ramp-peak { background: var(--ramp-peak); }
.layer-tokens { min-width: 46px; text-align: right; font: 600 13px/1 var(--font-mono); }
.layer-share { min-width: 40px; text-align: right; font-size: 12px; color: var(--graphite); }
.layer-text {
  margin: 2px 0 10px;
  padding: 12px 14px;
  max-height: 320px;
  overflow: auto;
  border-radius: var(--radius-inset);
  background: var(--field-deep);
  color: #cbd7ed;
  font: 12px/1.6 var(--font-mono);
  white-space: pre-wrap;
}

@media (max-width: 720px) {
  .layer-row { grid-template-columns: minmax(0, 1fr) max-content; row-gap: 7px; padding: 10px 8px; }
  .layer-name { grid-column: 1; }
  .layer-tokens { grid-column: 2; }
  .layer-bar { grid-column: 1 / -1; }
  .layer-share { display: none; }
}
</style>
