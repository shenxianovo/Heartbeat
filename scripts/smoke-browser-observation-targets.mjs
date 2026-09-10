// Real Chrome + native macOS Desktop -> isolated Analytics/PostgreSQL.
// Prepare the dedicated profile and build current binaries first; see browser-observation-targets.md.
// Reads the existing local development credential, never prints it, and directs all facts to a new local DB.
import {
  spawn,
  execFileSync
} from 'node:child_process'
import {
  readFileSync,
  writeFileSync,
  existsSync,
  mkdirSync,
  rmSync
} from 'node:fs'
import {
  resolve,
  join
} from 'node:path'
import {
  createServer
} from 'node:http'
import {
  setTimeout as delay
} from 'node:timers/promises'
import assert from 'node:assert/strict'
const root = process.cwd(),
  work = resolve('.local/observation-browser-02'),
  profile = join(work, 'desktop'),
  extension = join(work, 'desktop-browser/extension')
const binding = JSON.parse(readFileSync(join(profile, 'external-host-binding.json')))
const envFile = Object.fromEntries(readFileSync('.env.local', 'utf8').split('\n').filter(l => l && !l
  .startsWith('#') && l.includes('=')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)
  .trim()
]))
const source = JSON.parse(readFileSync('.local/desktop/config.json'))
writeFileSync(join(profile, 'config.json'), JSON.stringify({
  apiKey: source.apiKey,
  deviceName: 'Browser Ticket 02 isolated smoke',
  uploadIntervalMinutes: 1,
  windowTitleObservationEnabled: false,
  interactionSignalEnabled: false,
  inputEventRecordingEnabled: false
}), {
  mode: 0o600
})
const chromeData = join(work, 'chrome-profile-' + Date.now());
let extensionId;
const children = [],
  report = {};
let desktop, chrome, ws, pageServer, container

function child(cmd, args, env, name, cwd = root) {
  const c = spawn(cmd, args, {
    cwd,
    env: {
      ...process.env,
      ...env
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  children.push(c);
  let output = '';
  c.stdout.on('data', b => {
    output += b
  });
  c.stderr.on('data', b => {
    output += b
  });
  c.on('exit', () => writeFileSync(join(work, name + '.log'), output));
  return c
}
async function until(fn, label, ms = 30000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const value = await fn();
    if (value) return value;
    await delay(300)
  }
  throw Error('timeout: ' + label)
}
async function freePort() {
  const s = createServer();
  await new Promise(r => s.listen(0, '127.0.0.1', r));
  const p = s.address().port;
  await new Promise(r => s.close(r));
  return p
}
let seq = 1;
const pending = new Map()

function cdp(method, params = {}, sessionId) {
  return new Promise((ok, no) => {
    const id = seq++,
      timer = setTimeout(() => {
        pending.delete(id);
        no(Error('CDP timeout ' + method))
      }, 10000);
    pending.set(id, {
      ok: v => {
        clearTimeout(timer);
        ok(v)
      },
      no: e => {
        clearTimeout(timer);
        no(e)
      }
    });
    ws.send(JSON.stringify({
      id,
      method,
      params,
      ...(sessionId ? {
        sessionId
      } : {})
    }))
  })
}
async function startChrome() {
  const data = chromeData;
  mkdirSync(data, {
    recursive: true
  });
  rmSync(join(data, 'DevToolsActivePort'), {
    force: true
  });
  chrome = child('/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', ['--headless=new',
    '--enable-extensions', '--no-first-run', '--no-default-browser-check',
    '--enable-unsafe-extension-debugging', '--remote-debugging-port=0', `--user-data-dir=${data}`,
    'about:blank'
  ], {}, 'chrome');
  await until(() => existsSync(join(data, 'DevToolsActivePort')), 'Chrome');
  const [port, path] = readFileSync(join(data, 'DevToolsActivePort'), 'utf8').trim().split('\n');
  ws = new WebSocket(`ws://127.0.0.1:${port}${path}`);
  await new Promise((r, j) => {
    ws.onopen = r;
    ws.onerror = j
  });
  ws.onmessage = ({
    data
  }) => {
    const m = JSON.parse(data),
      p = pending.get(m.id);
    if (p) {
      pending.delete(m.id);
      m.error ? p.no(Error(JSON.stringify(m.error))) : p.ok(m.result)
    }
  };
  const setting = (await cdp('Target.createTarget', {
      url: 'chrome://extensions'
    })).targetId,
    settingSession = (await cdp('Target.attachToTarget', {
      targetId: setting,
      flatten: true
    })).sessionId;
  await delay(700);
  await cdp('Runtime.evaluate', {
    expression: `(function enable(root){for(const e of root.querySelectorAll('*')){if(e.localName==='cr-toggle'&&e.id==='devMode'&&!e.checked)e.click();if(e.shadowRoot)enable(e.shadowRoot)}})(document)`
  }, settingSession);
  extensionId = (await cdp('Extensions.loadUnpacked', {
    path: extension
  })).id;
}
async function evaluate(expression) {
  const worker = await until(async () => (await cdp('Target.getTargets')).targetInfos.find(t => t.type ===
    'service_worker' && t.url.startsWith(`chrome-extension://${extensionId}/`)), 'worker');
  const session = (await cdp('Target.attachToTarget', {
    targetId: worker.targetId,
    flatten: true
  })).sessionId;
  try {
    const result = await cdp('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true
    }, session);
    if (result.exceptionDetails) throw Error(JSON.stringify(result.exceptionDetails));
    return result.result.value
  } finally {
    await cdp('Target.detachFromTarget', {
      sessionId: session
    })
  }
}
async function flush() {
  await evaluate("chrome.alarms.create('heartbeat-flush',{when:Date.now()+100})");
  await delay(800)
}
async function stopDesktop() {
  if (!desktop || desktop.exitCode !== null) return;
  writeFileSync(join(profile, 'desktop-stop'), '');
  await until(() => desktop.exitCode !== null, 'Desktop clean stop', 60000);
  assert.equal(desktop.exitCode, 0)
}
async function startDesktop(api) {
  rmSync(join(profile, 'desktop-stop'), {
    force: true
  });
  desktop = child('dotnet', ['run', '--no-build', '--project', 'collection/desktop/Heartbeat.Desktop.Mac',
    '--', '--development', '--data-directory', profile
  ], {
    HEARTBEAT_API_BASE_URL: api,
    HEARTBEAT_SHOW_SETTINGS_ON_START: '0'
  }, 'desktop');
  await until(async () => {
    try {
      return (await fetch(
        `http://127.0.0.1:${binding.port}/v1/collector-bindings/${binding.profileId}/${binding.token}`
        )).ok
    } catch {
      return false
    }
  }, 'Desktop binding')
}
try {
  container = 'heartbeat-browser-02-' + Date.now();
  execFileSync('docker', ['run', '-d', '--name', container, '-e', 'POSTGRES_PASSWORD=smoke-local', '-e',
    'POSTGRES_DB=heartbeat', '-p', '127.0.0.1::5432', 'postgres:18-alpine'
  ], {
    stdio: 'pipe'
  });
  const pgPort = execFileSync('docker', ['port', container, '5432/tcp'], {
    encoding: 'utf8'
  }).trim().split(':').at(-1);
  await until(() => {
    try {
      execFileSync('docker', ['exec', container, 'pg_isready', '-U', 'postgres'], {
        stdio: 'pipe'
      });
      return true
    } catch {
      return false
    }
  }, 'PostgreSQL');
  const api = 'http://127.0.0.1:' + await freePort();
  report.analytics = api;
  const backend = child('dotnet', [resolve(
  'server/Heartbeat.Server/bin/Debug/net10.0/Heartbeat.Server.dll')], {
    ASPNETCORE_ENVIRONMENT: 'Development',
    ASPNETCORE_URLS: api,
    ConnectionStrings__DefaultConnection: `Host=127.0.0.1;Port=${pgPort};Database=heartbeat;Username=postgres;Password=smoke-local`,
    AuthService__Authority: envFile.AUTH_AUTHORITY,
    AuthService__Issuer: envFile.AUTH_ISSUER,
    AuthService__Audience: envFile.AUTH_AUDIENCE,
    AuthService__OidcIssuer: envFile.AUTH_OIDC_ISSUER,
    AuthService__OidcClientId: envFile.AUTH_OIDC_CLIENT_ID
  }, 'analytics', resolve('server/Heartbeat.Server'));
  await until(async () => {
    try {
      return (await fetch(api + '/openapi/v1.json')).ok
    } catch {
      return false
    }
  }, 'Analytics', 60000);
  writeFileSync(join(work, 'openapi.json'), await (await fetch(api + '/openapi/v1.json')).text());
  const auth = await fetch('https://auth.shenxianovo.com/api/v1/apikeys/exchange', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      apiKey: source.apiKey
    })
  });
  assert.equal(auth.status, 200, 'existing development credential exchange');
  const {
    accessToken
  } = await auth.json();
  const claims = JSON.parse(Buffer.from(accessToken.split('.')[1], 'base64url').toString());
  const username = 'browser-smoke';
  const owner = claims.sub.replaceAll("'", "''");
  execFileSync('docker', ['exec', container, 'psql', '-U', 'postgres', '-d', 'heartbeat', '-v',
    'ON_ERROR_STOP=1', '-c',
    `INSERT INTO "Users" ("Id","Username","LastSeenAt","IsPublic") VALUES ('${owner}','${username}',now(),false)`
  ], {
    stdio: 'pipe'
  });
  const read = async (path) => {
    const r = await fetch(api + path, {
      headers: {
        Authorization: 'Bearer ' + accessToken
      }
    });
    if (!r.ok) return [];
    return r.json()
  };
  await startDesktop(api);
  await startChrome();
  pageServer = createServer((req, res) => res.end('<html><title>Browser Target verification</title><body>' +
    req.url + '</body></html>'));
  await new Promise(r => pageServer.listen(0, '127.0.0.1', r));
  const page = `http://127.0.0.1:${pageServer.address().port}`;
  const windows = await evaluate(
    `Promise.all([chrome.windows.create({url:'${page}/ticket02-a'}),chrome.windows.create({url:'${page}/ticket02-b'})]).then(ws=>ws.map(w=>w.id))`
    );
  await delay(1200);
  await flush();
  const factsPath = `/api/v1/users/${encodeURIComponent(username)}/facts/segments`;
  let facts = await until(async () => {
    const all = await read(factsPath);
    return all.filter(f => f.source === 'browser' && f.payload.activityKey?.startsWith(page +
      '/ticket02-')).length >= 2 ? all : false
  }, 'two Browser windows reach Analytics', 100000);
  const browser = facts.filter(f => f.source === 'browser' && f.payload.activityKey?.startsWith(page +
    '/ticket02-'));
  assert.equal(new Set(browser.map(f => f.targetId)).size, 1);
  assert.equal(new Set(browser.map(f => f.payload.attributes.windowId)).size, 2);
  assert.ok(browser.every(f => f.observerId && f.targetKind === 'application-context'));
  const observer = browser[0].observerId,
    target = browser[0].targetId;
  report.twoWindows = true;
  report.observerId = observer;
  report.targetId = target;
  report.deviceId = browser[0].deviceId;
  report.appId = browser[0].appId;
  report.factIds = browser.map(f => f.factId);
  const filtered = await read(factsPath + `?deviceId=${report.deviceId}&appId=${report.appId}`);
  assert.ok(browser.every(b => filtered.some(f => f.id === b.id)));
  report.deviceAndAppQuery = true;
  console.log('Two actual Chrome windows reached isolated Analytics with one context.');
  const closedWindowFact = browser.find(f => String(f.payload.attributes.windowId) === String(windows[0]));
  assert.ok(closedWindowFact);
  await evaluate(`chrome.windows.remove(${windows[0]})`);
  await flush();
  assert.ok((await read(factsPath)).some(f => f.factId === closedWindowFact.factId));
  report.closedWindowHistory = true;
  await stopDesktop();
  await evaluate(`chrome.windows.create({url:'${page}/ticket02-offline'})`);
  await delay(800);
  await flush();
  const storage = await evaluate(
    "chrome.storage.local.get(['pendingSegments','browserCollectorExternalHostIdentity'])");
  const offline = Object.values(storage.pendingSegments).find(f => f.activityKey?.includes(
    '/ticket02-offline'));
  assert.ok(offline?.target && offline.observerId === observer);
  report.offlineFactId = offline.id;
  ws.close();
  chrome.kill('SIGTERM');
  await until(() => chrome.exitCode !== null, 'Chrome stop');
  await startDesktop(api);
  await startChrome();
  await delay(800);
  await evaluate("chrome.storage.session.set({backoff:{fails:0,nextAttemptAt:0}})");
  await flush();
  const recovered = await until(async () => {
    const all = await read(factsPath);
    return all.find(f => f.factId === offline.id)
  }, 'offline snapshot after real Chrome/Desktop restart', 100000);
  assert.equal(recovered.observerId, observer);
  assert.equal(recovered.targetId, target);
  report.browserAndDesktopRestart = true;
  report.offlineReplay = true;
  const state = await evaluate("chrome.storage.local.get('browserCollectorExternalHostIdentity')");
  assert.equal(state.browserCollectorExternalHostIdentity, observer);
  report.installationStable = true;
  report.realBrowser = 'Google Chrome headless, real MV3 extension and windows API';
  report.completedAt = new Date().toISOString();
  console.log(JSON.stringify(report));
} finally {
  await stopDesktop().catch(e => {
    report.shutdownError = e.message
  });
  ws?.close();
  for (const c of children.reverse())
    if (c.exitCode === null) c.kill('SIGTERM');
  await delay(1000);
  for (const c of children)
    if (c.exitCode === null) c.kill('SIGKILL');
  pageServer?.close();
  if (container) execFileSync('docker', ['rm', '-f', container], {
    stdio: 'pipe'
  });
  writeFileSync(join(work, 'smoke-report.json'), JSON.stringify(report, null, 2));
}
