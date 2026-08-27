// loopback hub 上报客户端（ADR-017 §1）：POST http://127.0.0.1:{port}/v1/segments。
// 采集器不持凭证、不知服务端地址——离线缓存、ApiKey、重试全在 Agent 侧复用。
//
// 端口发现：hub 基准端口被占时会向上顺延（与 Agent 侧 SegmentIngestWorker.PortRange 同约定），
// 采集器凭 GET /v1/hub 的身份应答在范围内定位真正的 hub——身份校验同时防止把
// 恰好占用端口的陌生服务的 4xx 误判为"hub 拒收"而丢队列。

import type { SegmentSnapshot } from './fold'

/** 与 Agent 侧 SegmentIngestWorker.PortRange 一致：基准端口起向上探测的端口数。 */
export const PORT_RANGE = 10

/** appHint loopback 契约首次可用的协议版本；必须精确匹配，避免跨破坏性版本误投。 */
export const REQUIRED_HUB_PROTOCOL = 2

/** 单端口探测超时：loopback 应答在毫秒级，超时即视为无人/非 hub。 */
const PROBE_TIMEOUT_MS = 1500

export type PostResult = 'ok' | 'rejected' | 'unreachable'

/**
 * - ok：hub 收下，队列可清。
 * - rejected（4xx）：hub 明确拒绝（校验失败/毒批次；将来 403 = 被停用，issue 04）——重传无意义，丢弃。
 * - unreachable（网络错误/5xx）：Agent 未运行或暂时故障——保留队列，下个周期重试。
 */
export async function postSegments(
  port: number,
  segments: SegmentSnapshot[],
): Promise<PostResult> {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/v1/segments`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ segments }),
    })
    if (res.ok) return 'ok'
    return res.status >= 400 && res.status < 500 ? 'rejected' : 'unreachable'
  } catch {
    return 'unreachable'
  }
}

/** GET /v1/hub 验证对端身份与 ingest 协议版本。 */
export async function probeHub(port: number): Promise<boolean> {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/v1/hub`, {
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
    })
    if (!res.ok) return false
    const body = (await res.json()) as { app?: string; proto?: unknown }
    return body.app === 'heartbeat' && body.proto === REQUIRED_HUB_PROTOCOL
  } catch {
    return false
  }
}

/**
 * GET /v1/collectors/browser/config（ADR-026 §2）：拉取本采集器在 hub 侧的配置。
 * 顺带把自报的 flushPeriodMs 带上，hub 据此派生 Active 窗口（§3）；此调用也触发 hub 侧自动注册（§1）。
 * 返回 null 表示拉取失败（hub 不在/身份不符）——调用方应保守视为"未停用"，不因拉取失败误停采集。
 */
export async function fetchCollectorConfig(
  port: number,
  source: string,
  flushPeriodMs: number,
): Promise<{ enabled: boolean } | null> {
  try {
    const url = `http://127.0.0.1:${port}/v1/collectors/${encodeURIComponent(source)}/config?flushPeriodMs=${flushPeriodMs}`
    const res = await fetch(url, { signal: AbortSignal.timeout(PROBE_TIMEOUT_MS) })
    if (!res.ok) return null
    const body = (await res.json()) as { enabled?: unknown }
    return { enabled: body.enabled !== false }
  } catch {
    return null
  }
}

/**
 * 在 [basePort, basePort + PORT_RANGE) 内并发探测，返回首个（编号最小的）hub 端口；无则 null。
 * hub 端口被占时顺延到下一个，所以低编号优先即"hub 实际所在"。
 */
export async function discoverHub(basePort: number): Promise<number | null> {
  const ports = Array.from({ length: PORT_RANGE }, (_, i) => basePort + i).filter(
    (p) => p <= 65535,
  )
  const results = await Promise.all(ports.map(probeHub))
  const index = results.findIndex(Boolean)
  return index >= 0 ? ports[index] : null
}

/** 优先复用缓存端口；不兼容时在约定范围内寻找协议匹配的 hub。 */
export async function findCompatibleHub(
  basePort: number,
  targetPort: number,
): Promise<number | null> {
  if (await probeHub(targetPort)) return targetPort
  return discoverHub(basePort)
}

/**
 * 面向队列的上报入口：先握手确认目标是兼容 hub，再 POST；否则范围发现。
 * 返回结果与实际使用的端口（供调用方缓存，减少后续探测）。
 *
 * 旧协议 hub 即使会接受旧 payload，也不会收到 POST，更不可能让调用方把队列当成功清空。
 * rejected 仅在身份与版本确认后成立；陌生服务或不兼容 hub 均归为 unreachable（保队列）。
 */
export async function postToHub(
  basePort: number,
  targetPort: number,
  segments: SegmentSnapshot[],
): Promise<{ result: PostResult; port: number }> {
  const found = await findCompatibleHub(basePort, targetPort)
  if (found === null) return { result: 'unreachable', port: targetPort }

  // found 已通过身份与版本探测，其 4xx 即真实拒收。
  return { result: await postSegments(found, segments), port: found }
}
