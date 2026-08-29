// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  fetchDailyQuestions,
  fetchStrands,
  proposeFromQuestion,
} from '../api/index'
import { resolveCalendarContext } from '../calendar/localCalendarWindow'
import ProposalReview from './ProposalReview.vue'
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

function questionResponse(
  id = 'question-1',
  windowKey = 'original-analytics-window-key',
  question = '这是什么？',
) {
  return {
    questions: [{
      id,
      windowKey,
      kind: 'cluster',
      question,
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

  it('replaces old questions before submitting against a new Calendar Context', async () => {
    const wrapper = await mountQuestions()
    vi.mocked(fetchDailyQuestions).mockResolvedValueOnce(
      questionResponse('question-2', 'changed-analytics-window-key', '新窗口的问题？'),
    )

    await wrapper.setProps({ calendarContext: changedContext })
    await flushPromises()
    expect(wrapper.text()).toContain('新窗口的问题？')
    expect(wrapper.text()).not.toContain('这是什么？')

    await wrapper.find('textarea').setValue('这是项目调研')
    const proposeButton = wrapper.findAll('button').find(button => button.text() === '整理成变更')!
    await proposeButton.trigger('click')
    await flushPromises()

    expect(proposeFromQuestion).toHaveBeenCalledWith('question-2', {
      window: changedContext.day,
      windowKey: 'changed-analytics-window-key',
      answer: '这是项目调研',
    })
  })

  it('does not let a slow question read from an older refresh generation overwrite the new list', async () => {
    let resolveOld!: (value: ReturnType<typeof questionResponse>) => void
    vi.mocked(fetchDailyQuestions).mockImplementationOnce(
      () => new Promise(resolve => { resolveOld = resolve }),
    )
    const wrapper = mount(StrandQuestions, {
      props: { calendarContext: originalContext },
      global: {
        stubs: {
          Card: { template: '<section><slot /></section>' },
          ProposalReview: true,
        },
      },
    })

    vi.mocked(fetchDailyQuestions).mockResolvedValueOnce(
      questionResponse('question-2', 'changed-analytics-window-key', '新窗口的问题？'),
    )
    await wrapper.setProps({ calendarContext: changedContext })
    await flushPromises()
    expect(wrapper.text()).toContain('新窗口的问题？')

    resolveOld(questionResponse('question-1', 'original-analytics-window-key', '迟到的旧问题？'))
    await flushPromises()

    expect(wrapper.text()).toContain('新窗口的问题？')
    expect(wrapper.text()).not.toContain('迟到的旧问题？')
  })

  it('does not let an in-flight old proposal or strand read overwrite new-generation Knowledge review state', async () => {
    type Proposal = Awaited<ReturnType<typeof proposeFromQuestion>>
    type Strands = Awaited<ReturnType<typeof fetchStrands>>
    let resolveOldProposal!: (value: Proposal) => void
    let resolveOldStrands!: (value: Strands) => void
    vi.mocked(proposeFromQuestion)
      .mockImplementationOnce(() => new Promise(resolve => { resolveOldProposal = resolve }))
      .mockResolvedValueOnce({
        explanation: 'new proposal', operations: [], warnings: [], suggestions: [], readingLabels: {},
      } as unknown as Proposal)
    vi.mocked(fetchStrands)
      .mockImplementationOnce(() => new Promise(resolve => { resolveOldStrands = resolve }))
      .mockResolvedValueOnce([{ id: 'new-strand', name: 'New strand' }] as Strands)

    const wrapper = await mountQuestions()
    await wrapper.find('textarea').setValue('旧窗口回答')
    await wrapper.findAll('button').find(button => button.text() === '整理成变更')!.trigger('click')

    vi.mocked(fetchDailyQuestions).mockResolvedValueOnce(
      questionResponse('question-2', 'changed-analytics-window-key', '新窗口的问题？'),
    )
    await wrapper.setProps({ calendarContext: changedContext })
    await flushPromises()
    await wrapper.find('textarea').setValue('新窗口回答')
    await wrapper.findAll('button').find(button => button.text() === '整理成变更')!.trigger('click')
    await flushPromises()

    expect(wrapper.findComponent(ProposalReview).props('strands')).toEqual([
      expect.objectContaining({ id: 'new-strand' }),
    ])

    resolveOldStrands([{ id: 'old-strand', name: 'Old strand' }] as Strands)
    resolveOldProposal({
      explanation: 'old proposal', operations: [], warnings: [], suggestions: [], readingLabels: {},
    } as unknown as Proposal)
    await flushPromises()

    expect(wrapper.findComponent(ProposalReview).props('strands')).toEqual([
      expect.objectContaining({ id: 'new-strand' }),
    ])
    expect(wrapper.text()).toContain('新窗口的问题？')
  })
})
