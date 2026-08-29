// @vitest-environment happy-dom

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchDailyQuestions, knowledgeErrorOf, proposeFromQuestion } from './index'
import type { CalendarWindowEnvelope } from '../calendar/localCalendarWindow'

vi.mock('../stores/auth', () => ({
  authStore: {
    token: { value: 'tok-owner' },
    tryRefresh: vi.fn(),
    clearAuth: vi.fn(),
  },
}))

const fetchMock = vi.fn()
const dayWindow: CalendarWindowEnvelope<'day'> = {
  version: 1,
  kind: 'day',
  localDate: '2026-03-08',
  timeZone: 'America/New_York',
  start: '2026-03-08T05:00:00Z',
  endExclusive: '2026-03-09T04:00:00Z',
}

describe('Asking Local Calendar Window transport', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({ questions: [], readingLabels: {} }),
      json: async () => ({ questions: [], readingLabels: {} }),
    } as Response)
  })

  it('reads questions with the complete immutable day envelope', async () => {
    await fetchDailyQuestions({ window: dayWindow })

    expectWindowRequest(0, '/api/v1/knowledge/questions')
  })

  it('submits the served WindowKey against the current complete day envelope', async () => {
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({ explanation: '', operations: [], warnings: [], suggestions: [] }),
      json: async () => ({ explanation: '', operations: [], warnings: [], suggestions: [] }),
    } as Response)

    await proposeFromQuestion('question-1', {
      window: dayWindow,
      windowKey: 'analytics-owned-window-key',
      answer: '这是项目调研',
    })

    expectWindowRequest(0, '/api/v1/knowledge/questions/question-1/propose')
    const [, init] = fetchMock.mock.calls[0]
    expect(JSON.parse(init.body)).toEqual({
      windowKey: 'analytics-owned-window-key',
      answer: '这是项目调研',
    })
  })

  it('preserves the typed window mismatch returned by proposal validation', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      status: 400,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({
        code: 'question_window_mismatch',
        message: 'Refresh questions before submitting.',
        strands: [],
      }),
    } as Response)

    const error = await proposeFromQuestion('question-1', {
      window: dayWindow,
      windowKey: 'old-window-key',
      answer: '这是项目调研',
    }).catch((caught: unknown) => caught)

    expect(knowledgeErrorOf(error)?.code).toBe('question_window_mismatch')
  })
})

function expectWindowRequest(index: number, path: string) {
  const [rawUrl] = fetchMock.mock.calls[index]
  const url = new URL(rawUrl, 'https://heartbeat.test')
  expect(url.pathname).toBe(path)
  expect(Object.fromEntries(url.searchParams)).toEqual({
    Version: '1',
    Kind: 'day',
    LocalDate: dayWindow.localDate,
    TimeZone: dayWindow.timeZone,
    Start: new Date(dayWindow.start).toISOString(),
    EndExclusive: new Date(dayWindow.endExclusive).toISOString(),
  })
  expect(url.searchParams.has('date')).toBe(false)
  expect(url.searchParams.has('correlationIdentity')).toBe(false)
}
