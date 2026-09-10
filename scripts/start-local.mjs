#!/usr/bin/env node
import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { developmentApiBaseUrl } from './development-endpoints.mjs'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const projectOrder = ['backend', 'frontend', 'hub', 'desktop', 'collector:browser']
const services = {
  backend: { name: 'backend', path: '/health', statuses: [200] },
  frontend: { name: 'frontend', path: '/', statuses: [200] },
  hub: { name: 'headless', path: '/hub/api/v1/collectors', statuses: [401, 403] }
}
export const usage = `Usage: ./scripts/start-local.sh [projects] [options]
       ./scripts/start-local.ps1 [projects] [options]

No project flags: backend + frontend + Hub. Explicit flags replace that default.
Projects (combine freely):
  --backend            Analytics backend (also starts its database)
  --frontend           Dashboard frontend
  --hub                Headless Hub
  --desktop            Isolated native development Desktop
  --collector browser  Browser Collector development/watch (also starts Desktop)
  --stack              backend + frontend + Hub
Options:
  --browser-app NAME   chrome (default) or edge; requires --collector browser
  --compose-file PATH  Default: this checkout's compose.local.yml
  --env-file PATH      Default: this checkout's .env.local
  --wait-timeout SEC   Startup/migration wait budget (default: 1800)
  -h, --help           Show help

Only selected projects and required dependencies are started. Unselected instances stay as they are.
Desktop uses .local/desktop and ${developmentApiBaseUrl}; it can buffer while Analytics is offline.
Browser: Load unpacked once; updates auto-reload (about 30s). Older extensions need one manual Reload.
Ctrl+C stops the owned Desktop.
Node.js is an internal dependency of both entrypoints. Compose services remain running on exit.`

export function createPlan(args, context = {}) {
  const repository = context.root ?? root, cwd = context.cwd ?? process.cwd()
  const platform = context.platform ?? process.platform
  const selected = new Set()
  let composeFile = join(repository, 'compose.local.yml'), envFile = join(repository, '.env.local')
  let waitTimeout = 1800, browserApp = 'chrome', browserAppExplicit = false
  for (let i = 0; i < args.length; i++) {
    const arg = args[i]
    function value() {
      if (!args[i + 1] || args[i + 1].startsWith('-')) throw new Error(`Missing value for ${arg}.`)
      return args[++i]
    }
    if (['--backend', '--frontend', '--hub', '--desktop'].includes(arg)) selected.add(arg.slice(2))
    else if (arg === '--stack') ['backend', 'frontend', 'hub'].forEach(project => selected.add(project))
    else if (arg === '--collector') {
      const collector = value()
      if (collector !== 'browser') throw new Error(`Unsupported Collector: ${collector}. Available: browser.`)
      selected.add('collector:browser')
    } else if (arg === '--browser-app') {
      browserApp = value()
      if (!['chrome', 'edge'].includes(browserApp)) throw new Error('--browser-app requires chrome or edge.')
      browserAppExplicit = true
    } else if (arg === '--compose-file') composeFile = resolve(cwd, value())
    else if (arg === '--env-file') envFile = resolve(cwd, value())
    else if (arg === '--wait-timeout') {
      const seconds = value()
      if (!/^[1-9][0-9]*$/.test(seconds) || !Number.isSafeInteger(Number(seconds)) || Number(seconds) > 2147483647)
        throw new Error('--wait-timeout requires a positive integer no greater than 2147483647.')
      waitTimeout = Number(seconds)
    } else if (arg === '--desktop-only' || arg === '-DesktopOnly') throw new Error('Use --desktop; explicit project flags now replace the default stack.')
    else if (arg === '--browser' || arg === '-Browser') throw new Error('Use --collector browser [--browser-app chrome|edge].')
    else throw new Error(`Unknown option: ${arg}. Use --help for the shared sh/pwsh syntax.`)
  }
  if (browserAppExplicit && !selected.has('collector:browser')) throw new Error('--browser-app requires --collector browser.')
  const defaults = selected.size === 0
  if (defaults) ['backend', 'frontend', 'hub'].forEach(project => selected.add(project))
  const requested = projectOrder.filter(project => selected.has(project))
  const dependencies = []
  if (selected.has('backend')) dependencies.push('db')
  if (selected.has('collector:browser') && !selected.has('desktop')) { selected.add('desktop'); dependencies.push('desktop') }
  if (selected.has('desktop') && !['darwin', 'win32'].includes(platform)) throw new Error('Desktop requires Windows or macOS.')
  const projects = projectOrder.filter(project => selected.has(project))
  return { root: repository, platform, composeFile, envFile, waitTimeout, browserApp, defaults, requested,
    projects, dependencies, services: projects.filter(project => services[project]).map(project => services[project].name) }
}

export function requestChildStop(child, signal, platform = process.platform) {
  if (child.connected) {
    child.send({ type: 'heartbeat-development-stop' }, () => { /* Child may have already exited. */ })
  } else if (platform !== 'win32') child.kill(signal)
  // Windows console children receive Ctrl+C themselves. kill(SIGINT) would forcibly terminate them.
}

export function runProcess(command, args, { capture = false, ipc = false, ...options } = {}) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(command, args, { stdio: ipc ? ['inherit', 'inherit', 'inherit', 'ipc'] : capture ? ['ignore', 'pipe', 'inherit'] : 'inherit', ...options })
    let stdout = ''
    if (capture) child.stdout.on('data', data => { stdout += data })
    const interrupt = () => requestChildStop(child, 'SIGINT'), terminate = () => requestChildStop(child, 'SIGTERM')
    process.on('SIGINT', interrupt); process.on('SIGTERM', terminate)
    const cleanup = () => { process.off('SIGINT', interrupt); process.off('SIGTERM', terminate) }
    child.once('error', error => { cleanup(); reject(error) })
    child.once('exit', (code, signal) => { cleanup(); resolveRun({ code: code ?? (signal === 'SIGINT' ? 130 : 143), stdout }) })
  })
}

export async function start(plan, overrides = {}) {
  const io = {
    run: runProcess, exists: existsSync, log: console.log, now: Date.now, sleep: delay,
    request: async url => {
      try { return (await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(2000) })).status }
      catch { return 0 }
    }, ...overrides
  }
  async function run(command, args, options = {}) {
    const result = await io.run(command, args, { cwd: plan.root, ...options })
    if (result.code !== 0) throw Object.assign(new Error(`${command} ${args[0] ?? ''} failed with exit code ${result.code}.`), { exitCode: result.code })
    return result.stdout.trim()
  }
  io.log(`${plan.defaults ? 'Default projects' : 'Selected projects'}: ${plan.requested.join(', ')}`)
  io.log(`Required dependencies: ${plan.dependencies.join(', ') || 'none'}`)
  io.log(`Start: ${[...plan.dependencies.filter(item => !plan.projects.includes(item)), ...plan.projects].join(', ')}`)
  if (plan.projects.includes('desktop')) await run('dotnet', ['--version'], { capture: true })
  if (plan.services.length > 0) {
    if (!io.exists(plan.composeFile)) throw new Error(`Compose file not found: ${plan.composeFile}`)
    if (!io.exists(plan.envFile)) throw new Error(`Environment file not found: ${plan.envFile}. Copy .env.local.example first.`)
    await run('docker', ['compose', 'version'], { capture: true })
    await run('docker', ['info'], { capture: true })
    const compose = ['compose', '--file', plan.composeFile, '--env-file', plan.envFile]
    await run('docker', [...compose, 'config', '--quiet'])
    io.log('Building and starting selected services; the backend applies pending migrations.')
    if (plan.dependencies.includes('db'))
      await run('docker', [...compose, 'up', '--detach', '--no-deps', '--wait', '--wait-timeout', String(plan.waitTimeout), 'db'])
    await run('docker', [...compose, 'up', '--build', '--detach', '--no-deps', ...plan.services])
    const probes = []
    for (const project of plan.projects.filter(project => services[project])) {
      const service = services[project]
      const mapping = await run('docker', [...compose, 'port', service.name, '8080'], { capture: true })
      const match = /^(127\.0\.0\.1|\[::1\]):([0-9]+)$/.exec(mapping)
      if (!match || Number(match[2]) < 1 || Number(match[2]) > 65535)
        throw new Error(`${service.name} must publish container port 8080 on loopback; got no supported local binding.`)
      probes.push({ ...service, url: `http://${match[1]}:${match[2]}${service.path}`, status: 0 })
    }
    const deadline = io.now() + plan.waitTimeout * 1000
    let nextProgress = 0, ready = false
    const initialRestarts = new Map()
    while (io.now() < deadline) {
      for (const probe of probes) {
        const container = await run('docker', [...compose, 'ps', '--all', '--quiet', probe.name], { capture: true })
        if (!container || container.includes('\n')) throw new Error(`${probe.name} failed: expected one container.`)
        const state = JSON.parse(await run('docker', ['inspect', '--format', '{"State":{{json .State}},"RestartCount":{{.RestartCount}}}', container], { capture: true }))
        if (!initialRestarts.has(probe.name)) initialRestarts.set(probe.name, state.RestartCount)
        if (state.State.Status !== 'running' || state.State.OOMKilled || state.RestartCount !== initialRestarts.get(probe.name))
          throw new Error(`${probe.name} failed: state=${state.State.Status}, exit=${state.State.ExitCode}, restarts=${state.RestartCount}. Inspect its Compose logs.`)
        probe.status = await io.request(probe.url)
      }
      if (probes.every(probe => probe.statuses.includes(probe.status))) { ready = true; break }
      if (io.now() >= nextProgress) {
        io.log(`Waiting for selected services: ${probes.map(probe => `${probe.name}=${probe.status}`).join(', ')}`)
        nextProgress = io.now() + 15000
      }
      await io.sleep(1000)
    }
    if (!ready) throw new Error(`Selected services did not become ready within ${plan.waitTimeout}s. Check migrations and Compose logs before restarting.`)
    for (const probe of probes) io.log(`Ready: ${probe.name} ${probe.url}`)
    io.log('Unselected services were not started or stopped. Ready checks only cover the selected services, not upstream data delivery.')
  }
  if (plan.projects.includes('collector:browser')) {
    await run(process.execPath, [join(plan.root, 'scripts/browser-development.mjs'), '--watch', '--browser', plan.browserApp], { ipc: true })
  } else if (plan.projects.includes('desktop')) {
    const platform = plan.platform === 'darwin' ? 'Mac' : 'Windows'
    io.log(`Starting development Desktop: ${join(plan.root, '.local/desktop')} -> ${developmentApiBaseUrl}`)
    await run('dotnet', ['run', '--project', join(plan.root, `collection/desktop/Heartbeat.Desktop.${platform}/Heartbeat.Desktop.${platform}.csproj`),
      '--', '--development', '--data-directory', join(plan.root, '.local/desktop')],
    { env: { ...process.env, HEARTBEAT_API_BASE_URL: developmentApiBaseUrl, HEARTBEAT_SHOW_SETTINGS_ON_START: '1' } })
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const args = process.argv.slice(2)
  if (args.includes('--help') || args.includes('-h')) console.log(usage)
  else {
    let plan
    try { plan = createPlan(args) }
    catch (error) { console.error(error.message); process.exitCode = 2 }
    if (plan) await start(plan).catch(error => { console.error(error.message); process.exitCode = error.exitCode ?? 1 })
  }
}
