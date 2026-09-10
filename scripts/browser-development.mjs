#!/usr/bin/env node
import { spawn } from 'node:child_process'
import { generateKeyPairSync, randomUUID } from 'node:crypto'
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { developmentApiBaseUrl } from './development-endpoints.mjs'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const browser = join(root, 'collection/collectors/Heartbeat.Collector.Browser')

export function publishDevelopmentExtension(source, output, binding) {
  const next = output + '.next', previous = output + '.previous'
  // Recover before checking identity; otherwise an interrupted swap could bypass the Profile pin.
  if (!existsSync(output) && existsSync(previous)) renameSync(previous, output)
  const oldBinding = join(output, 'desktop-binding.json')
  if (existsSync(oldBinding) && JSON.parse(readFileSync(oldBinding, 'utf8')).profileId !== binding.profileId)
    throw new Error('Desktop Profile changed. Retained extension data must not be retargeted to another Profile.')
  const oldManifest = join(output, 'manifest.json'), newManifest = join(source, 'manifest.json')
  if (existsSync(oldManifest) && existsSync(newManifest) &&
      JSON.parse(readFileSync(oldManifest, 'utf8')).key !== JSON.parse(readFileSync(newManifest, 'utf8')).key)
    throw new Error('Extension identity changed; refusing to detach retained browser storage.')
  rmSync(next, { recursive: true, force: true })
  cpSync(source, next, { recursive: true })
  writeFileSync(join(next, 'desktop-binding.json'), JSON.stringify(binding), { mode: 0o600 })
  rmSync(previous, { recursive: true, force: true })
  if (existsSync(output)) renameSync(output, previous)
  try { renameSync(next, output) }
  catch (error) { if (existsSync(previous)) renameSync(previous, output); throw error }
  rmSync(previous, { recursive: true, force: true })
}

function launch(command, args, options = {}) {
  const child = spawn(command, args, { cwd: root, stdio: 'inherit', ...options })
  const exited = new Promise((resolveExit, reject) => {
    child.once('error', reject)
    child.once('exit', (code, signal) => resolveExit({ code, signal }))
  })
  return { child, exited }
}
async function run(command, args, options) {
  const result = await launch(command, args, options).exited
  if (result.code !== 0) throw new Error(`${command} failed (${result.code ?? result.signal})`)
}
function fingerprint() {
  const files = [join(browser, 'manifest.json'), join(browser, 'options.html'), join(browser, 'vite.config.ts'),
    ...readdirSync(join(browser, 'src'), { recursive: true }).map(path => join(browser, 'src', path)),
    ...readdirSync(join(browser, 'Package')).filter(path => path.endsWith('.json')).map(path => join(browser, 'Package', path))]
  return files.filter(path => statSync(path).isFile()).map(path => `${path}:${statSync(path).mtimeMs}`).join('\n')
}

async function main() {
  const args = process.argv.slice(2)
  let browserName
  const browserIndex = args.indexOf('--browser')
  if (browserIndex >= 0) {
    browserName = args[browserIndex + 1]
    if (!['chrome', 'edge'].includes(browserName)) throw new Error('--browser requires chrome or edge.')
    args.splice(browserIndex, 2)
  }
  let profile = join(root, '.local/desktop')
  const profileIndex = args.indexOf('--profile')
  if (profileIndex >= 0) {
    if (!args[profileIndex + 1] || args[profileIndex + 1].startsWith('--')) throw new Error('--profile requires a directory.')
    profile = resolve(args[profileIndex + 1])
    args.splice(profileIndex, 2)
  }
  if (args.includes('--help')) {
    console.log('Usage: node scripts/browser-development.mjs [--watch] [--prepare-only] [--profile PATH] [--browser chrome|edge]\nBuilds an isolated extension and starts this checkout\'s development Desktop.\n--watch rebuilds Browser changes, gracefully restarts the owned Desktop, then the development extension detects the build and reloads itself.\n--prepare-only installs locally without starting collection or connecting to Analytics.')
    return
  }
  if (args.some(arg => !['--watch', '--prepare-only'].includes(arg)) || (args.includes('--watch') && args.includes('--prepare-only')))
    throw new Error('Use --watch or --prepare-only; see --help.')
  if (!['darwin', 'win32'].includes(process.platform)) throw new Error('Development Desktop requires macOS or Windows.')
  const work = profileIndex < 0 ? join(root, '.local/browser-development') : profile + '-browser'
  mkdirSync(work, { recursive: true })
  const lock = join(work, '.owner')
  try { mkdirSync(lock) }
  catch { throw new Error(`Another development command owns ${lock}. If it crashed, verify its PID before removing this lock.`) }
  writeFileSync(join(lock, 'pid'), String(process.pid))
  const output = join(work, 'extension')
  const project = join(root, `collection/desktop/Heartbeat.Desktop.${process.platform === 'darwin' ? 'Mac' : 'Windows'}`)
  let desktop, stopping = false, monitor, monitorGeneration = 0, browserOpened = false
  function monitorConnection(binding, reference) {
    clearInterval(monitor)
    const generation = ++monitorGeneration
    let lastState = '', checking = false
    monitor = setInterval(async () => {
      if (checking) return
      checking = true
      let state = 'Desktop offline; pending data retained'
      try {
        const endpoint = `http://127.0.0.1:${binding.port}/v1/collector-bindings/${binding.profileId}/${binding.token}`
        const response = await fetch(endpoint, { redirect: 'error', signal: AbortSignal.timeout(1000) })
        if (response.ok) {
          const discovery = await response.json()
          const instance = discovery.profileId === binding.profileId && discovery.instances?.find(item =>
            item.packageId === reference.packageId && item.packageContentHash === reference.packageContentHash)
          state = !instance ? 'Desktop online; expected Package is not selected'
            : instance.status.connectedExternalHosts > 0 ? 'Ready: extension connected to the expected Profile and Package'
            : 'Desktop ready; waiting for extension Load unpacked / automatic Reload (about 30s)'
        }
      } catch { /* Offline is an explicit state, never a discovery fallback. */ }
      finally { checking = false }
      if (generation !== monitorGeneration) return
      if (lastState !== state) { console.log(state); lastState = state }
    }, 1000)
  }
  async function openBrowser() {
    if (!browserName || browserOpened) return
    const app = browserName === 'chrome' ? 'Google Chrome' : 'Microsoft Edge'
    const vendor = browserName === 'chrome' ? 'Google/Chrome' : 'Microsoft/Edge'
    const executable = browserName === 'chrome' ? 'chrome.exe' : 'msedge.exe'
    const candidates = process.platform === 'darwin' ? [`/Applications/${app}.app/Contents/MacOS/${app}`]
      : [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA]
          .filter(Boolean).map(directory => join(directory, vendor, 'Application', executable))
    const command = candidates.find(existsSync)
    if (!command) throw new Error(`${app} was not found; omit --browser to launch it manually.`)
    const userData = join(work, `${browserName}-profile`)
    const child = spawn(command, [`--user-data-dir=${userData}`, '--no-first-run', '--no-default-browser-check', 'chrome://extensions'],
      { detached: true, stdio: 'ignore' })
    await new Promise((resolveSpawn, reject) => { child.once('spawn', resolveSpawn); child.once('error', reject) })
    child.unref()
    browserOpened = true
    console.log(`Independent ${app} user data: ${userData}`)
  }
  let signalStop
  const stopSignal = new Promise(resolveStop => { signalStop = resolveStop })
  const stopRequested = () => { stopping = true; signalStop() }
  const parentMessage = message => { if (message?.type === 'heartbeat-development-stop') stopRequested() }
  process.on('SIGINT', stopRequested)
  process.on('SIGTERM', stopRequested)
  process.on('message', parentMessage)
  process.on('disconnect', stopRequested)
  async function stopDesktop() {
    clearInterval(monitor)
    monitorGeneration++
    if (!desktop) return
    if (desktop.child.exitCode === null && desktop.child.signalCode === null) {
      const deadline = Date.now() + 90_000
      while (desktop.child.exitCode === null && desktop.child.signalCode === null) {
        if (Date.now() >= deadline) throw new Error('Desktop did not stop cleanly; preserving the Profile and command lock.')
        writeFileSync(join(profile, 'desktop-stop'), '')
        await Promise.race([desktop.exited, delay(250)])
      }
    }
    const result = await desktop.exited
    desktop = undefined
    if (result.code !== 0) throw new Error(`Development Desktop exited ${result.code ?? result.signal}; update stopped.`)
  }
  try {
    const keyPath = join(work, 'extension-public-key.txt')
    if (!existsSync(keyPath)) {
      const retainedManifest = [join(output, 'manifest.json'), join(output + '.previous', 'manifest.json')].find(existsSync)
      if (retainedManifest) {
        const retainedKey = JSON.parse(readFileSync(retainedManifest, 'utf8')).key
        if (!retainedKey) throw new Error('Existing development extension has no stable key; refusing to replace its identity.')
        writeFileSync(keyPath, retainedKey)
      } else {
        const { publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 })
        writeFileSync(keyPath, publicKey.export({ type: 'spki', format: 'der' }).toString('base64'))
      }
    }
    const key = readFileSync(keyPath, 'utf8')
    let last = ''
    do {
      const observed = fingerprint()
      if (last !== observed) {
        const temporary = mkdtempSync(join(work, 'build-'))
        try {
          const dist = join(temporary, 'dist'), staged = join(temporary, 'package')
          const buildId = randomUUID()
          // No writes to tracked Package/browser-extension or the production dist.
          await run(process.execPath, [join(browser, 'node_modules/typescript/bin/tsc'), '--noEmit'], { cwd: browser })
          await run(process.execPath, [join(browser, 'node_modules/vite/bin/vite.js'), 'build', '--mode', 'development'],
            { cwd: browser, env: { ...process.env, HEARTBEAT_BROWSER_BUILD_DIR: dist, VITE_HEARTBEAT_BUILD_ID: buildId } })
          const manifestPath = join(dist, 'manifest.json')
          const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
          manifest.name = 'Heartbeat Browser Collector — Development'
          manifest.key = key
          writeFileSync(manifestPath, JSON.stringify(manifest, null, 2))
          await run(process.execPath, [join(root, 'scripts/collector-contracts.mjs'), 'stage', 'browser', staged, '--browser-extension', dist])
          // Build failures leave the old running Desktop and extension intact.
          await run('dotnet', ['build', project, '--nologo', '--verbosity', 'quiet'])
          if (stopping) break
          await stopDesktop()
          const desktopArgs = ['run', '--no-build', '--project', project, '--', '--development',
            '--data-directory', profile, '--development-package', staged]
          const env = { ...process.env, HEARTBEAT_API_BASE_URL: developmentApiBaseUrl, HEARTBEAT_SHOW_SETTINGS_ON_START: '1' }
          // Preparation acquires the real Profile lock and must complete before publishing the extension.
          await run('dotnet', [...desktopArgs, '--development-prepare-only'], { env })
          const binding = { ...JSON.parse(readFileSync(join(profile, 'external-host-binding.json'), 'utf8')), buildId }
          publishDevelopmentExtension(join(staged, 'browser-extension'), output, binding)
          if (!args.includes('--prepare-only')) {
            // Already prepared; no Package update occurs after Host.Start.
            desktop = launch('dotnet', ['run', '--no-build', '--project', project, '--', '--development', '--data-directory', profile], { env })
            monitorConnection(binding, JSON.parse(readFileSync(join(output, 'collector-artifact-ref.json'), 'utf8')))
            await openBrowser()
          }
          const reference = JSON.parse(readFileSync(join(output, 'collector-artifact-ref.json'), 'utf8'))
          console.log(`Prepared ${reference.packageId} ${reference.packageVersion} ${reference.packageContentHash}`)
          if (args.includes('--prepare-only')) console.log('Prepared only: Desktop collection was not started.')
          console.log(`Development extension: ${output}\nDesktop Profile: ${binding.profileId}\nUse a separate Chrome/Edge profile. Load unpacked once. Development updates reload automatically (about 30s). Older extensions need one manual Reload to enable this.\nIdentity, configuration and pending data are retained. Never remove/reinstall to update.`)
          last = observed
        } catch (error) {
          if (!args.includes('--watch') || !desktop) throw error
          console.error(`Update failed; retained existing state: ${error.message}`)
          last = observed
        } finally { rmSync(temporary, { recursive: true, force: true }) }
      }
      if (!args.includes('--watch')) {
        if (desktop) await Promise.race([desktop.exited, stopSignal])
        break
      }
      if (desktop && (desktop.child.exitCode !== null || desktop.child.signalCode !== null)) break
      await delay(750)
    } while (!stopping)
  } finally {
    await stopDesktop()
    rmSync(lock, { recursive: true, force: true })
    process.off('SIGINT', stopRequested)
    process.off('SIGTERM', stopRequested)
    process.off('message', parentMessage)
    process.off('disconnect', stopRequested)
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
  main().catch(error => { console.error(error.message); process.exitCode = 1 })
    .finally(() => { if (process.connected) process.disconnect() })
