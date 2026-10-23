const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const ts = require('typescript')
const { reactive, ref } = require('vue')

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
function loadModule(filename, app = {}) {
  const exports = {}
  const source = fs.readFileSync(filename, 'utf8')
  const compiled = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText
  const localRequire = id => {
    if (id === '@/stores/app') return { useAppStore: () => app }
    if (id.startsWith('.')) return loadModule(path.resolve(path.dirname(filename), `${id}.ts`), app)
    return require(id)
  }
  new Function('require', 'exports', compiled)(localRequire, exports)
  return exports
}

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
