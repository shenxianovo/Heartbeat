// Real Dashboard interactions against an isolated Analytics/PostgreSQL, using an existing local dev credential.
// Run from the repository root after building Analytics and frontend. No production writes.
import { spawn, execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { resolve, join, extname } from 'node:path'
import { createServer } from 'node:http'
import { randomUUID } from 'node:crypto'
import { setTimeout as delay } from 'node:timers/promises'
import assert from 'node:assert/strict'

const root = process.cwd(), work = resolve('.local/observation-person-04')
mkdirSync(work, { recursive: true })
const env = Object.fromEntries(readFileSync('.env.local', 'utf8').split('\n').filter(l => l && !l.startsWith('#') && l.includes('=')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]))
const credential = JSON.parse(readFileSync('.local/desktop/config.json')).apiKey
const children = [], report = { scope: 'Real Chrome UI, isolated Analytics/PostgreSQL, pre-existing fixtures; no live collectors' }
let container, proxy, ws, pageSession, accessToken = ''
const profile = join(work, 'chrome-' + Date.now())
function redact(text) { for (const secret of [credential, accessToken]) if (secret) text = text.replaceAll(secret, '[redacted]'); return text }
function child(command, args, environment, name, cwd = root) {
  const process = spawn(command, args, { cwd, env: { ...globalThis.process.env, ...environment }, stdio: ['ignore', 'pipe', 'pipe'] })
  children.push(process)
  let log = ''
  process.stdout.on('data', b => { log += b }); process.stderr.on('data', b => { log += b })
  process.on('exit', () => writeFileSync(join(work, name + '.log'), redact(log)))
  return process
}
async function until(fn, label, ms = 30000) {
  const deadline = Date.now() + ms
  while (Date.now() < deadline) { if (await fn()) return; await delay(150) }
  throw new Error('Timeout: ' + label)
}
async function port() {
  const server = createServer()
  await new Promise(r => server.listen(0, '127.0.0.1', r))
  const value = server.address().port
  await new Promise(r => server.close(r))
  return value
}
const pending = new Map(); let sequence = 0
function cdp(method, params = {}, sessionId) {
  return new Promise((resolve, reject) => {
    const id = ++sequence
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)) }, 10000)
    pending.set(id, m => { clearTimeout(timer); m.error ? reject(new Error('CDP failed: ' + method)) : resolve(m.result) })
    ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }))
  })
}
try {
  container = 'heartbeat-person-04-' + Date.now()
  execFileSync('docker', ['run', '-d', '--name', container, '-e', 'POSTGRES_PASSWORD=smoke-local', '-e', 'POSTGRES_DB=heartbeat', '-p', '127.0.0.1::5432', 'postgres:18-alpine'], { stdio: 'pipe' })
  const pgPort = execFileSync('docker', ['port', container, '5432/tcp'], { encoding: 'utf8' }).trim().split(':').at(-1)
  await until(() => { try { execFileSync('docker', ['exec', container, 'pg_isready', '-U', 'postgres'], { stdio: 'pipe' }); return true } catch { return false } }, 'PostgreSQL')
  const api = 'http://127.0.0.1:' + await port()
  child('dotnet', [resolve('server/Heartbeat.Server/bin/Debug/net10.0/Heartbeat.Server.dll')], {
    ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: api,
    ConnectionStrings__DefaultConnection: `Host=127.0.0.1;Port=${pgPort};Database=heartbeat;Username=postgres;Password=smoke-local`,
    AuthService__Authority: env.AUTH_AUTHORITY, AuthService__Issuer: env.AUTH_ISSUER, AuthService__Audience: env.AUTH_AUDIENCE,
    AuthService__OidcIssuer: env.AUTH_OIDC_ISSUER, AuthService__OidcClientId: env.AUTH_OIDC_CLIENT_ID,
    Logging__LogLevel__Default: 'Warning',
  }, 'analytics', resolve('server/Heartbeat.Server'))
  await until(async () => { try { return (await fetch(api + '/openapi/v1.json')).ok } catch { return false } }, 'Analytics', 60000)
  writeFileSync(join(work, 'openapi.json'), await (await fetch(api + '/openapi/v1.json')).text())
  const auth = await fetch('https://auth.shenxianovo.com/api/v1/apikeys/exchange', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ apiKey: credential }) })
  assert.equal(auth.status, 200, 'Existing development credential exchange')
  accessToken = (await auth.json()).accessToken
  const claims = JSON.parse(Buffer.from(accessToken.split('.')[1], 'base64url'))
  const username = claims.preferred_username || 'person-smoke'
  const quote = s => "'" + s.replaceAll("'", "''") + "'"
  execFileSync('docker', ['exec', container, 'psql', '-U', 'postgres', '-d', 'heartbeat', '-v', 'ON_ERROR_STOP=1', '-c',
    `INSERT INTO "Users" ("Id","Username","LastSeenAt","IsPublic") VALUES (${quote(claims.sub)},${quote(username)},now(),false)`], { stdio: 'pipe' })
  async function request(path, method = 'GET', body) {
    const response = await fetch(api + path, { method, headers: { Authorization: 'Bearer ' + accessToken, 'Content-Type': 'application/json', 'X-Heartbeat-Protocol-Version': '4' }, body: body === undefined ? undefined : JSON.stringify(body) })
    assert.ok(response.ok, `API ${method} ${path}: ${response.status}`)
    return response.status === 204 ? null : response.text().then(s => s ? JSON.parse(s) : null)
  }
  const person = await request('/api/v1/me/person', 'PUT')
  function uuid7() { const id = randomUUID().replaceAll('-', ''); const hex = Date.now().toString(16).padStart(12, '0') + '7' + id.slice(13); return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}` }
  for (const [source, aspect, target, title] of [
    ['system', 'desktop-activity', { kind: 'device', reference: 'person-smoke-device' }, 'System 历史活动'],
    ['browser', 'selected-page', { kind: 'application-context', reference: JSON.stringify(['person-smoke-device', 'mac:com.google.chrome']) }, 'Browser 历史页面'],
    ['vrchat.account', 'account-location', { kind: 'account', reference: JSON.stringify(['vrchat', 'usr_11111111-1111-4111-8111-111111111111']) }, 'VRChat 历史世界'],
    ['personal.fixture', 'activity', { kind: 'person', reference: person.reference }, '直接个人事实'],
  ]) {
    const streamId = randomUUID(), observerId = randomUUID()
    await request('/api/v1/facts', 'POST', { streams: [{ streamId, collectorInstanceId: observerId, subject: { subjectId: randomUUID(), kind: 'person' }, outputId: 'activity', source, factKind: 'segment', dimensions: {} }], facts: [{ streamId, factId: uuid7(), revision: 1, observerId, target, aspect, start: '2026-09-01T01:00:00Z', end: '2026-09-01T01:10:00Z', isFinal: true, payload: { activityKey: source, title } }], gaps: [] })
  }
  const original = await request(`/api/v1/users/${encodeURIComponent(username)}/facts/segments`)
  const settings = await request('/api/v1/me/person')
  const device = settings.targets.find(t => t.kind === 'device'), account = settings.targets.find(t => t.kind === 'account')
  const dist = resolve('frontend/dist')
  proxy = createServer(async (req, res) => {
    try {
      if (req.url.startsWith('/api/')) {
        const chunks = []; for await (const chunk of req) chunks.push(chunk)
        const headers = { ...req.headers }; delete headers.host; delete headers.connection
        const upstream = await fetch(api + req.url, { method: req.method, headers, body: chunks.length ? Buffer.concat(chunks) : undefined })
        res.writeHead(upstream.status, { 'Content-Type': upstream.headers.get('content-type') || 'application/json' }); res.end(Buffer.from(await upstream.arrayBuffer())); return
      }
      const relative = decodeURIComponent(new URL(req.url, 'http://local').pathname)
      let path = resolve(dist, '.' + relative)
      if (!path.startsWith(dist + '/') || !existsSync(path) || !extname(path)) path = join(dist, 'index.html')
      const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml' }
      res.writeHead(200, { 'Content-Type': mime[extname(path)] || 'application/octet-stream' }); res.end(readFileSync(path))
    } catch { res.writeHead(500); res.end('Smoke server failed') }
  })
  await new Promise(r => proxy.listen(0, '127.0.0.1', r))
  const ui = 'http://127.0.0.1:' + proxy.address().port
  mkdirSync(profile)
  child('/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', ['--headless=new', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--window-size=1280,1100', '--user-data-dir=' + profile, 'about:blank'], {}, 'chrome')
  await until(() => existsSync(join(profile, 'DevToolsActivePort')), 'Chrome')
  const [debugPort, wsPath] = readFileSync(join(profile, 'DevToolsActivePort'), 'utf8').trim().split('\n')
  ws = new WebSocket(`ws://127.0.0.1:${debugPort}${wsPath}`)
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j })
  ws.onmessage = ({ data }) => { const m = JSON.parse(data); const callback = pending.get(m.id); if (callback) { pending.delete(m.id); callback(m) } }
  const { targetId } = await cdp('Target.createTarget', { url: 'about:blank' })
  const { sessionId } = await cdp('Target.attachToTarget', { targetId, flatten: true })
  pageSession = sessionId
  await cdp('Page.enable', {}, sessionId)
  const evaluate = async expression => {
    const result = await cdp('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId)
    assert.ok(!result.exceptionDetails, 'Browser evaluation succeeded')
    return result.result.value
  }
  await cdp('Page.addScriptToEvaluateOnNewDocument', { source: `localStorage.setItem('access_token',${JSON.stringify(accessToken)});localStorage.setItem('user_id',${JSON.stringify(claims.sub)});` }, sessionId)
  await cdp('Emulation.setTimezoneOverride', { timezoneId: 'Asia/Shanghai' }, sessionId)
  await cdp('Page.navigate', { url: ui + '/settings/person' }, sessionId)
  await until(() => evaluate(`document.body.innerText.includes('直接个人事实')`), 'Person view')
  const set = (selector, value) => evaluate(`(() => { const el=document.querySelector(${JSON.stringify(selector)}); el.value=${JSON.stringify(value)}; el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true})); })()`)
  const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`)
  async function link(kind, id, start, end) {
    await set('[aria-label="关联设备或账号"]', `${kind}:${id}`)
    await set('[aria-label="适用起点"]', start); await set('[aria-label="适用终点"]', end)
    await evaluate(`document.querySelector('form[aria-label="维护本人关联"]').requestSubmit()`)
    await until(() => evaluate(`!document.querySelector('form[aria-label="维护本人关联"] button').disabled && document.querySelector('[aria-label="关联设备或账号"]').value === ''`), 'Create association')
  }
  await link('device', device.id, '2026-09-01T09:02', '2026-09-01T09:05')
  await link('account', account.id, '2026-09-01T09:03', '2026-09-01T09:07')
  await until(() => evaluate(`document.body.innerText.includes('Browser 历史页面') && document.body.innerText.includes('VRChat 历史世界')`), 'Historical sources visible')
  assert.equal((await request('/api/v1/me/person/facts/segments')).totalCount, 4)
  report.createAndBackdatedFourTargetView = true
  const first = (await request('/api/v1/me/person')).associations.find(a => a.deviceId === device.id)
  await click(`[aria-label="纠正关联 ${first.id}"]`)
  await set('[aria-label="适用终点"]', '2026-09-01T09:06')
  await evaluate(`document.querySelector('form[aria-label="维护本人关联"]').requestSubmit()`)
  await until(async () => (await request('/api/v1/me/person')).associations.find(a => a.id === first.id)?.end === '2026-09-01T01:06:00+00:00', 'Correct association')
  await until(() => evaluate(`document.querySelectorAll('.fact').length === 4 && document.body.innerText.includes('有效覆盖：240 秒')`), 'Corrected coverage in UI')
  report.correctionAndEffectiveCoverage = true
  const screenshot = await cdp('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true }, sessionId)
  writeFileSync(join(work, 'person-view.png'), Buffer.from(screenshot.data, 'base64'))
  for (const association of (await request('/api/v1/me/person')).associations) {
    await click(`[aria-label="移除关联 ${association.id}"]`)
    await until(() => evaluate(`!document.querySelector('[aria-label="移除关联 ${association.id}"]') && !document.querySelector('form[aria-label="维护本人关联"] button').disabled`), 'Remove association')
  }
  await until(() => evaluate(`document.querySelectorAll('.fact').length === 1 && document.body.innerText.includes('直接个人事实')`), 'Removed associations change view')
  report.removalRefreshesHistory = true
  assert.deepEqual(await request(`/api/v1/users/${encodeURIComponent(username)}/facts/segments`), original)
  report.originalFactsUnchanged = true
  await set('[aria-label="事实家族"]', 'events')
  await evaluate(`document.querySelector('form[aria-label="按本人筛选"]').requestSubmit()`)
  await until(() => evaluate(`document.body.innerText.includes('0 条事实')`), 'Family filter')
  report.filterInteraction = true
  report.completedAt = new Date().toISOString()
  console.log(JSON.stringify(report, null, 2))
} catch (error) {
  if (ws && pageSession) {
    try {
      const diagnostic = await cdp('Runtime.evaluate', { expression: 'document.body.innerText', returnByValue: true }, pageSession)
      writeFileSync(join(work, 'failure-page.txt'), redact(diagnostic.result.value || ''))
      const shot = await cdp('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true }, pageSession)
      writeFileSync(join(work, 'failure-page.png'), Buffer.from(shot.data, 'base64'))
    } catch { /* Preserve the original error. */ }
  }
  report.failure = redact(error.message)
  console.error(report.failure)
  process.exitCode = 1
} finally {
  ws?.close()
  for (const process of children.reverse()) if (process.exitCode === null) process.kill('SIGTERM')
  await delay(1000)
  for (const process of children) if (process.exitCode === null) process.kill('SIGKILL')
  proxy?.closeAllConnections(); proxy?.close()
  if (container) execFileSync('docker', ['rm', '-f', container], { stdio: 'pipe' })
  rmSync(profile, { recursive: true, force: true })
  writeFileSync(join(work, 'smoke-report.json'), JSON.stringify(report, null, 2))
}
