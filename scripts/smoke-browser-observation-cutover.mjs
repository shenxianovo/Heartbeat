// Real Chrome MV3/windows/storage -> actual ExternalHost -> durable Runtime.
// Uses only a fresh temporary browser Profile and a capability-bound local TestHost.
// HTTP/PostgreSQL continuation is FactHttpTests.Browser.cs, using the same production fold/protocol.
import assert from 'node:assert/strict'
import { spawn, execFileSync } from 'node:child_process'
import { randomBytes, randomUUID } from 'node:crypto'
import { createServer } from 'node:http'
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, existsSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'
import { publishDevelopmentExtension } from './browser-development.mjs'

const root = process.cwd()
const work = mkdtempSync(join(tmpdir(), 'heartbeat-browser-cutover-'))
const browser = join(root, 'collection/collectors/Heartbeat.Collector.Browser')
const project = join(root, 'collection/collectors/Heartbeat.Collector.Browser.TestHost')
const chromeBinary = process.env.HEARTBEAT_BROWSER_EXECUTABLE ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
const children = []
const report = { work, browser: chromeBinary, productionProfileUsed: false, analytics: 'separately verified by FactHttpTests.Browser.cs' }
let ws, pageServer, chrome, host, extensionId
let sequence = 0
const pending = new Map()

function run(command, args, options = {}) {
  try { return execFileSync(command, args, { stdio: 'pipe', ...options }) }
  catch (error) {
    throw Error(`${command} failed:\n${error.stdout?.toString() ?? ''}\n${error.stderr?.toString() ?? ''}`)
  }
}
function child(command, args, name) {
  const process = spawn(command, args, { cwd: root, stdio: ['pipe', 'pipe', 'pipe'] })
  children.push(process)
  let output = ''
  process.stdout.on('data', data => { output += data })
  process.stderr.on('data', data => { output += data })
  process.on('exit', () => writeFileSync(join(work, `${name}.log`), output))
  return process
}
async function until(read, label, timeout = 30_000) {
  const deadline = Date.now() + timeout
  while (Date.now() < deadline) {
    const value = await read()
    if (value) return value
    await delay(200)
  }
  throw Error(`Timed out: ${label}`)
}
async function freePort() {
  const server = createServer()
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  await new Promise(resolve => server.close(resolve))
  return port
}
function cdp(method, params = {}, sessionId) {
  const id = ++sequence
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(Error(`CDP timeout: ${method}`)) }, 10_000)
    pending.set(id, { resolve: value => { clearTimeout(timer); resolve(value) }, reject: error => { clearTimeout(timer); reject(error) } })
    ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }))
  })
}
async function workerEvaluate(expression) {
  const target = await until(async () => (await cdp('Target.getTargets')).targetInfos.find(target => target.type === 'service_worker' && target.url.startsWith(`chrome-extension://${extensionId}/`)), 'extension worker')
  const { sessionId } = await cdp('Target.attachToTarget', { targetId: target.targetId, flatten: true })
  try {
    const result = await cdp('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId)
    if (result.exceptionDetails) throw Error(JSON.stringify(result.exceptionDetails))
    return result.result.value
  } finally { await cdp('Target.detachFromTarget', { sessionId }) }
}
async function flush() {
  await workerEvaluate("chrome.alarms.create('heartbeat-flush',{when:Date.now()+100})")
  await delay(700)
}
async function startChrome() {
  const profile = join(work, 'chrome-profile')
  mkdirSync(profile, { recursive: true })
  rmSync(join(profile, 'DevToolsActivePort'), { force: true })
  chrome = child(chromeBinary, ['--headless=new', '--enable-extensions', '--no-first-run', '--no-default-browser-check', '--enable-unsafe-extension-debugging', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], `chrome-${children.length}`)
  await until(() => existsSync(join(profile, 'DevToolsActivePort')), 'Chrome startup')
  const [port, path] = readFileSync(join(profile, 'DevToolsActivePort'), 'utf8').trim().split('\n')
  ws = new WebSocket(`ws://127.0.0.1:${port}${path}`)
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject })
  ws.onmessage = ({ data }) => {
    const message = JSON.parse(data)
    const promise = pending.get(message.id)
    if (!promise) return
    pending.delete(message.id)
    message.error ? promise.reject(Error(JSON.stringify(message.error))) : promise.resolve(message.result)
  }
  const { targetId } = await cdp('Target.createTarget', { url: 'chrome://extensions' })
  const { sessionId } = await cdp('Target.attachToTarget', { targetId, flatten: true })
  await delay(600)
  await cdp('Runtime.evaluate', { expression: `(function enable(root){for(const e of root.querySelectorAll('*')){if(e.localName==='cr-toggle'&&e.id==='devMode'&&!e.checked)e.click();if(e.shadowRoot)enable(e.shadowRoot)}})(document)` }, sessionId)
  extensionId = (await cdp('Extensions.loadUnpacked', { path: join(work, 'extension') })).id
}
async function stop(process) {
  if (!process || (process.exitCode !== null || process.signalCode !== null)) return
  process.kill('SIGTERM')
  await until(() => (process.exitCode !== null || process.signalCode !== null), 'owned process stop', 10_000)
}

try {
  const binding = { profileId: randomBytes(16).toString('hex'), token: randomBytes(32).toString('hex'), port: await freePort() }
  const buildId = randomUUID()
  writeFileSync(join(work, 'external-host-binding.json'), JSON.stringify(binding), { mode: 0o600 })
  const dist = join(work, 'dist'), staged = join(work, 'package')
  run(process.execPath, [join(browser, 'node_modules/vite/bin/vite.js'), 'build', '--mode', 'development'], {
    cwd: browser, env: { ...process.env, HEARTBEAT_BROWSER_BUILD_DIR: dist, VITE_HEARTBEAT_BUILD_ID: buildId }, stdio: 'pipe',
  })
  run(process.execPath, [join(root, 'scripts/collector-contracts.mjs'), 'stage', 'browser', staged, '--include-current-test-platform', '--browser-extension', dist], { stdio: 'pipe' })
  publishDevelopmentExtension(join(staged, 'browser-extension'), join(work, 'extension'), { ...binding, buildId })
  report.package = JSON.parse(readFileSync(join(staged, 'browser-extension/collector-artifact-ref.json'), 'utf8'))
  run('dotnet', ['build', project, '--nologo'], { stdio: 'pipe', timeout: 120_000 })
  host = child('dotnet', [join(project, 'bin/Debug/net10.0/Heartbeat.Collector.Browser.TestHost.dll'), staged, join(work, 'runtime'), work], 'host')
  const endpoint = `http://127.0.0.1:${binding.port}`
  const read = async () => (await fetch(`${endpoint}/test/pending`)).json()
  await until(async () => { try { return (await fetch(`${endpoint}/test/pending`)).ok } catch { return false } }, 'ExternalHost')
  await startChrome()
  pageServer = createServer((request, response) => response.end(`<html><title>Browser cutover fixture</title><body>${request.url}</body></html>`))
  await new Promise(resolve => pageServer.listen(0, '127.0.0.1', resolve))
  const url = `http://127.0.0.1:${pageServer.address().port}/same-page`
  const windows = await workerEvaluate(`Promise.all([chrome.windows.create({url:'${url}'}),chrome.windows.create({url:'${url}'})]).then(windows=>windows.map(window=>window.id))`)
  await delay(800)
  await flush()
  const first = await until(async () => {
    const state = await read()
    const facts = state.facts.filter(item => item.observation?.result.activityKey === url)
    return facts.length === 2 ? { ...state, facts } : false
  }, 'two actual windows in Runtime')
  const observations = first.facts.map(item => item.observation)
  const observer = observations[0].collectorId
  assert.equal(new Set(observations.map(fact => fact.id)).size, 2)
  assert.equal(new Set(observations.map(fact => fact.result.attributes.windowId)).size, 2)
  assert.ok(observations.every(fact => fact.collectorId === observer && fact.collectorId !== first.instanceId && fact.foi.kind === 'app' && fact.relations.some(relation => relation.kind === 'observed-on')))
  assert.ok(first.facts.every(item => item.stream === null && item.fact === null))
  report.twoActualWindows = true
  report.observer = observer
  report.factIds = observations.map(fact => fact.id)
  report.foi = observations[0].foi
  const initial = observations.find(fact => fact.result.attributes.windowId === windows[0])
  await workerEvaluate(`chrome.windows.remove(${windows[0]})`)
  await flush()
  const terminal = (await read()).facts.find(item => item.observation?.id === initial.id)
  assert.equal(terminal.isFinal, true)
  assert.ok(terminal.observation.revision > initial.revision)
  await workerEvaluate(`chrome.windows.create({url:'${url}'})`)
  await delay(500)
  await flush()
  const reopened = (await read()).facts.filter(item => item.observation?.result.activityKey === url)
  assert.equal(new Set(reopened.map(item => item.observation.id)).size, 3)
  report.closeAndReopen = true
  const worker = (await cdp('Target.getTargets')).targetInfos.find(target => target.type === 'service_worker' && target.url.startsWith(`chrome-extension://${extensionId}/`))
  const options = await cdp('Target.createTarget', { url: `chrome-extension://${extensionId}/options.html` })
  const control = await cdp('Target.attachToTarget', { targetId: options.targetId, flatten: true })
  await cdp('ServiceWorker.enable', {}, control.sessionId)
  await cdp('ServiceWorker.stopAllWorkers', {}, control.sessionId)
  await cdp('ServiceWorker.startWorker', { scopeURL: `chrome-extension://${extensionId}/` }, control.sessionId)
  await until(() => workerEvaluate("typeof chrome.alarms?.create === 'function'"), 'restarted worker Chrome APIs')
  await flush()
  const afterWorker = (await read()).facts.filter(item => item.observation?.result.activityKey === url)
  assert.equal(new Set(afterWorker.map(item => item.observation.id)).size, 3)
  assert.equal(await workerEvaluate("chrome.storage.local.get('browserCollectorExternalHostIdentity').then(value=>value.browserCollectorExternalHostIdentity)"), observer)
  report.serviceWorkerRestart = true
  report.workerTargetBeforeRestart = worker.targetId
  await fetch(`${endpoint}/test/restart`, { method: 'POST' })
  await workerEvaluate("chrome.storage.session.set({backoff:{fails:0,nextAttemptAt:0}})")
  await flush()
  const restored = (await read()).facts.filter(item => item.observation?.result.activityKey === url)
  assert.ok(report.factIds.every(id => restored.some(item => item.observation.id === id)))
  assert.ok(restored.every(item => item.observation.collectorId === observer))
  report.runtimeRestart = true
  host.stdin.end('\n')
  await until(() => (host.exitCode !== null || host.signalCode !== null), 'offline Runtime stop')
  await workerEvaluate(`chrome.windows.create({url:'${url}-offline'})`)
  await delay(500)
  await flush()
  const offlineState = await workerEvaluate("chrome.storage.local.get('browserObservationJournal')")
  const offline = Object.values(offlineState.browserObservationJournal.state.queue).find(snapshot => snapshot.activityKey === `${url}-offline`)
  assert.ok(offline && offline.collectorId === observer && offline.kind === 'segment')
  report.offlineFactId = offline.id
  ws.close()
  await stop(chrome)
  host = child('dotnet', [join(project, 'bin/Debug/net10.0/Heartbeat.Collector.Browser.TestHost.dll'), staged, join(work, 'runtime'), work], 'host-restored')
  await until(async () => { try { return (await fetch(`${endpoint}/test/pending`)).ok } catch { return false } }, 'restored ExternalHost')
  await startChrome()
  await delay(800)
  await workerEvaluate("chrome.storage.session.set({backoff:{fails:0,nextAttemptAt:0}})")
  await flush()
  const offlineReplay = await until(async () => (await read()).facts.find(item => item.observation?.id === offline.id), 'offline browser replay')
  assert.equal(offlineReplay.observation.collectorId, observer)
  assert.ok(offlineReplay.observation.revision >= offline.revision)
  assert.deepEqual(offlineReplay.observation.foi, observations[0].foi)
  assert.equal(await workerEvaluate("chrome.storage.local.get('browserCollectorExternalHostIdentity').then(value=>value.browserCollectorExternalHostIdentity)"), observer)
  report.browserAndHostProcessRestart = true
  report.offlineReplay = true
  report.completedAt = new Date().toISOString()
  console.log(JSON.stringify(report, null, 2))
} catch (error) {
  report.failure = error.message
  throw error
} finally {
  ws?.close()
  host?.stdin.end('\n')
  for (const process of children.reverse()) await stop(process).catch(() => process.kill('SIGKILL'))
  pageServer?.close()
  writeFileSync(join(work, 'report.json'), JSON.stringify(report, null, 2))
  console.log(`Disposable fixture evidence: ${join(work, 'report.json')}`)
}
