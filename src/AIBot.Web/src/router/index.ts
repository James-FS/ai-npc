import { createRouter, createWebHashHistory } from 'vue-router'

const MemorySettingsView = () => import('@/views/settings/MemorySettingsView.vue')
const GameMemoryPolicyView = () => import('@/views/game/GameMemoryPolicyView.vue')
const NpcMemoryPolicyView = () => import('@/views/npc/NpcMemoryPolicyView.vue')
const MemoryInspectorView = () => import('@/views/memory/MemoryInspectorView.vue')
const MemoryMigrationView = () => import('@/views/memory/MemoryMigrationView.vue')
const MemoryAuditView = () => import('@/views/memory/MemoryAuditView.vue')
const DebugChatView = () => import('@/views/debug/DebugChatView.vue')
const DebugNpcView = () => import('@/views/debug/DebugNpcView.vue')
const DebugWorldView = () => import('@/views/debug/DebugWorldView.vue')
const DebugPromptView = () => import('@/views/debug/DebugPromptView.vue')
const DebugSessionsView = () => import('@/views/debug/DebugSessionsView.vue')
const DebugLogsView = () => import('@/views/debug/DebugLogsView.vue')
const DebugStatsView = () => import('@/views/debug/DebugStatsView.vue')

export default createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', redirect: '/settings/memory' },
    { path: '/settings/memory', component: MemorySettingsView, meta: { title: '系统记忆边界' } },
    { path: '/game/memory-policy', component: GameMemoryPolicyView, meta: { title: 'Game 记忆策略' } },
    { path: '/npc/:id/memory', component: NpcMemoryPolicyView, meta: { title: 'NPC 记忆覆盖' } },
    { path: '/memories', component: MemoryInspectorView, meta: { title: '玩家记忆检查器' } },
    { path: '/memory-migrations', component: MemoryMigrationView, meta: { title: '旧记忆迁移' } },
    { path: '/memory-audit', component: MemoryAuditView, meta: { title: '记忆审计' } },
    { path: '/debug/chat', component: DebugChatView, meta: { title: '流式对话调试' } },
    { path: '/debug/npc', component: DebugNpcView, meta: { title: 'NPC 配置调试' } },
    { path: '/debug/world', component: DebugWorldView, meta: { title: '世界观调试' } },
    { path: '/debug/prompt', component: DebugPromptView, meta: { title: 'Prompt 预览' } },
    { path: '/debug/sessions', component: DebugSessionsView, meta: { title: '会话调试' } },
    { path: '/debug/logs', component: DebugLogsView, meta: { title: '请求日志' } },
    { path: '/debug/stats', component: DebugStatsView, meta: { title: '用量统计' } },
  ],
})
