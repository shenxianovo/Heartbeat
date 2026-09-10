import test from 'node:test'
import assert from 'node:assert/strict'
import { createPlan, start, usage, requestChildStop, runProcess } from './start-local.mjs'

const context = { root: '/checkout with spaces', cwd: '/caller', platform: 'darwin' }
const plan = args => createPlan(args, context)
function harness() {
  const commands = [], requests = [], messages = []
  const io = {
    exists: () => true,
    log: message => messages.push(message),
    request: async url => { requests.push(url); return url.endsWith('/collectors') ? 401 : 200 },
    run: async (command, args, options) => {
      commands.push({ command, args, options })
      if (args.includes('ps')) return { code: 0, stdout: args.at(-1) + '-container' }
      if (args[0] === 'inspect') return { code: 0, stdout: JSON.stringify({ State: { Status: 'running', OOMKilled: false, ExitCode: 0 }, RestartCount: 0 }) }
      if (args.includes('port')) return { code: 0, stdout: '127.0.0.1:' + ({ backend: 18080, frontend: 8080, headless: 18081 })[args.at(-2)] }
      return { code: 0, stdout: '' }
    }
  }
  return { io, commands, requests, messages }
}

test('default stack; explicit selection replaces defaults; dependency closure is narrow', () => {
  assert.deepEqual(plan([]).services, ['backend', 'frontend', 'headless'])
  assert.deepEqual(plan(['--backend']).projects, ['backend'])
  assert.deepEqual(plan(['--backend']).dependencies, ['db'])
  assert.deepEqual(plan(['--desktop']).services, [])
  assert.deepEqual(plan(['--collector', 'browser']).projects, ['desktop', 'collector:browser'])
  assert.deepEqual(plan(['--collector', 'browser']).dependencies, ['desktop'])
  assert.deepEqual(plan(['--frontend', '--frontend']).services, ['frontend'])
  assert.deepEqual(plan(['--stack', '--collector', 'browser']).services, plan([]).services)
  assert.equal(plan(['--collector', 'browser', '--browser-app', 'edge']).browserApp, 'edge')
})

test('configuration alone retains defaults; invalid and retired options fail before effects', () => {
  assert.deepEqual(plan(['--wait-timeout', '3']).services, plan([]).services)
  assert.equal(plan(['--env-file', 'custom.env']).envFile, '/caller/custom.env')
  for (const args of [['--collector'], ['--collector', 'vrchat'], ['--browser-app', 'edge'], ['--browser-app', 'safari'], ['--browser', 'chrome'], ['--desktop-only'], ['-Desktop'], ['--wait-timeout', '0']])
    assert.throws(() => plan(args), undefined, args.join(' '))
  assert.throws(() => createPlan(['--desktop'], { ...context, platform: 'linux' }), /Windows or macOS/)
  assert.match(usage, /--collector/)
})

test('each service starts and becomes ready without checking unselected projects', async () => {
  for (const [flag, service, endpoint] of [
    ['--backend', 'backend', 'http://127.0.0.1:18080/health'],
    ['--frontend', 'frontend', 'http://127.0.0.1:8080/'],
    ['--hub', 'headless', 'http://127.0.0.1:18081/hub/api/v1/collectors']
  ]) {
    const h = harness()
    await start(plan([flag]), h.io)
    assert.deepEqual(h.commands.find(c => c.args.includes('--build')).args.slice(-5), ['up', '--build', '--detach', '--no-deps', service])
    assert.ok(h.commands.filter(c => c.args.includes('up')).every(c => c.args.includes('--no-deps')))
    if (service === 'backend') assert.ok(h.commands.some(c => c.args.includes('--wait') && c.args.at(-1) === 'db'))
    assert.deepEqual(h.requests, [endpoint])
    assert.deepEqual(h.commands.filter(c => c.args.includes('ps')).map(c => c.args.at(-1)), [service])
    assert.ok(!h.commands.some(c => c.args.some(a => ['down', 'stop', '--remove-orphans'].includes(a))))
  }
})

test('desktop and Browser selections bypass Docker and force the isolated local endpoint', async () => {
  for (const args of [['--desktop'], ['--collector', 'browser', '--browser-app', 'edge']]) {
    const h = harness()
    h.io.exists = () => false // No Compose or .env files required.
    await start(plan(args), h.io)
    assert.ok(!h.commands.some(c => c.command === 'docker'))
    const command = h.commands.at(-1)
    if (args[0] === '--desktop') {
      assert.equal(command.command, 'dotnet')
      assert.deepEqual(command.args.slice(-3), ['--development', '--data-directory', '/checkout with spaces/.local/desktop'])
      assert.equal(command.options.env.HEARTBEAT_API_BASE_URL, 'http://localhost:18080')
    } else {
      assert.deepEqual(command.args, ['/checkout with spaces/scripts/browser-development.mjs', '--watch', '--browser', 'edge'])
    }
  }
})

test('failed startup stops before Desktop; selected container crashes fail promptly', async () => {
  const h = harness(), run = h.io.run
  h.io.run = async (...args) => args[1].includes('up') ? { code: 23, stdout: '' } : run(...args)
  await assert.rejects(start(plan(['--stack', '--desktop']), h.io), error => error.exitCode === 23)
  assert.ok(!h.commands.some(c => c.command === 'dotnet' && c.args.includes('run')))
  const crash = harness(), original = crash.io.run
  crash.io.run = async (...args) => args[1][0] === 'inspect'
    ? { code: 0, stdout: JSON.stringify({ State: { Status: 'exited', OOMKilled: false, ExitCode: 7 }, RestartCount: 0 }) } : original(...args)
  await assert.rejects(start(plan(['--hub']), crash.io), /headless failed/)
})

test('readiness rejects redirect responses, times out, and never probes non-loopback bindings', async () => {
  const h = harness()
  h.io.request = async () => 302
  let time = 0
  h.io.now = () => time
  h.io.sleep = async () => { time += 1000 }
  await assert.rejects(start(plan(['--frontend', '--wait-timeout', '1']), h.io), /did not become ready/)
  const remote = harness(), run = remote.io.run
  remote.io.run = async (...args) => args[1].includes('port') ? { code: 0, stdout: '0.0.0.0:8080' } : run(...args)
  await assert.rejects(start(plan(['--frontend']), remote.io), /loopback/)
  assert.deepEqual(remote.requests, [])
})

test('readiness rejects OOM and restart loops even when HTTP responds', async () => {
  for (const state of [
    { State: { Status: 'running', OOMKilled: true, ExitCode: 137 }, RestartCount: 0 },
    { State: { Status: 'restarting', OOMKilled: false, ExitCode: 1 }, RestartCount: 3 }
  ]) {
    const h = harness(), run = h.io.run
    h.io.run = async (...args) => args[1][0] === 'inspect'
      ? { code: 0, stdout: JSON.stringify(state) } : run(...args)
    await assert.rejects(start(plan(['--backend']), h.io), /backend failed/)
    assert.deepEqual(h.requests, [])
  }
})


test('Windows console interrupts never become forced kill; Browser uses a portable stop message', () => {
  const signals = [], messages = []
  const child = { connected: false, kill: signal => signals.push(signal), send: message => messages.push(message) }
  requestChildStop(child, 'SIGINT', 'win32')
  assert.deepEqual(signals, [])
  child.connected = true
  requestChildStop(child, 'SIGINT', 'win32')
  assert.deepEqual(messages, [{ type: 'heartbeat-development-stop' }])
  assert.deepEqual(signals, [])
  child.connected = false
  requestChildStop(child, 'SIGTERM', 'darwin')
  assert.deepEqual(signals, ['SIGTERM'])
})

test('Windows Desktop plan selects its native head and preserves child failures', async () => {
  const h = harness()
  await start(createPlan(['--desktop'], { ...context, platform: 'win32' }), h.io)
  assert.ok(h.commands.at(-1).args.some(arg => arg.includes('Heartbeat.Desktop.Windows.csproj')))
  const failure = harness(), run = failure.io.run
  failure.io.run = async (...args) => args[1].includes('--development') ? { code: 37, stdout: '' } : run(...args)
  await assert.rejects(start(plan(['--desktop']), failure.io), error => error.exitCode === 37)
})

test('real wrapper stop event uses IPC and waits for child cleanup', async () => {
  const { spawn } = await import('node:child_process')
  const module = new URL('./start-local.mjs', import.meta.url).href
  const childCode = `process.on('message', message => {
    if (message.type === 'heartbeat-development-stop') {
      console.log('cleanup-complete'); process.disconnect(); process.exitCode = 0
    }
  }); console.log('child-ready')`
  const parentCode = `import { runProcess } from ${JSON.stringify(module)};
    process.on('message', () => process.emit('SIGINT'));
    const result = await runProcess(process.execPath, ['-e', ${JSON.stringify(childCode)}], { ipc: true });
    process.exitCode = result.code; process.disconnect();`
  const parent = spawn(process.execPath, ['--input-type=module', '-e', parentCode], { stdio: ['ignore', 'pipe', 'pipe', 'ipc'] })
  let output = '', errors = '', sent = false
  parent.stdout.on('data', chunk => {
    output += chunk
    if (!sent && output.includes('child-ready')) { sent = true; parent.send('interrupt') }
  })
  parent.stderr.on('data', chunk => { errors += chunk })
  const timeout = setTimeout(() => parent.kill('SIGKILL'), 10000)
  try {
    const code = await new Promise((resolve, reject) => { parent.once('exit', resolve); parent.once('error', reject) })
    assert.equal(code, 0, errors)
    assert.match(output, /cleanup-complete/)
  } finally { clearTimeout(timeout) }
})
