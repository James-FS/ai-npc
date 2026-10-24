import { computed, onBeforeUnmount, ref, watch, type Ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { ElMessageBox } from 'element-plus'
let active: (() => boolean) | undefined
let discard: (() => void) | undefined
export async function confirmDiscard() {
  if (!active?.()) return true
  try { await ElMessageBox.confirm('当前有未保存修改，确定放弃并继续？', '未保存修改', { type: 'warning', confirmButtonText: '放弃修改', cancelButtonText: '保留编辑' }); discard?.(); return true } catch { return false }
}
export function useUnsaved(value: Ref<unknown>) {
  const baseline = ref('')
  const dirty = computed(() => value.value != null && JSON.stringify(value.value) !== baseline.value)
  const check = () => dirty.value
  active = check
  discard = () => markSaved()
  function markSaved(payload = value.value) { baseline.value = JSON.stringify(payload) }
  const unload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
  watch(dirty, yes => { if (yes) window.addEventListener('beforeunload', unload); else window.removeEventListener('beforeunload', unload) }, { flush: 'sync' })
  onBeforeRouteLeave(() => confirmDiscard())
  onBeforeUnmount(() => { if (active === check) active = undefined; window.removeEventListener('beforeunload', unload) })
  return { dirty, markSaved }
}
