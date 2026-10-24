const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const ts = require('typescript')
const { reactive, ref } = require('vue')
global.window = { addEventListener() {}, removeEventListener() {} }

test('console presents five primary entrances while retaining the legacy route table', () => {
  const app = fs.readFileSync(path.resolve('src/App.vue'), 'utf8')
  const router = fs.readFileSync(path.resolve('src/router/index.ts'), 'utf8')
  assert.deepEqual([...app.matchAll(/\{ id: '(debug|content|memory|models|ops)'/g)].map(match => match[1]),
    ['debug', 'content', 'memory', 'models', 'ops'])
  for (const route of ['/debug/chat', '/debug/npc', '/debug/world', '/debug/prompt', '/debug/sessions', '/debug/logs', '/debug/stats', '/memory-migrations']) {
    assert.ok(router.includes(`path: '${route}'`), `legacy route ${route} remains available`)
  }
})

// Run the actual TypeScript modules with a small browser/store boundary, without a
// second bundler or a browser installation. fetch still uses real Response streams.
function loadModule(filename, app = {}, mocks = {}) {
  const exports = {}
  const source = fs.readFileSync(filename, 'utf8')
  const compiled = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText
  const localRequire = id => {
    if (Object.prototype.hasOwnProperty.call(mocks, id)) return mocks[id]
    if (id === '@/stores/app') return { useAppStore: () => app }
    if (id.startsWith('.')) return loadModule(path.resolve(path.dirname(filename), `${id}.ts`), app)
    return require(id)
  }
  new Function('require', 'exports', compiled)(localRequire, exports)
  return exports
}

function loadSfcScript(filename, mocks = {}) {
  const { parse, compileScript } = require('@vue/compiler-sfc')
  const descriptor = parse(fs.readFileSync(filename, 'utf8'), { filename }).descriptor
  const script = compileScript(descriptor, { id: 'regression-test' }).content
  const compiled = ts.transpileModule(script, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText
  const exports = {}
  const localRequire = id => {
    if (Object.prototype.hasOwnProperty.call(mocks, id)) return mocks[id]
    if (id.startsWith('@/utils/')) return loadModule(path.resolve('src', id.slice(2) + '.ts'), {}, {
      ...mocks, 'vue-router': { onBeforeRouteLeave() {} },
    })
    return require(id)
  }
  new Function('require', 'exports', compiled)(localRequire, exports)
  return exports.default
}

function deferred() {
  let resolve, reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

function activateStore(storeFactory) {
  const { createPinia, setActivePinia } = require('pinia')
  setActivePinia(createPinia())
  return storeFactory()
}

test('Game policy store drops stale successes, failures and finally from A-B-A loads', async () => {
  const pending = []
  const api = { gamePolicy: game => { const d = deferred(); pending.push({ game, d }); return d.promise } }
  const { useGameMemoryPolicyStore } = loadModule(path.resolve('src/stores/gameMemoryPolicy.ts'), {}, { '@/api/memory': { memoryApi: api } })
  const store = activateStore(useGameMemoryPolicyStore)
  const firstA = store.load('A'), loadB = store.load('B'), secondA = store.load('A')
  pending[2].d.resolve({ policy: { marker: 'new A' }, limits: { marker: 'new' } })
  await secondA
  pending[1].d.reject(new Error('stale B failure'))
  pending[0].d.resolve({ policy: { marker: 'old A' }, limits: {} })
  await Promise.all([loadB, firstA])
  assert.equal(store.policy.marker, 'new A')
  assert.equal(store.loading, false)
  // A second A request is also protected by its request token even though the owner ID matches.
  const oldA = deferred(), freshA = deferred(); let calls = 0
  api.gamePolicy = () => (++calls === 1 ? oldA.promise : freshA.promise)
  const staleA = store.load('A'), latestA = store.load('A')
  freshA.resolve({ policy: { marker: 'latest' }, limits: {} }); await latestA
  oldA.resolve({ policy: { marker: 'stale' }, limits: {} }); await staleA
  assert.equal(store.policy.marker, 'latest')
})

test('app store ignores stale NPC list errors and keeps the current list loading state', async t => {
  const originalStorage = global.localStorage
  global.localStorage = { getItem: () => '', setItem() {} }
  t.after(() => { global.localStorage = originalStorage })
  const requests = []
  const api = { npcIds: gameId => { const d = deferred(); requests.push({ gameId, d }); return d.promise } }
  const { useAppStore } = loadModule(path.resolve('src/stores/app.ts'), {}, { '@/api/memory': { memoryApi: api } })
  const store = activateStore(useAppStore)
  store.gameId = 'A'
  const old = store.loadNpcs()
  store.gameId = 'B'
  const current = store.loadNpcs()
  requests[1].d.resolve({ npcs: ['new'] }); await current
  requests[0].d.reject(Error('stale list failure')); await old
  assert.deepEqual(store.npcIds, ['new'])
  assert.equal(store.loadingNpcs, false)
})

test('NPC policy save and preview are tied to load epochs and capture request payloads', async () => {
  const reads = [], saves = [], previews = []
  const api = {
    npcPolicy: (gameId, npcId) => { const d = deferred(); reads.push({ gameId, npcId, d }); return d.promise },
    saveNpcPolicy: (...args) => { const d = deferred(); saves.push({ args, d }); return d.promise },
    previewNpcPolicy: (...args) => { const d = deferred(); previews.push({ args, d }); return d.promise },
  }
  const { useNpcMemoryPolicyStore } = loadModule(path.resolve('src/stores/npcMemoryPolicy.ts'), {}, { '@/api/memory': { memoryApi: api } })
  const store = activateStore(useNpcMemoryPolicyStore)
  const a1 = store.load('A', 'n'), b = store.load('B', 'n'), a2 = store.load('A', 'n')
  reads[2].d.resolve({ npc: { marker: 'A2' }, effective: { marker: 'A2' } }); await a2
  reads[1].d.reject(Error('old B')); await b
  reads[0].d.resolve({ npc: { marker: 'A1' }, effective: { marker: 'A1' } }); await a1
  assert.equal(store.settings.marker, 'A2')
  const epoch = store.contextEpoch, save = store.save('A', 'n')
  assert.equal(saves[0].args[2].marker, 'A2')
  const loadB2 = store.load('B', 'n')
  reads[3].d.resolve({ npc: { marker: 'B' }, effective: { marker: 'B' } }); await loadB2
  saves[0].d.resolve({ policy: { marker: 'stale save' } }); assert.equal(await save, false)
  assert.equal(store.settings.marker, 'B')
  assert.notEqual(store.contextEpoch, epoch)
})

test('Memory inspector keeps detail ownership across list refreshes and accepts consecutive mutations', async () => {
  let version = 1
  const oldDetail = deferred()
  const api = {
    memories: async () => ({ items: [{ marker: 'list' }], total: 1 }),
    memory: (gameId, npcId, playerId) => playerId === 'old' ? oldDetail.promise
      : Promise.resolve({ gameId, npcId, playerId, memoryVersion: version, facts: [], summary: '' }),
    sessions: async () => ({ sessions: [] }), queue: async () => ({}),
    saveSummary: async (gameId, npcId, playerId, summary) => ({ gameId, npcId, playerId, summary, memoryVersion: ++version, facts: [] }),
    addFact: async (gameId, npcId, playerId, fact) => ({ gameId, npcId, playerId, memoryVersion: ++version, facts: [fact] }),
    updateFact: async (gameId, npcId, playerId, id, fact) => ({ gameId, npcId, playerId, memoryVersion: ++version, facts: [fact] }),
    deleteFact: async (gameId, npcId, playerId) => ({ gameId, npcId, playerId, memoryVersion: ++version, facts: [] }),
  }
  const { useMemoryInspectorStore } = loadModule(path.resolve('src/stores/memoryInspector.ts'), {}, { '@/api/memory': { memoryApi: api } })
  const store = activateStore(useMemoryInspectorStore)
  const oldOpen = store.open('A', 'n', 'old')
  await store.open('B', 'n', 'p')
  oldDetail.reject(Error('stale open failure')); await oldOpen
  await store.search('B', {})
  assert.equal(await store.saveFact('B', { value: 'one' }), true)
  assert.equal(await store.saveFact('B', { value: 'two' }), true)
  assert.equal(await store.saveSummary('B', 'summary'), true)
  assert.equal(await store.deleteFact('B', 'id'), true)
  assert.equal(store.detail.gameId, 'B')
  assert.equal(store.detail.memoryVersion, 5)
})

test('Game policy view cancels a confirmed save or preset after its context epoch changes', async () => {
  const { ref } = require('vue')
  const app = { gameId: 'A' }
  const store = {
    contextEpoch: 1, loading: false, saving: false, policy: ref({ memoryScope: 'player_npc', marker: 'A' }),
    effective: ref(null), limits: ref({}), load: async () => {}, invalidate() {},
    saveCalls: [], async save(game, payload, epoch) { this.saveCalls.push({ game, payload, epoch }); return true },
  }
  const prompts = [], messages = []
  const messageBox = { confirm: () => { const d = deferred(); prompts.push(d); return d.promise } }
  const component = loadSfcScript(path.resolve('src/views/game/GameMemoryPolicyView.vue'), {
    '@/components/PageHeader.vue': {}, '@/components/memory/MemoryPolicyForm.vue': {},
    '@/components/memory/EffectivePolicyPanel.vue': {}, '@/stores/app': { useAppStore: () => app },
    '@/stores/gameMemoryPolicy': { useGameMemoryPolicyStore: () => store },
    vue: { ...require('vue'), onMounted() {}, onBeforeUnmount() {} },
    pinia: { storeToRefs: value => ({ policy: value.policy, limits: value.limits, effective: value.effective }) },
    'element-plus': { ElMessage: { success: value => messages.push(value), error: value => messages.push(value) }, ElMessageBox: messageBox },
  })
  const actions = component.setup({}, { expose() {} })
  const saving = actions.save()
  app.gameId = 'B'; store.contextEpoch = 2; store.policy.value = { memoryScope: 'session', marker: 'B' }
  prompts[0].resolve(); await saving
  assert.equal(store.saveCalls.length, 0)
  assert.equal(messages.length, 0)

  const applying = actions.applyPreset('标准')
  app.gameId = 'A'; store.contextEpoch = 3; store.policy.value = { marker: 'fresh A', memoryScope: 'player_npc' }
  prompts[1].resolve(); await applying
  assert.equal(store.policy.value.marker, 'fresh A')

  const normalSave = actions.save()
  prompts[2].resolve(); await normalSave
  assert.equal(store.saveCalls.length, 1)
  assert.equal(store.saveCalls[0].game, 'A')
  assert.equal(store.saveCalls[0].payload.marker, 'fresh A')
  assert.equal(messages[0], 'Game 记忆策略已保存')
})

test('Debug NPC view finishes a normal save without a stale saving flag', async () => {
  const app = { gameId: 'A', currentNpcId: 'n' }
  const saved = [], messages = []
  const config = {
    npcId: 'n', model: { baseUrl: '', model: '', temperature: 0.8, maxTokens: 500, timeoutMs: 20000 },
    output: { emotions: [], actions: [] }, loreBlocks: [], enabledToolIds: [], fallbackReplies: [],
  }
  const api = { npc: async () => structuredClone(config), saveNpc: async (...args) => saved.push(args) }
  const component = loadSfcScript(path.resolve('src/views/debug/DebugNpcView.vue'), {
    '@/components/PageHeader.vue': {}, '@/api/debug': { debugApi: api }, '@/stores/app': { useAppStore: () => app },
    '@element-plus/icons-vue': { Plus: {}, Connection: {} },
    vue: { ...require('vue'), onMounted() {}, onBeforeUnmount() {} },
    'element-plus': { ElMessage: { success: value => messages.push(value), error: value => messages.push(value) }, ElMessageBox: {} },
  })
  const actions = component.setup({}, { expose() {} })
  await actions.load()
  await actions.save()
  assert.equal(saved.length, 1)
  assert.equal(actions.saving.value, false)
  assert.equal(messages.at(-1), 'NPC 配置已保存')
})

test('Memory inspector view invalidates a fact draft and delete confirmation when detail changes', async () => {
  const { reactive, ref } = require('vue')
global.window = { addEventListener() {}, removeEventListener() {} }
  const app = { gameId: 'A' }, writes = [], messages = [], confirmations = []
  const store = reactive({
    detail: ref({ gameId: 'A', npcId: 'nA', playerId: 'pA', memoryVersion: 1, summary: '', facts: [] }),
    items: [], total: 0, sessions: [], queue: { failedCurrent: 0 }, loading: false, queueLoading: false,
    async open(gameId, npcId, playerId) { this.detail = { gameId, npcId, playerId, memoryVersion: 1, summary: '', facts: [] } },
    async search() {}, async loadQueue() {}, async saveFact(...args) { writes.push(args); return true },
    async saveSummary() { return true }, async deleteFact(...args) { writes.push(args); return true },
    closeDetail() { this.detail = null }, invalidate() {},
  })
  const component = loadSfcScript(path.resolve('src/views/memory/MemoryInspectorView.vue'), {
    '@/components/PageHeader.vue': {}, '@/components/RampMeter.vue': {}, '@/api/http': { ApiError: class ApiError extends Error {} },
    '@/api/memory': { memoryApi: { deleteMemory: async (...args) => writes.push(args), summarize: async () => ({}), exportMemory: async () => ({}), retrySummaryQueue: async () => ({ retried: 0 }) } },
    '@/stores/app': { useAppStore: () => app }, '@/stores/memoryInspector': { useMemoryInspectorStore: () => store },
    '@/utils/clone': loadModule(path.resolve('src/utils/clone.ts')),
    vue: { ...require('vue'), onMounted() {}, onBeforeUnmount() {} },
    'element-plus': {
      ElMessage: { success: value => messages.push(value), error: value => messages.push(value), warning() {} },
      ElMessageBox: { confirm: () => { const d = deferred(); confirmations.push(d); return d.promise } },
    },
  })
  const actions = component.setup({}, { expose() {} })
  actions.newFact()
  actions.factForm.value = 'A fact draft'
  const deleting = actions.removeFact({ id: 'factA', value: 'fact from A' })
  await actions.openDetail('nB', 'pB')
  confirmations[0].resolve(); await deleting
  assert.equal(writes.length, 0)
  await actions.saveFact()
  assert.equal(writes.length, 0)
  assert.equal(messages.length, 0)
})

test('fact forms and loaded facts can be cloned without losing editable fields', () => {
  const { cloneJson } = loadModule(path.resolve('src/utils/clone.ts'))
  const form = reactive({ category: 'general', value: 'test', pinned: true, expiresUtc: null })
  const loadedFact = ref({ facts: [form] }).value.facts[0]
  for (const value of [form, loadedFact]) {
    const clone = cloneJson(value)
    assert.deepEqual(clone, { category: 'general', value: 'test', pinned: true, expiresUtc: null })
    clone.value = 'edited'
    assert.equal(value.value, 'test')
  }
})

test('a partial token stream recovers from the terminal-only server replay', async t => {
  const { streamChat } = loadModule(path.resolve('src/api/debug.ts'), { serverBase: '', adminToken: 'admin' })
  const originalFetch = global.fetch
  t.after(() => { global.fetch = originalFetch })
  let requests = []
  global.fetch = async (url, init) => {
    requests.push(init)
    const replay = requests.length > 1
    const events = replay ? [{ type: 'reply', say: 'hello world' }, { type: 'done', requestId: 'req' }]
      : [{ type: 'token', delta: 'hello' }]
    return new Response(events.map(e => `data: ${JSON.stringify(e)}\n\n`).join(''), {
      headers: replay ? { 'X-AIBot-Replayed': 'true' } : {},
    })
  }
  const received = []
  await streamChat('g', { requestId: 'req', npcId: 'n', playerId: 'p', sessionId: 's', message: 'hi', simState: {} }, e => received.push(e))
  assert.deepEqual(received.map(e => e.type), ['token', 'reply', 'done'])
  assert.equal(requests.length, 2)
  assert.equal(requests[0].body, requests[1].body)
  assert.equal(requests[0].headers.get('Authorization'), 'Bearer admin')
})

test('reply delivered before a disconnect is not emitted twice on replay', async t => {
  const { streamChat } = loadModule(path.resolve('src/api/debug.ts'), { serverBase: '' })
  const originalFetch = global.fetch
  t.after(() => { global.fetch = originalFetch })
  let attempt = 0
  global.fetch = async () => {
    const replay = ++attempt > 1
    const events = [{ type: 'reply', say: 'hello' }, ...(replay ? [{ type: 'done' }] : [])]
    return new Response(events.map(e => `data: ${JSON.stringify(e)}\n\n`).join(''), {
      headers: replay ? { 'X-AIBot-Replayed': 'true' } : {},
    })
  }
  const received = []
  await streamChat('g', { requestId: 'req', npcId: 'n', playerId: 'p', sessionId: 's', message: 'hi', simState: {} }, e => received.push(e))
  assert.deepEqual(received.map(e => e.type), ['reply', 'done'])
})
const browserBoundary = { ...require('vue'), onMounted() {}, onBeforeUnmount() {} }

test('session selection separates server, game and player and normalizes same-site addresses', t => {
  const previous = global.location
  global.location = { origin: 'http://localhost:5000' }
  t.after(() => { global.location = previous })
  const { sessionKey } = loadModule(path.resolve('src/utils/sessionKey.ts'))
  const key = sessionKey('', 'g', 'n', 'p')
  assert.equal(key, sessionKey('http://localhost:5000/', 'g', 'n', 'p'))
  for (const args of [['http://other:5000', 'g', 'n', 'p'], ['', 'other', 'n', 'p'], ['', 'g', 'n', 'other']]) assert.notEqual(key, sessionKey(...args))
})

test('unsaved cancellation retains draft and restoring baseline clears dirty state', async () => {
  const draft = ref({ name: 'original' }), answers = []
  const helper = loadModule(path.resolve('src/utils/unsaved.ts'), {}, {
    vue: browserBoundary, 'vue-router': { onBeforeRouteLeave() {} },
    'element-plus': { ElMessageBox: { confirm: () => new Promise((resolve, reject) => answers.push({ resolve, reject })) } },
  })
  const guard = helper.useUnsaved(draft)
  guard.markSaved()
  assert.equal(guard.dirty.value, false)
  draft.value.name = 'edited'
  const cancel = helper.confirmDiscard(); answers[0].reject('cancel')
  assert.equal(await cancel, false); assert.equal(draft.value.name, 'edited'); assert.equal(guard.dirty.value, true)
  draft.value.name = 'original'; assert.equal(guard.dirty.value, false)
})

test('dedicated summary selection writes once with mode and connection, cancellation writes nothing', async () => {
  const writes = []
  const state = { connections: [{ id: 'summary', hasApiKey: false }], bindings: { npcSummary: {}, npcSummaryMode: {} } }
  const component = loadSfcScript(path.resolve('src/components/ModelConnectionsPanel.vue'), {
    vue: browserBoundary,
    '@/api/settings': { settingsApi: { modelConnections: async () => state, bindModelConnection: async (...args) => { writes.push(args); return state } } },
    'element-plus': { ElMessage: { success() {}, error() {} }, ElMessageBox: {} },
  })
  const actions = component.setup({}, { expose() {}, emit() {} })
  await actions.load()
  await actions.setNpcSummaryMode('g', 'n', 'dedicated')
  assert.equal(writes.length, 0)
  actions.editingNpcSummary.value = null
  assert.equal(writes.length, 0)
  await actions.setNpcSummaryMode('g', 'n', 'dedicated')
  await actions.saveNpcSummary('g', 'n', 'missing'); assert.equal(writes.length, 0)
  await actions.saveNpcSummary('g', 'n', 'summary')
  assert.deepEqual(writes, [['npc_summary', 'g', 'n', 'summary', 'dedicated']])
})

test('session list and delete confirmation ignore A-B-A context changes', async () => {
  const app = reactive({ serverBase: '', gameId: 'A', currentNpcId: 'n' }), reads = [], confirmations = [], writes = []
  global.localStorage = { getItem: () => '', setItem() {} }
  const component = loadSfcScript(path.resolve('src/views/debug/DebugSessionsView.vue'), {
    vue: browserBoundary, '@/components/PageHeader.vue': {}, '@/stores/app': { useAppStore: () => app },
    '@/api/debug': { debugApi: { sessions: () => { const d = deferred(); reads.push(d); return d.promise }, deleteSession: async (...args) => writes.push(args) } },
    'element-plus': { ElMessage: { success() {}, error() {} }, ElMessageBox: { confirm: () => { const d = deferred(); confirmations.push(d); return d.promise } } },
  })
  const actions = component.setup({}, { expose() {} })
  const first = actions.load()
  const removing = actions.remove({ npcId: 'n', sessionId: 's' })
  app.gameId = 'B'; app.gameId = 'A'
  reads[2].resolve({ sessions: [{ sessionId: 'fresh' }] }); await Promise.resolve(); await Promise.resolve()
  reads[0].resolve({ sessions: [{ sessionId: 'stale' }] }); await first
  reads[1].resolve({ sessions: [] })
  confirmations[0].resolve(); await removing
  assert.equal(actions.sessions.value[0].sessionId, 'fresh')
  assert.equal(writes.length, 0); assert.equal(actions.loading.value, false)
})

test('chat restore and stream callbacks cannot write after A-B-A or player changes', async () => {
  const app = reactive({ serverBase: '', gameId: 'A', currentNpcId: 'n' }), reads = [], streams = []
  global.localStorage = { getItem: () => '', setItem() {} }
  global.location = { origin: 'http://localhost:5000' }
  const component = loadSfcScript(path.resolve('src/views/debug/DebugChatView.vue'), {
    vue: browserBoundary, '@/components/PageHeader.vue': {}, '@/stores/app': { useAppStore: () => app },
    '@element-plus/icons-vue': { Switch: {} }, '@/api/http': { ApiError: class extends Error {} },
    '@/api/debug': { debugApi: { session: () => { const d = deferred(); reads.push(d); return d.promise } },
      streamChat: (...args) => { const d = deferred(); streams.push({ args, d }); return d.promise } },
    'element-plus': { ElMessage: { success() {}, error() {} } },
  })
  const actions = component.setup({}, { expose() {} })
  const old = actions.restoreSession(); app.gameId = 'B'; app.gameId = 'A'
  reads[2].resolve({ messages: [{ role: 'assistant', content: 'fresh' }] }); await Promise.resolve(); await Promise.resolve()
  reads[0].resolve({ messages: [{ role: 'assistant', content: 'old' }] }); await old
  assert.equal(actions.messages.value[0].content, 'fresh')
  const sending = actions.send('hello')
  const target = actions.messages.value.at(-1)
  streams[0].args[2]({ type: 'token', delta: 'live' }); assert.equal(target.content, 'live')
  actions.playerId.value = 'other'
  assert.equal(streams[0].args[3].aborted, true)
  streams[0].args[2]({ type: 'token', delta: 'stale' }); streams[0].d.resolve(); await sending
  assert.equal(target.content, 'live'); assert.equal(actions.messages.value.length, 0)
})

test('model settings page keeps one unified refresh while the panel reports draft state', async () => {
  const page = fs.readFileSync(path.resolve('src/views/settings/LlmSettingsView.vue'), 'utf8')
  const panel = fs.readFileSync(path.resolve('src/components/ModelConnectionsPanel.vue'), 'utf8')
  assert.equal([...page.matchAll(/>刷新</g)].length, 1)
  assert.match(page, /panel\.value\?\.load\(\)/)
  assert.match(page, /:disabled="panelDraft"/)
  assert.ok(!panel.includes('>刷新<'), 'panel no longer renders its own refresh button')
  assert.ok(!panel.includes('恢复默认'), 'inherit recovery only stays in the summary mode dropdown')

  const reads = []
  const state = { connections: [], bindings: { globalMain: '', npcMain: {}, npcSummary: {}, gameSummary: {}, npcSummaryMode: {} } }
  const api = { modelConnections: async () => { reads.push('load'); return state } }
  const component = loadSfcScript(path.resolve('src/components/ModelConnectionsPanel.vue'), {
    vue: browserBoundary,
    '@/api/settings': { settingsApi: api },
    'element-plus': { ElMessage: { success() {}, error() {} }, ElMessageBox: {} },
  })
  const actions = component.setup({}, { expose() {}, emit() {} })
  assert.equal(actions.hasDraft.value, false)
  await actions.load()
  assert.deepEqual(reads, ['load'])
  actions.editingNpcSummary.value = 'g/n'
  assert.equal(actions.hasDraft.value, true)
  actions.dialog.value = true
  assert.equal(actions.hasDraft.value, true)
})

test('NPC save bar derives loading, saved and unsaved states from existing flags', async () => {
  const app = { gameId: 'A', currentNpcId: 'n' }
  const config = {
    npcId: 'n', model: { baseUrl: '', model: '', temperature: 0.8, maxTokens: 500, timeoutMs: 20000 },
    output: { emotions: [], actions: [] }, loreBlocks: [], enabledToolIds: [], fallbackReplies: [],
  }
  const pending = deferred()
  const api = { npc: () => pending.promise, saveNpc: async () => {} }
  const component = loadSfcScript(path.resolve('src/views/debug/DebugNpcView.vue'), {
    '@/components/PageHeader.vue': {}, '@/api/debug': { debugApi: api }, '@/stores/app': { useAppStore: () => app },
    '@element-plus/icons-vue': { Plus: {}, Connection: {} },
    vue: { ...require('vue'), onMounted() {}, onBeforeUnmount() {} },
    'element-plus': { ElMessage: { success() {}, error() {} }, ElMessageBox: {} },
  })
  const actions = component.setup({}, { expose() {} })
  assert.equal(actions.statusText.value, '未加载')
  const loadingNpc = actions.load()
  assert.equal(actions.statusText.value, '加载中')
  pending.resolve(structuredClone(config)); await loadingNpc
  assert.equal(actions.statusText.value, '已保存')
  actions.config.value.displayName = '改名'
  assert.equal(actions.statusText.value, '未保存修改')
})

test('save bars rename reset to undo and disable actions while clean', () => {
  for (const rel of ['views/debug/DebugNpcView.vue', 'views/debug/DebugWorldView.vue',
    'views/game/GameMemoryPolicyView.vue', 'views/npc/NpcMemoryPolicyView.vue']) {
    const source = fs.readFileSync(path.resolve('src', rel), 'utf8')
    const bar = source.slice(source.indexOf('class="save-bar"'))
    assert.ok(bar.includes('撤销未保存修改'), `${rel} renames the save-bar reset`)
    assert.ok(!bar.includes('>重置<'), `${rel} keeps no generic reset button`)
    assert.match(bar, /type="primary"[^>]*:disabled="[^"]*!dirty/, `${rel} disables save without modifications`)
  }
})

test('single-item groups skip sub navigation and the ops entry names legacy migration', () => {
  const app = fs.readFileSync(path.resolve('src/App.vue'), 'utf8')
  assert.match(app, /v-if="activeGroup\.items\.length > 1"/)
  assert.ok(app.includes("'旧记忆迁移'"))
  assert.ok(!app.includes('高级操作'))
})

test('fact save keeps advanced fields hidden in the collapsed group', async () => {
  const writes = []
  const store = reactive({
    detail: ref({ gameId: 'A', npcId: 'nA', playerId: 'pA', memoryVersion: 1, summary: '', facts: [] }),
    items: [], total: 0, sessions: [], queue: { failedCurrent: 0 }, loading: false, queueLoading: false,
    async open(gameId, npcId, playerId) { this.detail = { gameId, npcId, playerId, memoryVersion: 1, summary: '', facts: [] } },
    async search() {}, async loadQueue() {}, async saveFact(...args) { writes.push(args); return true },
    async saveSummary() { return true }, async deleteFact() { return true },
    closeDetail() { this.detail = null }, invalidate() {},
  })
  const component = loadSfcScript(path.resolve('src/views/memory/MemoryInspectorView.vue'), {
    '@/components/PageHeader.vue': {}, '@/components/RampMeter.vue': {}, '@/api/http': { ApiError: class ApiError extends Error {} },
    '@/api/memory': { memoryApi: { deleteMemory: async () => {}, summarize: async () => ({}), exportMemory: async () => {}, retrySummaryQueue: async () => ({ retried: 0 }) } },
    '@/stores/app': { useAppStore: () => ({ gameId: 'A' }) }, '@/stores/memoryInspector': { useMemoryInspectorStore: () => store },
    '@/utils/clone': loadModule(path.resolve('src/utils/clone.ts')),
    vue: { ...require('vue'), onMounted() {}, onBeforeUnmount() {} },
    'element-plus': { ElMessage: { success() {}, error() {}, warning() {} }, ElMessageBox: { confirm: async () => {} } },
  })
  const actions = component.setup({}, { expose() {} })
  await actions.openDetail('nA', 'pA')
  actions.newFact()
  actions.factForm.value = '一条内容'
  actions.factForm.key = 'player.name'
  actions.factForm.confidence = 0.55
  actions.factForm.sourceSessionId = 's-9'
  actions.factForm.expiresUtc = '2030-01-01T00:00:00.000Z'
  actions.factForm.pinned = true
  await actions.saveFact()
  assert.equal(writes.length, 1)
  const payload = writes[0][1]
  assert.equal(payload.value, '一条内容')
  assert.equal(payload.key, 'player.name')
  assert.equal(payload.confidence, 0.55)
  assert.equal(payload.sourceSessionId, 's-9')
  assert.equal(payload.expiresUtc, '2030-01-01T00:00:00.000Z')
  assert.equal(payload.pinned, true)
})
