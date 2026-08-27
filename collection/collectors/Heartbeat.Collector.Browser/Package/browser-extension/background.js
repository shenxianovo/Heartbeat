import { l as loadConfig } from "./assets/config-CudPlTIo.js";
const ROTATE_AFTER_MS = 23 * 60 * 60 * 1e3;
function emptyState() {
  return { open: {} };
}
function applyEvent(state, ev, deps2) {
  const cur = state.open[ev.windowId];
  if (ev.kind === "windowClosed") {
    if (!cur) return { state, out: [] };
    const open = { ...state.open };
    delete open[ev.windowId];
    return { state: { open }, out: [snapshotOf(cur, ev.at, deps2, true)] };
  }
  const key = deps2.identityKeyOf(ev.url);
  if (cur && cur.identityKey === key) {
    const open = { ...state.open, [ev.windowId]: { ...cur, url: ev.url, title: ev.title } };
    return { state: { open }, out: [] };
  }
  const out = cur ? [snapshotOf(cur, ev.at, deps2, true)] : [];
  const next = {
    id: deps2.newId(),
    identityKey: key,
    url: ev.url,
    title: ev.title,
    windowId: ev.windowId,
    startTime: ev.at
  };
  return { state: { open: { ...state.open, [ev.windowId]: next } }, out };
}
function flush(state, now, deps2) {
  const out = [];
  let open = state.open;
  let copied = false;
  for (const [wid, a] of Object.entries(state.open)) {
    const isFinal = now - a.startTime >= ROTATE_AFTER_MS;
    out.push(snapshotOf(a, now, deps2, isFinal));
    if (isFinal) {
      if (!copied) {
        open = { ...open };
        copied = true;
      }
      open[Number(wid)] = { ...a, id: deps2.newId(), startTime: now };
    }
  }
  return { state: copied ? { open } : state, out };
}
function snapshotOf(a, endMs, deps2, isFinal) {
  return {
    id: a.id,
    source: "browser",
    identityKey: a.identityKey,
    ...deps2.appHint === void 0 ? {} : { appHint: deps2.appHint },
    title: a.title,
    startTime: new Date(a.startTime).toISOString(),
    endTime: new Date(Math.max(endMs, a.startTime)).toISOString(),
    isFinal,
    attributes: { url: a.url, domain: deps2.domainOf(a.url), site: deps2.siteOf(a.url), windowId: a.windowId }
  };
}
function identityKeyOf(rawUrl) {
  let u;
  try {
    u = new URL(rawUrl);
  } catch {
    return rawUrl;
  }
  if (u.origin === "null") {
    return u.href.split("#")[0].split("?")[0];
  }
  const path = u.pathname !== "/" && u.pathname.endsWith("/") ? u.pathname.slice(0, -1) : u.pathname;
  return u.origin + path;
}
function domainOf(rawUrl) {
  try {
    return new URL(rawUrl).hostname;
  } catch {
    return "";
  }
}
const MULTI_PART_SUFFIXES = /* @__PURE__ */ new Set([
  "com.cn",
  "net.cn",
  "org.cn",
  "gov.cn",
  "edu.cn",
  "ac.cn",
  "co.uk",
  "org.uk",
  "ac.uk",
  "gov.uk",
  "co.jp",
  "ne.jp",
  "or.jp",
  "ac.jp",
  "go.jp",
  "com.tw",
  "org.tw",
  "edu.tw",
  "com.hk",
  "org.hk",
  "edu.hk",
  "com.au",
  "net.au",
  "org.au",
  "edu.au",
  "co.kr",
  "or.kr",
  "ac.kr",
  "com.br",
  "org.br",
  "co.in",
  "org.in",
  "com.sg",
  "edu.sg"
]);
function siteOf(rawUrl) {
  let host;
  try {
    host = new URL(rawUrl).hostname;
  } catch {
    return "";
  }
  if (host.length === 0) return "";
  if (host.startsWith("[")) return host;
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host)) return host;
  const labels = host.split(".");
  if (labels.length === 1) return host;
  const lastTwo = labels.slice(-2).join(".");
  if (labels.length >= 3 && MULTI_PART_SUFFIXES.has(lastTwo)) return labels.slice(-3).join(".");
  return lastTwo;
}
function uuidv7(nowMs = Date.now()) {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  const ts = BigInt(nowMs);
  bytes[0] = Number(ts >> 40n & 0xffn);
  bytes[1] = Number(ts >> 32n & 0xffn);
  bytes[2] = Number(ts >> 24n & 0xffn);
  bytes[3] = Number(ts >> 16n & 0xffn);
  bytes[4] = Number(ts >> 8n & 0xffn);
  bytes[5] = Number(ts & 0xffn);
  bytes[6] = bytes[6] & 15 | 112;
  bytes[8] = bytes[8] & 63 | 128;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
const PORT_RANGE = 10;
const REQUIRED_HUB_PROTOCOL = 2;
const PROBE_TIMEOUT_MS = 1500;
async function postSegments(port, segments) {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/v1/segments`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ segments })
    });
    if (res.ok) return "ok";
    return res.status >= 400 && res.status < 500 ? "rejected" : "unreachable";
  } catch {
    return "unreachable";
  }
}
async function probeHub(port) {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/v1/hub`, {
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS)
    });
    if (!res.ok) return false;
    const body = await res.json();
    return body.app === "heartbeat" && body.proto === REQUIRED_HUB_PROTOCOL;
  } catch {
    return false;
  }
}
async function fetchCollectorConfig(port, source, flushPeriodMs) {
  try {
    const url = `http://127.0.0.1:${port}/v1/collectors/${encodeURIComponent(source)}/config?flushPeriodMs=${flushPeriodMs}`;
    const res = await fetch(url, { signal: AbortSignal.timeout(PROBE_TIMEOUT_MS) });
    if (!res.ok) return null;
    const body = await res.json();
    return { enabled: body.enabled !== false };
  } catch {
    return null;
  }
}
async function discoverHub(basePort) {
  const ports = Array.from({ length: PORT_RANGE }, (_, i) => basePort + i).filter(
    (p) => p <= 65535
  );
  const results = await Promise.all(ports.map(probeHub));
  const index = results.findIndex(Boolean);
  return index >= 0 ? ports[index] : null;
}
async function findCompatibleHub(basePort, targetPort) {
  if (await probeHub(targetPort)) return targetPort;
  return discoverHub(basePort);
}
async function postToHub(basePort, targetPort, segments) {
  const found = await findCompatibleHub(basePort, targetPort);
  if (found === null) return { result: "unreachable", port: targetPort };
  return { result: await postSegments(found, segments), port: found };
}
const BACKOFF_BASE_MS = 3e4;
const BACKOFF_MAX_MS = 10 * 6e4;
const noBackoff = { fails: 0, nextAttemptAt: 0 };
function backoffAfterFailure(state, now) {
  const fails = state.fails + 1;
  const delay = Math.min(BACKOFF_BASE_MS * 2 ** (fails - 1), BACKOFF_MAX_MS);
  return { fails, nextAttemptAt: now + delay };
}
function shouldSkipAttempt(state, now) {
  return now < state.nextAttemptAt;
}
const EXACT_BRANDS = /* @__PURE__ */ new Map([
  ["google chrome", "chrome"],
  ["microsoft edge", "edge"],
  ["brave", "brave"],
  ["opera", "opera"],
  ["opera gx", "opera"],
  ["vivaldi", "vivaldi"],
  ["firefox", "firefox"]
]);
function detectBrowserAppHint(signals) {
  const candidates = /* @__PURE__ */ new Set();
  let hasUnknownBrand = false;
  for (const rawBrand of signals.brands ?? []) {
    const brand = rawBrand.trim().toLowerCase();
    const exact = EXACT_BRANDS.get(brand);
    if (exact) {
      candidates.add(exact);
    } else if (!isGenericClientHintBrand(brand)) {
      hasUnknownBrand = true;
    }
  }
  if (signals.hasBraveApi) candidates.add("brave");
  const ua = signals.userAgent ?? "";
  if (/\bEdg(?:A|iOS)?\//i.test(ua)) candidates.add("edge");
  if (/\bOPR\//i.test(ua)) candidates.add("opera");
  if (/\bVivaldi\//i.test(ua)) candidates.add("vivaldi");
  if (/\bFirefox\//i.test(ua)) candidates.add("firefox");
  if (hasUnknownBrand || candidates.size !== 1) return void 0;
  return candidates.values().next().value;
}
function isGenericClientHintBrand(brand) {
  if (brand === "" || brand === "chromium") return true;
  return brand.replace(/[^a-z0-9]/g, "") === "notabrand";
}
const MAX_QUEUED = 5e3;
function enqueueBounded(current, snapshots, limit = MAX_QUEUED) {
  const queue = { ...current };
  const overflow = [];
  let queuedCount = Object.keys(queue).length;
  for (const snapshot of snapshots) {
    if (queue[snapshot.id] === void 0 && queuedCount >= limit) {
      overflow.push(snapshot);
    } else {
      if (queue[snapshot.id] === void 0) queuedCount += 1;
      queue[snapshot.id] = snapshot;
    }
  }
  return { queue, overflow };
}
function normalizeQueuedSnapshots(stored, currentAppHint) {
  return Object.fromEntries(
    Object.entries(stored).map(([id, { appName: _legacyAppName, ...snapshot }]) => [
      id,
      {
        ...snapshot,
        isFinal: snapshot.isFinal === true,
        ...snapshot.appHint === void 0 && currentAppHint !== void 0 ? { appHint: currentAppHint } : {}
      }
    ])
  );
}
const ROUTE = "/v1/collector-protocol/browser";
const ARTIFACT_ID = "browser.extension";
const TEST_ARTIFACT_HASH = `sha256:${"0".repeat(64)}`;
async function browserArtifactHash() {
  if (typeof chrome === "undefined" || !chrome.runtime?.getURL) return TEST_ARTIFACT_HASH;
  const response = await fetch(chrome.runtime.getURL("package-metadata.json"));
  if (!response.ok) throw new Error("browser Package metadata is unavailable");
  const metadata = await response.json();
  if (typeof metadata.artifactHash !== "string" || !/^sha256:[0-9a-f]{64}$/.test(metadata.artifactHash)) {
    throw new Error("browser Package metadata has an invalid artifact hash");
  }
  return metadata.artifactHash;
}
const DEFAULT_LIMITS = {
  maxFactsPerBatch: 500,
  maxBatchBytes: 1048576
};
const acknowledgedStatuses = /* @__PURE__ */ new Set(["committed", "duplicate", "superseded"]);
function isUuidV7(value) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}
function snapshotRevision(snapshot) {
  const revision = Date.parse(snapshot.endTime);
  return Number.isSafeInteger(revision) && revision > 0 ? revision : 1;
}
function toProtocolFact(snapshot, streamId) {
  if (!isUuidV7(snapshot.id)) return null;
  return {
    streamId,
    schemaRevision: 1,
    factId: snapshot.id,
    revision: snapshotRevision(snapshot),
    observedAt: null,
    recordState: "present",
    time: {
      start: snapshot.startTime,
      end: snapshot.endTime,
      isFinal: snapshot.isFinal
    },
    payload: {
      identityKey: snapshot.identityKey,
      title: snapshot.title,
      attributes: snapshot.attributes
    }
  };
}
function acknowledgedSnapshotIds(snapshots, acknowledgement) {
  return acknowledgement.results.filter(
    (result) => Number.isInteger(result.index) && result.index >= 0 && result.index < snapshots.length && acknowledgedStatuses.has(result.status)
  ).map((result) => snapshots[result.index].id);
}
async function openBrowserProtocolSession(port, appHint, attempt, applySpec) {
  try {
    const hello = await fetch(`http://127.0.0.1:${port}${ROUTE}/hello`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(message(
        "heartbeat.collector.bootstrap/1",
        "activation.hello",
        attempt.helloMessageId,
        void 0,
        {
          artifactId: ARTIFACT_ID,
          artifactHash: await browserArtifactHash(),
          protocolMajors: [1],
          supportedCapabilities: {
            "facts.segment": [1],
            "diagnostics.stream-gap": [1]
          },
          appHint
        }
      ))
    });
    if (hello.status === 404) return "legacy-required";
    if (!hello.ok) return "rejected";
    const acceptedMessage = await hello.json();
    if (!isCorrelatedResponse(
      acceptedMessage,
      "heartbeat.collector.bootstrap/1",
      "activation.accepted",
      void 0,
      attempt.helloMessageId
    ) || !isUuidV7(acceptedMessage.body.activationId) || acceptedMessage.body.selectedProtocolMajor !== 1 || acceptedMessage.body.selectedCapabilities?.["facts.segment"] !== 1 || acceptedMessage.body.selectedCapabilities?.["diagnostics.stream-gap"] !== 1)
      return "rejected";
    const accepted = acceptedMessage.body;
    const initialize = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/initialize`,
      { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }
    );
    if (!initialize.ok) return "rejected";
    const initializeMessage = await initialize.json();
    if (!isCorrelatedResponse(
      initializeMessage,
      "heartbeat.collector/1",
      "activation.initialize",
      accepted.activationId,
      void 0
    )) return "rejected";
    const initialized = initializeMessage.body;
    if (initialized.spec.config.value.enabled === false) return "disabled";
    const flushPeriodMilliseconds = positiveInteger(initialized.spec.config.value.flushPeriodMs);
    if (flushPeriodMilliseconds === void 0 || flushPeriodMilliseconds < 3e4) return "rejected";
    if (positiveInteger(initialized.limits?.maxFactsPerBatch) === void 0 || positiveInteger(initialized.limits?.maxBatchBytes) === void 0)
      return "rejected";
    await applySpec?.({ enabled: true, flushPeriodMilliseconds });
    const initializedAck = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/initialized`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "activation.initialized",
          attempt.initializedMessageId,
          accepted.activationId,
          { appliedSpecRevision: initialized.spec.revision },
          initializeMessage.messageId
        ))
      }
    );
    if (!initializedAck.ok) return "rejected";
    const streams = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/streams`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "streams.open",
          attempt.streamsMessageId,
          accepted.activationId,
          {
            specRevision: initialized.spec.revision,
            bindings: [{ bindingId: "tabs", outputId: "activeTab", dimensions: {} }]
          }
        ))
      }
    );
    if (!streams.ok) return "rejected";
    const openedMessage = await streams.json();
    if (!isCorrelatedResponse(
      openedMessage,
      "heartbeat.collector/1",
      "streams.opened",
      accepted.activationId,
      attempt.streamsMessageId
    )) return "rejected";
    const opened = openedMessage.body;
    const stream = opened.streams.tabs;
    if (!stream?.streamId) return "rejected";
    const ready = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/ready`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "activation.ready",
          attempt.readyMessageId,
          accepted.activationId,
          {
            appliedSpecRevision: initialized.spec.revision
          }
        ))
      }
    );
    if (!ready.ok) return "rejected";
    const readyMessage = await ready.json();
    if (!isCorrelatedResponse(
      readyMessage,
      "heartbeat.collector/1",
      "activation.readyAck",
      accepted.activationId,
      attempt.readyMessageId
    )) return "rejected";
    const readyAcknowledgement = readyMessage.body;
    if (!readyAcknowledgement.lease?.token) return null;
    return {
      port,
      activationId: accepted.activationId,
      leaseToken: readyAcknowledgement.lease.token,
      streamId: stream.streamId,
      specRevision: initialized.spec.revision,
      expiresAt: readyAcknowledgement.lease.expiresAt,
      limits: normalizeLimits(initialized.limits),
      flushPeriodMilliseconds
    };
  } catch {
    return null;
  }
}
async function renewBrowserProtocolSession(session) {
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/renew`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ leaseToken: session.leaseToken })
      }
    );
    if (!response.ok) return null;
    const lease = await response.json();
    return lease.token === session.leaseToken && typeof lease.expiresAt === "string" ? { ...session, expiresAt: lease.expiresAt } : null;
  } catch {
    return null;
  }
}
async function publishBrowserFacts(session, snapshots, previousAttempt, persistAttempt) {
  const limits = normalizeLimits(session.limits);
  const maxFacts = Math.max(1, Math.min(limits.maxFactsPerBatch, 500));
  const batch = previousAttempt?.snapshots ?? takeBatchWithinByteLimit(snapshots, session, maxFacts);
  if (snapshots.length > 0 && batch.length === 0) return { kind: "unavailable" };
  const facts = batch.map((snapshot) => toProtocolFact(snapshot, session.streamId));
  if (facts.some((fact) => fact === null)) return { kind: "legacy-required" };
  if (facts.length === 0) {
    return {
      kind: "acked",
      acknowledgedIds: [],
      acknowledgedRevisions: {},
      rejectedRevisions: {},
      session
    };
  }
  const attempt = previousAttempt ?? { messageId: uuidv7(), snapshots: batch };
  await persistAttempt?.(attempt);
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/facts`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "facts.publish",
          attempt.messageId,
          session.activationId,
          {
            leaseToken: session.leaseToken,
            facts
          }
        ))
      }
    );
    if (response.status === 403) return { kind: "disabled" };
    if (!response.ok) return { kind: "unavailable" };
    const acknowledgementMessage = await response.json();
    if (!isCorrelatedResponse(
      acknowledgementMessage,
      "heartbeat.collector/1",
      "facts.ack",
      session.activationId,
      attempt.messageId
    ) || !hasCompleteFactResults(acknowledgementMessage.body, batch.length)) {
      throw new Error("facts.ack is malformed or does not match the publish attempt");
    }
    const acknowledgement = acknowledgementMessage.body;
    const acknowledgedIds = acknowledgedSnapshotIds(batch, acknowledgement);
    const rejected = acknowledgement.results.filter((result) => Number.isInteger(result.index) && result.index >= 0 && result.index < batch.length && result.status === "rejected");
    const retryResults = acknowledgement.results.filter((result) => result.status === "retry");
    const retries = retryResults.map((result) => positiveInteger(result.retryAfterMs) ?? 1e3);
    const nextPublishAttempt = retryResults.length === 0 ? void 0 : {
      messageId: uuidv7(),
      snapshots: retryResults.map((result) => batch[result.index])
    };
    if (nextPublishAttempt !== void 0) await persistAttempt?.(nextPublishAttempt);
    return {
      kind: "acked",
      acknowledgedIds,
      acknowledgedRevisions: Object.fromEntries(
        acknowledgedIds.map((id) => [
          id,
          snapshotRevision(batch.find((snapshot) => snapshot.id === id))
        ])
      ),
      rejectedRevisions: Object.fromEntries(rejected.map((result) => [
        batch[result.index].id,
        snapshotRevision(batch[result.index])
      ])),
      ...retries.length === 0 ? {} : { retryAfterMilliseconds: Math.max(...retries) },
      ...nextPublishAttempt === void 0 ? {} : { nextPublishAttempt },
      session
    };
  } catch {
    return { kind: "unavailable", publishAttempt: attempt, session };
  }
}
async function uploadWithBrowserProtocol(port, appHint, snapshots, previousSession, previousActivationAttempt, previousPublishAttempt, persistActivationAttempt, persistPublishAttempt, applySpec, pendingGap, persistGapAttempt) {
  if (!appHint) return { kind: "legacy-required" };
  if (snapshots.some((snapshot) => !isUuidV7(snapshot.id))) return { kind: "legacy-required" };
  const renewed = previousSession?.port === port ? await renewBrowserProtocolSession(previousSession) : null;
  const activationAttempt = previousActivationAttempt ?? {
    helloMessageId: uuidv7(),
    initializedMessageId: uuidv7(),
    streamsMessageId: uuidv7(),
    readyMessageId: uuidv7()
  };
  if (renewed === null) await persistActivationAttempt?.(activationAttempt);
  const session = renewed ?? await openBrowserProtocolSession(port, appHint, activationAttempt, applySpec);
  if (session === "disabled") return { kind: "disabled" };
  if (session === "legacy-required") return { kind: "legacy-required" };
  if (session === "rejected") return { kind: "unavailable" };
  if (session === null) return { kind: "unavailable", activationAttempt };
  let gapAcknowledged = false;
  if (pendingGap !== void 0) {
    const gapResult = await reportBrowserGap(session, pendingGap, persistGapAttempt);
    if (gapResult !== "acked") {
      return {
        kind: "unavailable",
        session
      };
    }
    gapAcknowledged = true;
  }
  const result = await publishBrowserFacts(
    session,
    snapshots,
    renewed === null && previousSession !== void 0 ? void 0 : previousPublishAttempt,
    persistPublishAttempt
  );
  return result.kind === "acked" || result.kind === "unavailable" ? { ...result, ...gapAcknowledged ? { gapAcknowledged: true } : {} } : result;
}
async function reportBrowserGap(session, gap, persistAttempt) {
  const attempt = gap.activationId === session.activationId && gap.messageId !== void 0 ? gap : { ...gap, activationId: session.activationId, messageId: uuidv7() };
  await persistAttempt?.(attempt);
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/gap`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "stream.gap",
          attempt.messageId,
          session.activationId,
          {
            leaseToken: session.leaseToken,
            streamId: session.streamId,
            gap: {
              start: attempt.start,
              end: attempt.end,
              reason: attempt.reason,
              estimatedFactsLost: attempt.estimatedFactsLost
            }
          }
        ))
      }
    );
    if (!response.ok) return "rejected";
    const acknowledgement = await response.json();
    return acknowledgement.protocol === "heartbeat.collector/1" && acknowledgement.type === "stream.gapAck" && acknowledgement.activationId === session.activationId && acknowledgement.replyTo === attempt.messageId && acknowledgement.body.streamId === session.streamId ? "acked" : "unavailable";
  } catch {
    return "unavailable";
  }
}
function takeBatchWithinByteLimit(snapshots, session, maxFacts) {
  const limit = normalizeLimits(session.limits).maxBatchBytes;
  const batch = [];
  for (const snapshot of snapshots.slice(0, maxFacts)) {
    const candidate = [...batch, snapshot];
    const facts = candidate.map((item) => toProtocolFact(item, session.streamId));
    const logicalMessage = {
      protocol: "heartbeat.collector/1",
      type: "facts.publish",
      messageId: "00000000-0000-7000-8000-000000000000",
      activationId: session.activationId,
      body: { facts }
    };
    if (dotNetJsonUpperBoundBytes(logicalMessage) > limit) {
      if (batch.length === 0) continue;
      break;
    }
    batch.push(snapshot);
  }
  return batch;
}
function dotNetJsonUpperBoundBytes(value) {
  const json = JSON.stringify(value);
  let bytes = 0;
  for (let index = 0; index < json.length; index += 1) {
    const code = json.charCodeAt(index);
    bytes += code > 127 || code === 43 || code === 60 || code === 62 || code === 38 || code === 39 ? 6 : 1;
  }
  return bytes;
}
function normalizeLimits(limits) {
  return {
    maxFactsPerBatch: positiveInteger(limits?.maxFactsPerBatch) ?? DEFAULT_LIMITS.maxFactsPerBatch,
    maxBatchBytes: positiveInteger(limits?.maxBatchBytes) ?? DEFAULT_LIMITS.maxBatchBytes
  };
}
function positiveInteger(value) {
  return Number.isSafeInteger(value) && Number(value) > 0 ? Number(value) : void 0;
}
function isCorrelatedResponse(response, protocol, type, activationId, replyTo) {
  return response?.protocol === protocol && response.type === type && isUuidV7(response.messageId) && response.activationId === activationId && response.replyTo === replyTo && response.body !== void 0;
}
function hasCompleteFactResults(acknowledgement, factCount) {
  if (!Array.isArray(acknowledgement?.results) || acknowledgement.results.length !== factCount)
    return false;
  const indices = acknowledgement.results.map((result) => result.index).sort((left, right) => left - right);
  if (!indices.every((index, position) => index === position)) return false;
  return acknowledgement.results.every((result) => {
    if (!["committed", "duplicate", "superseded", "rejected", "retry"].includes(result.status)) return false;
    if (result.status === "retry") return positiveInteger(result.retryAfterMs) !== void 0;
    return result.retryAfterMs === void 0;
  });
}
function message(protocol, type, messageId, activationId, body, replyTo) {
  return {
    protocol,
    type,
    messageId,
    ...activationId === void 0 ? {} : { activationId },
    ...replyTo === void 0 ? {} : { replyTo },
    body
  };
}
const FLUSH_PERIOD_MINUTES = 0.5;
const FLUSH_PERIOD_MS = FLUSH_PERIOD_MINUTES * 6e4;
const SOURCE = "browser";
const STATE_KEY = "foldState";
const QUEUE_KEY = "pendingSegments";
const BACKOFF_KEY = "backoff";
const HUB_PORT_KEY = "hubPort";
const PROTOCOL_SESSION_KEY = "collectorProtocolSession";
const PROTOCOL_ACTIVATION_ATTEMPT_KEY = "collectorProtocolActivationAttempt";
const PROTOCOL_PUBLISH_ATTEMPT_KEY = "collectorProtocolPublishAttempt";
const FLUSH_PERIOD_KEY = "browserCollectorFlushPeriodMs";
const DEAD_LETTER_KEY = "browserCollectorDeadLetters";
const PENDING_GAP_KEY = "browserCollectorPendingGap";
const DESIRED_ENABLED_KEY = "browserCollectorDesiredEnabled";
const ALARM_NAME = "heartbeat-flush";
const deps = {
  newId: uuidv7,
  identityKeyOf,
  domainOf,
  siteOf,
  appHint: detectAppHint()
};
function detectAppHint() {
  const nav = navigator;
  return detectBrowserAppHint({
    brands: nav.userAgentData?.brands?.map((b) => b.brand),
    userAgent: nav.userAgent,
    hasBraveApi: typeof nav.brave?.isBrave === "function"
  });
}
let chain = Promise.resolve();
function serialized(fn) {
  const next = chain.then(fn, fn);
  chain = next.catch(() => {
  });
  return next;
}
async function loadState() {
  const got = await chrome.storage.session.get(STATE_KEY);
  return got[STATE_KEY] ?? emptyState();
}
async function saveState(state) {
  await chrome.storage.session.set({ [STATE_KEY]: state });
}
async function loadQueue() {
  const got = await chrome.storage.local.get(QUEUE_KEY);
  const stored = got[QUEUE_KEY] ?? {};
  return normalizeQueuedSnapshots(stored, deps.appHint);
}
async function saveQueue(queue) {
  await chrome.storage.local.set({ [QUEUE_KEY]: queue });
}
async function loadPendingGaps() {
  const got = await chrome.storage.local.get(PENDING_GAP_KEY);
  const stored = got[PENDING_GAP_KEY];
  if (Array.isArray(stored)) return stored;
  return stored === void 0 ? [] : [stored];
}
async function savePendingGaps(gaps) {
  if (gaps.length === 0) await chrome.storage.local.remove(PENDING_GAP_KEY);
  else await chrome.storage.local.set({ [PENDING_GAP_KEY]: gaps });
}
async function recordBufferGap(snapshots) {
  if (snapshots.length === 0) return;
  const starts = snapshots.map((snapshot) => snapshot.startTime);
  const ends = snapshots.map((snapshot) => snapshot.endTime);
  const gap = {
    start: starts.sort()[0],
    end: ends.sort().at(-1),
    reason: "buffer_overflow",
    estimatedFactsLost: snapshots.length
  };
  await savePendingGaps([...await loadPendingGaps(), gap]);
}
async function persistFirstGapAttempt(gap) {
  const gaps = await loadPendingGaps();
  if (gaps.length === 0) return;
  gaps[0] = gap;
  await savePendingGaps(gaps);
}
async function appendDeadLetters(snapshots) {
  if (snapshots.length === 0) return;
  const got = await chrome.storage.local.get(DEAD_LETTER_KEY);
  const existing = Array.isArray(got[DEAD_LETTER_KEY]) ? got[DEAD_LETTER_KEY] : [];
  await chrome.storage.local.set({
    [DEAD_LETTER_KEY]: [...existing, ...snapshots].slice(-100)
  });
  console.warn(`[heartbeat] ${snapshots.length} 条 Fact 被 Hub 永久拒绝，已移入诊断 dead-letter`);
}
async function loadBackoff() {
  const got = await chrome.storage.session.get(BACKOFF_KEY);
  return got[BACKOFF_KEY] ?? noBackoff;
}
async function saveBackoff(state) {
  await chrome.storage.session.set({ [BACKOFF_KEY]: state });
}
async function loadHubPort(basePort) {
  const got = await chrome.storage.session.get(HUB_PORT_KEY);
  const port = Number(got[HUB_PORT_KEY]);
  return Number.isInteger(port) && port >= basePort ? port : basePort;
}
async function saveHubPort(port) {
  await chrome.storage.session.set({ [HUB_PORT_KEY]: port });
}
async function loadProtocolSession() {
  const got = await chrome.storage.session.get(PROTOCOL_SESSION_KEY);
  return got[PROTOCOL_SESSION_KEY];
}
async function saveProtocolSession(session) {
  if (session === void 0) await chrome.storage.session.remove(PROTOCOL_SESSION_KEY);
  else await chrome.storage.session.set({ [PROTOCOL_SESSION_KEY]: session });
}
async function loadProtocolActivationAttempt() {
  const got = await chrome.storage.session.get(PROTOCOL_ACTIVATION_ATTEMPT_KEY);
  return got[PROTOCOL_ACTIVATION_ATTEMPT_KEY];
}
async function saveProtocolActivationAttempt(attempt) {
  if (attempt === void 0) await chrome.storage.session.remove(PROTOCOL_ACTIVATION_ATTEMPT_KEY);
  else await chrome.storage.session.set({ [PROTOCOL_ACTIVATION_ATTEMPT_KEY]: attempt });
}
async function loadProtocolPublishAttempt() {
  const got = await chrome.storage.session.get(PROTOCOL_PUBLISH_ATTEMPT_KEY);
  return got[PROTOCOL_PUBLISH_ATTEMPT_KEY];
}
async function saveProtocolPublishAttempt(attempt) {
  if (attempt === void 0) await chrome.storage.session.remove(PROTOCOL_PUBLISH_ATTEMPT_KEY);
  else await chrome.storage.session.set({ [PROTOCOL_PUBLISH_ATTEMPT_KEY]: attempt });
}
async function desiredEnabled() {
  const got = await chrome.storage.session.get(DESIRED_ENABLED_KEY);
  return got[DESIRED_ENABLED_KEY] !== false;
}
async function saveDesiredEnabled(enabled) {
  await chrome.storage.session.set({ [DESIRED_ENABLED_KEY]: enabled });
}
async function desiredFlushPeriodMilliseconds() {
  const got = await chrome.storage.session.get(FLUSH_PERIOD_KEY);
  const value = Number(got[FLUSH_PERIOD_KEY]);
  return Number.isSafeInteger(value) && value >= 3e4 ? value : FLUSH_PERIOD_MS;
}
async function applyProtocolSpec(spec) {
  await saveDesiredEnabled(spec.enabled);
  await chrome.storage.session.set({ [FLUSH_PERIOD_KEY]: spec.flushPeriodMilliseconds });
  chrome.alarms.create(ALARM_NAME, {
    periodInMinutes: spec.flushPeriodMilliseconds / 6e4
  });
}
async function applyDesiredEnabled(enabled) {
  const wasEnabled = await desiredEnabled();
  await saveDesiredEnabled(enabled);
  if (!enabled) {
    await saveState(emptyState());
  } else if (!wasEnabled) {
    await reconcile();
  }
}
async function enqueue(snapshots) {
  if (snapshots.length === 0) return;
  const { queue, overflow } = enqueueBounded(await loadQueue(), snapshots);
  try {
    await saveQueue(queue);
  } catch (error) {
    console.warn("[heartbeat] outbox 写入失败，记录 Stream Gap", error);
    await recordBufferGap(snapshots);
    return;
  }
  await recordBufferGap(overflow);
}
async function handleEvent(ev) {
  if (!await desiredEnabled()) return;
  const state = await loadState();
  const { state: next, out } = applyEvent(state, ev, deps);
  if (next !== state) await saveState(next);
  await enqueue(out);
}
async function flushAndUpload() {
  if (await desiredEnabled()) {
    const state = await loadState();
    const { state: next, out } = flush(state, Date.now(), deps);
    if (next !== state) await saveState(next);
    await enqueue(out);
  }
  const backoff = await loadBackoff();
  const now = Date.now();
  if (shouldSkipAttempt(backoff, now)) return;
  const { port: basePort } = await loadConfig();
  const targetPort = await loadHubPort(basePort);
  const compatiblePort = await findCompatibleHub(basePort, targetPort);
  if (compatiblePort === null) {
    await saveBackoff(backoffAfterFailure(backoff, now));
    return;
  }
  if (compatiblePort !== targetPort) await saveHubPort(compatiblePort);
  const collectorConfig = await fetchCollectorConfig(
    compatiblePort,
    SOURCE,
    await desiredFlushPeriodMilliseconds()
  );
  if (collectorConfig?.enabled === false) {
    await applyDesiredEnabled(false);
    return;
  }
  if (collectorConfig?.enabled === true) await applyDesiredEnabled(true);
  const queue = await loadQueue();
  const items = Object.values(queue);
  const protocolResult = await uploadWithBrowserProtocol(
    compatiblePort,
    deps.appHint,
    items,
    await loadProtocolSession(),
    await loadProtocolActivationAttempt(),
    await loadProtocolPublishAttempt(),
    saveProtocolActivationAttempt,
    saveProtocolPublishAttempt,
    applyProtocolSpec,
    (await loadPendingGaps())[0],
    persistFirstGapAttempt
  );
  if ((protocolResult.kind === "acked" || protocolResult.kind === "unavailable") && protocolResult.gapAcknowledged === true) {
    const gaps = await loadPendingGaps();
    await savePendingGaps(gaps.slice(1));
  }
  if (protocolResult.kind === "acked") {
    const latestQueue = await loadQueue();
    const rejected = Object.entries(latestQueue).filter(([id, snapshot]) => protocolResult.rejectedRevisions[id] === snapshotRevision(snapshot)).map(([, snapshot]) => snapshot);
    await appendDeadLetters(rejected);
    const remaining = Object.fromEntries(
      Object.entries(latestQueue).filter(
        ([id, snapshot]) => protocolResult.acknowledgedRevisions[id] !== snapshotRevision(snapshot) && protocolResult.rejectedRevisions[id] !== snapshotRevision(snapshot)
      )
    );
    await saveQueue(remaining);
    await saveProtocolSession(protocolResult.session);
    await saveProtocolActivationAttempt(void 0);
    await saveProtocolPublishAttempt(protocolResult.nextPublishAttempt);
    if (protocolResult.retryAfterMilliseconds !== void 0) {
      await saveBackoff({
        fails: 0,
        nextAttemptAt: now + protocolResult.retryAfterMilliseconds
      });
    } else if (backoff.fails > 0) await saveBackoff(noBackoff);
    return;
  }
  if (protocolResult.kind === "disabled") {
    await saveProtocolActivationAttempt(void 0);
    await saveProtocolPublishAttempt(void 0);
    await applyDesiredEnabled(false);
    return;
  }
  if (protocolResult.kind === "unavailable") {
    if (protocolResult.activationAttempt !== void 0) {
      await saveProtocolActivationAttempt(protocolResult.activationAttempt);
    } else {
      await saveProtocolActivationAttempt(void 0);
    }
    if (protocolResult.publishAttempt !== void 0) {
      await saveProtocolPublishAttempt(protocolResult.publishAttempt);
      if (protocolResult.session !== void 0) await saveProtocolSession(protocolResult.session);
    } else {
      await saveProtocolPublishAttempt(void 0);
      await saveProtocolSession(void 0);
    }
    await saveBackoff(backoffAfterFailure(backoff, now));
    return;
  }
  await saveProtocolSession(void 0);
  await saveProtocolActivationAttempt(void 0);
  await saveProtocolPublishAttempt(void 0);
  if (items.length === 0) return;
  const { result, port } = await postToHub(basePort, compatiblePort, items);
  if (port !== compatiblePort) await saveHubPort(port);
  if (result === "ok") {
    await saveQueue({});
    if (backoff.fails > 0) await saveBackoff(noBackoff);
  } else if (result === "rejected") {
    console.warn(`[heartbeat] legacy hub 拒收 ${items.length} 条段，保留 outbox`);
  } else {
    await saveBackoff(backoffAfterFailure(backoff, now));
  }
}
async function reconcile() {
  if (!await desiredEnabled()) return;
  const tabs = await chrome.tabs.query({ active: true });
  const liveWindows = new Set(tabs.map((t) => t.windowId));
  const now = Date.now();
  const state = await loadState();
  for (const wid of Object.keys(state.open).map(Number)) {
    if (!liveWindows.has(wid)) await handleEvent({ kind: "windowClosed", windowId: wid, at: now });
  }
  for (const t of tabs) {
    if (t.url && t.windowId !== void 0) {
      await handleEvent({ kind: "activated", windowId: t.windowId, url: t.url, title: t.title ?? "", at: now });
    }
  }
}
chrome.tabs.onActivated.addListener(({ tabId, windowId }) => {
  void serialized(async () => {
    const tab = await chrome.tabs.get(tabId).catch(() => null);
    if (!tab?.url) return;
    await handleEvent({ kind: "activated", windowId, url: tab.url, title: tab.title ?? "", at: Date.now() });
  });
});
chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (!tab.active || !tab.url) return;
  if (changeInfo.url === void 0 && changeInfo.title === void 0) return;
  void serialized(
    () => handleEvent({ kind: "activated", windowId: tab.windowId, url: tab.url, title: tab.title ?? "", at: Date.now() })
  );
});
chrome.windows.onRemoved.addListener((windowId) => {
  void serialized(() => handleEvent({ kind: "windowClosed", windowId, at: Date.now() }));
});
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === ALARM_NAME) void serialized(flushAndUpload);
});
chrome.alarms.create(ALARM_NAME, { periodInMinutes: FLUSH_PERIOD_MINUTES });
void serialized(reconcile);
