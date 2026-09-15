/// 数据色阶映射。色阶是控制台里唯一自由的彩色，只用于填充条与色带，不用于文字。
/// 单一色相、四级档位，让整栈读成一把尺子而不是一张饼图。

/// 相对份额，0~1。
export function shareOf(value: number, total: number) {
  return total > 0 ? value / total : 0
}

/// 份额 → 色档。一半以上为 peak，越热表示占用越大。
export function rampClass(share: number) {
  if (share >= 0.5) return 'ramp-peak'
  if (share >= 0.3) return 'ramp-hot'
  if (share >= 0.15) return 'ramp-warm'
  return 'ramp-cool'
}

export function percentOf(value: number, total: number) {
  return `${Math.round(shareOf(value, total) * 100)}%`
}
