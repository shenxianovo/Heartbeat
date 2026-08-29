// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  fetchDailyQuestions,
  fetchStrands,
  proposeFromQuestion,
} from '../api/index'
import { resolveCalendarContext } from '../calendar/localCalendarWindow'
import StrandQuestions from './StrandQuestions.vue'

vi.mock('../api/index', () => ({
  fetchDailyQuestions: vi.fn(),
  fetchStrands: vi.fn(),
  proposeFromQuestion: vi.fn(),
  commitChangeSet: vi.fn(),
  muteMatcher: vi.fn(),
  toApiError: vi.fn((error: unknown) => error),
  changeSetErrorOf: vi.fn(),
  knowledgeErrorOf: vi.fn(),
  KnowledgeOperationDto: { fromJS: vi.fn((value: unknown) => value) },
}))

vi.mock('../composables/useHeartbeat', () => ({
  formatDuration: vi.fn((seconds: number) => `${seconds}s`),
}))

const originalContext = resolveCalendarContext('2026-03-08', {
  timeZone: 'America/New_York',
  now: '2026-03-08T12:00:00Z',
  correlationIdentity: () => 'original-refresh',
})

const changedContext = resolveCalendarContext('2026-03-08', {
  timeZone: 'UTC',
  now: '2026-03-08T12:00:00Z',
  correlationIdentity: () => 'changed-refresh',
})

function questionResponse() {
  return {
    questions: [{
      id: 'question-1',
      windowKey: 'original-analytics-window-key',
      kind: 'cluster',
      question: '这是什么？',
      matcher: { source: 'system', steps: [] },
      observations: [],
    }],
    readingLabels: {},
  } as never
}

async function mountQuestions() {
  const wrapper = mount(StrandQuestions, {
    props: { calendarContext: originalContext },
    global: {
      stubs: {
        Card: { template: '<section><slot /></section>' },
        ProposalReview: true,
      },
    },
  })
  await flushPromises()
  return wrapper
}

describe('StrandQuestions window identity', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(fetchDailyQuestions).mockResolvedValue(questionResponse())
    vi.mocked(fetchStrands).mockResolvedValue([])
    vi.mocked(proposeFromQuestion).mockResolvedValue({
      explanation: '', operations: [], warnings: [], suggestions: [], readingLabels: {},
    } as never)
  })

  it('reads questions using the captured Calendar Context day window', async () => {
    await mountQuestions()

    expect(fetchDailyQuestions).toHaveBeenCalledWith({ window: originalContext.day })
  })

  it('submits the original question credential against the current Calendar Context', async () => {
    const wrapper = await mountQuestions()
    vi.mocked(fetchDailyQuestions).mockImplementation(() => new Promise(() => {}))

    await wrapper.setProps({ calendarContext: changedContext })
    await wrapper.find('textarea').setValue('这是项目调研')
    const proposeButton = wrapper.findAll('button').find(button => button.text() === '整理成变更')!
    await proposeButton.trigger('click')
    await flushPromises()

    expect(proposeFromQuestion).toHaveBeenCalledWith('question-1', {
      window: changedContext.day,
      windowKey: 'original-analytics-window-key',
      answer: '这是项目调研',
    })
  })
})
