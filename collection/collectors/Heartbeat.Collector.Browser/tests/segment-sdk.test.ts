import { afterEach, expect, it, vi } from 'vitest'
import { createSegmentSdk, ROTATE_AFTER_MS } from '../src/sdk/segments'

const start = Date.UTC(2026, 8, 10)
const sdk = () => createSegmentSdk({ source: 'test', payloadOf: (value: { label: string }) => value })
afterEach(() => vi.restoreAllMocks())

it('Segment 对象更新读数、恢复会话并结束，SDK 保留身份与实际观测时间', () => {
  const segments = sdk()
  const segment = segments.startSegment({ start, payload: { label: 'first' } })
  const before = segment.state
  segment.update({ label: 'updated' })
  // 发送/系统时钟已经更晚，快照仍只能到 Collector 给出的观测时刻。
  vi.spyOn(Date, 'now').mockReturnValue(start + 3_600_000)
  const [first] = segment.observe(start + 30_000)
  expect(first).toMatchObject({
    label: 'updated', startTime: new Date(start).toISOString(),
    endTime: new Date(start + 30_000).toISOString(), isFinal: false,
  })
  expect(first.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  const restored = segments.restore(JSON.parse(JSON.stringify(segment.state)))
  expect(restored.end(start + 60_000)).toMatchObject({
    id: first.id, label: 'updated', isFinal: true,
    startTime: first.startTime, endTime: new Date(start + 60_000).toISOString(),
  })
  expect(before.label).toBe('first')
})

it('独立 Segment 互不干扰，轮转只替换该对象的检查点，旧检查点仍可恢复', () => {
  const segments = sdk()
  const first = segments.startSegment({ start, payload: { label: 'same' } })
  const second = segments.startSegment({ start: start + 60_000, payload: { label: 'same' } })
  const checkpoint = first.state
  const secondCheckpoint = second.state
  const at = start + ROTATE_AFTER_MS
  expect(first.observe(at)[0]).toMatchObject({ id: checkpoint.id, isFinal: true })
  expect(first.state.id).not.toBe(checkpoint.id)
  expect(first.observe(at + 1000)[0]).toMatchObject({
    id: first.state.id, startTime: new Date(at).toISOString(), isFinal: false,
  })
  expect(segments.restore(checkpoint).observe(start + 1000)[0].id).toBe(checkpoint.id)
  expect(second.state).toBe(secondCheckpoint)
  expect(second.observe(at)[0]).toMatchObject({ id: secondCheckpoint.id, isFinal: false })
})
