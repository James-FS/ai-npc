<script setup lang="ts">
import { computed } from 'vue'
import { rampClass, shareOf } from '@/utils/ramp'

/// 单值计量条：把一个真实的同单位量级画成条。
/// tone='ramp' 用于「占预算/上限的份额」——热色表示压力，仅在份额语义成立时使用；
/// tone='neutral' 用于「绝对质量分」——长度已表达数值，用热色会误导为警报，故用中性填充。
const props = withDefaults(defineProps<{
  value: number
  /// 参照量：整列的最大值或总量（0~1 的比率则传 1）。
  max: number
  label?: string
  width?: number
  tone?: 'ramp' | 'neutral'
}>(), { width: 64, tone: 'ramp' })

const share = computed(() => shareOf(props.value, props.max))
const toneClass = computed(() => props.tone === 'neutral' ? 'ramp-neutral' : rampClass(share.value))
</script>

<template>
  <span class="ramp-meter">
    <span class="ramp-track" :style="{ width: `${width}px` }">
      <i :class="toneClass" :style="{ width: `${Math.min(100, share * 100)}%` }"></i>
    </span>
    <span v-if="label !== undefined" class="ramp-label">{{ label }}</span>
  </span>
</template>

<style scoped>
.ramp-meter { display: inline-flex; align-items: center; gap: 8px; }
.ramp-track { height: 8px; border-radius: var(--radius-chip); background: var(--surface-track); overflow: hidden; flex: 0 0 auto; }
.ramp-track i { display: block; height: 100%; min-width: 2px; border-radius: var(--radius-chip); }
.ramp-track i.ramp-cool { background: var(--ramp-cool); }
.ramp-track i.ramp-warm { background: var(--ramp-warm); }
.ramp-track i.ramp-hot { background: var(--ramp-hot); }
.ramp-track i.ramp-peak { background: var(--ramp-peak); }
.ramp-track i.ramp-neutral { background: var(--ink); }
.ramp-label { font-size: 12px; }
</style>
