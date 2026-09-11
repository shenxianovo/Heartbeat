import rotationPolicy from '../../../../contracts/segment-rotation-policy.json'
import { uuidv7 } from '../ids'

/** SDK 的会话检查点；保留 Browser 已有的平铺格式，调用方只负责保存和恢复。 */
export type SegmentState<T> = T & { readonly id: string; readonly startTime: number; kind?: 'segment'; revision?: number; lastSnapshot?: unknown; recoveryRequired?: true }

export type SegmentSnapshot<P, S extends string = string> = P & {
  id: string
  kind?: 'segment'
  revision?: number
  source: S
  startTime: string
  endTime: string
  isFinal: boolean
}

export interface Segment<T, P, S extends string> {
  readonly state: SegmentState<T>
  /** 更新读数，保留事实身份和起点；由下一次观测快照交付。 */
  update(payload: T): Segment<T, P, S>
  /** 调用方明确观测到了此时刻；SDK 不从发送时刻推断结束时间。 */
  observe(observedUntil: number): SegmentSnapshot<P, S>[]
  end(end: number): SegmentSnapshot<P, S>
}

export interface SegmentSdk<T, P, S extends string> {
  startSegment(input: { start: number; payload: T }): Segment<T, P, S>
  restore(state: SegmentState<T>): Segment<T, P, S>
}

export const ROTATE_AFTER_MS = rotationPolicy.rotateAfterMilliseconds

/** 最小 Segment SDK；窗口、URL、互斥规则及协议会话都不属于这个模块。 */
export function createSegmentSdk<T, P, S extends string>(options: {
  source: S
  payloadOf: (value: T) => P
  newId?: () => string
}): SegmentSdk<T, P, S> {
  const newId = options.newId ?? uuidv7

  function snapshot(state: SegmentState<T>, end: number, isFinal: boolean): SegmentSnapshot<P, S> {
    const { id: _id, startTime: _start, kind: _kind, revision: _revision, lastSnapshot: _last, recoveryRequired: _recovery, ...payload } = options.payloadOf(state) as P & Partial<SegmentState<T>>
    const { revision: _previousRevision, ...previous } = state.lastSnapshot as SegmentSnapshot<P, S> ?? {} as SegmentSnapshot<P, S>
    return {
      ...previous,
      ...payload as P,
      id: state.id,
      ...(state.kind === undefined ? {} : { kind: state.kind }),
      source: options.source,
      startTime: new Date(state.startTime).toISOString(),
      endTime: new Date(Math.max(end, state.startTime)).toISOString(),
      isFinal,
    }
  }

  function restore(state: SegmentState<T>): Segment<T, P, S> {
    function publish(end: number, isFinal: boolean): SegmentSnapshot<P, S> {
      const content = snapshot(state, end, isFinal)
      const last = state.lastSnapshot as SegmentSnapshot<P, S> | undefined
      const { revision: _revision, ...previousContent } = last ?? {} as SegmentSnapshot<P, S>
      if (last && JSON.stringify(previousContent) === JSON.stringify(content)) return last
      // Historical sessions keep their old identity and endTime high-water mark. Migration
      // supplies the last published snapshot; only a new observation advances its revision.
      const highWater = state.revision ?? (last?.revision ?? (last ? Date.parse(last.endTime) : 0))
      const revision = highWater > 0 ? highWater + 1 : state.kind ? 1 : Math.max(1, Date.parse(content.endTime))
      if (!Number.isSafeInteger(revision)) throw new Error('Segment Revision exhausted; preserve checkpoint')
      const next = { ...content, revision }
      state = { ...state, revision, lastSnapshot: next }
      return next
    }
    const segment: Segment<T, P, S> = {
      get state() { return state },
      update(payload) {
        state = { ...state, ...payload, id: state.id, startTime: state.startTime }
        return segment
      },
      observe(observedUntil) {
        const rotate = observedUntil - state.startTime >= ROTATE_AFTER_MS
        const out = [publish(observedUntil, rotate)]
        if (rotate) state = { ...state, id: newId(), startTime: observedUntil, kind: 'segment', revision: 0, lastSnapshot: undefined }
        return out
      },
      end: end => publish(end, true),
    }
    return segment
  }

  function startSegment({ start, payload }: { start: number; payload: T }): Segment<T, P, S> {
    return restore({ ...payload, id: newId(), startTime: start, kind: 'segment', revision: 0 })
  }

  return { startSegment, restore }
}
