// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { proposeCorrection, fetchStrands, commitChangeSet } from '../api/index'
import { resolveCalendarContext } from '../calendar/localCalendarWindow'
import RecapCorrection from './RecapCorrection.vue'

vi.mock('../api/index', () => ({
  proposeCorrection: vi.fn(),
  fetchStrands: vi.fn(async () => []),
  commitChangeSet: vi.fn(),
  toApiError: vi.fn(() => ({ kind: 'network' })),
  changeSetErrorOf: vi.fn(() => null),
  knowledgeErrorOf: vi.fn(() => null),
}))

function context(date: string, identity: string) {
  return resolveCalendarContext(date, {
    timeZone: 'Etc/UTC',
    now: '2026-08-19T12:00:00Z',
    correlationIdentity: () => identity,
  })
}

describe('RecapCorrection Calendar Context isolation', () => {
  beforeEach(() => vi.clearAllMocks())

  it('does not let an old proposal overwrite the next refresh generation', async () => {
    let resolveOld!: (value: Awaited<ReturnType<typeof proposeCorrection>>) => void
    vi.mocked(proposeCorrection).mockReturnValue(new Promise(resolve => { resolveOld = resolve }))
    vi.mocked(fetchStrands).mockResolvedValue([])

    const wrapper = mount(RecapCorrection, {
      props: {
        calendarContext: context('2026-08-19', 'generation-1'),
        regenerate: vi.fn(async () => {}),
      },
      global: {
        stubs: { ProposalReview: true },
      },
    })

    await wrapper.get('button').trigger('click')
    await wrapper.get('textarea').setValue('补上那天的调研')
    await wrapper.findAll('button').find(button => button.text() === '整理成变更')!.trigger('click')
    await flushPromises()

    expect(proposeCorrection).toHaveBeenCalledWith({
      window: context('2026-08-19', 'ignored').day,
      correction: '补上那天的调研',
    })

    await wrapper.setProps({ calendarContext: context('2026-08-18', 'generation-2') })
    resolveOld({
      explanation: '旧窗口提案',
      operations: [],
      warnings: [],
      suggestions: [],
      readingLabels: {},
    } as unknown as Awaited<ReturnType<typeof proposeCorrection>>)
    await flushPromises()

    expect(wrapper.text()).toContain('这里不对')
    expect(wrapper.text()).not.toContain('旧窗口提案')
  })

  it('regenerates the committed window without showing its result in a newer generation', async () => {
    const firstContext = context('2026-08-19', 'generation-1')
    vi.mocked(proposeCorrection).mockResolvedValue({
      explanation: '补上事实',
      operations: [{
        opId: 'op1',
        type: 'createEpisode',
        createEpisode: { localDate: new Date('2026-08-19T00:00:00'), text: '做了调研' },
      }],
      warnings: [],
      suggestions: [],
      readingLabels: {},
    } as unknown as Awaited<ReturnType<typeof proposeCorrection>>)
    let resolveCommit!: (value: Awaited<ReturnType<typeof commitChangeSet>>) => void
    vi.mocked(commitChangeSet).mockReturnValue(new Promise(resolve => { resolveCommit = resolve }))
    const regenerate = vi.fn(async () => {})
    const wrapper = mount(RecapCorrection, {
      props: { calendarContext: firstContext, regenerate },
      global: { stubs: { ProposalReview: true } },
    })

    await wrapper.get('button').trigger('click')
    await wrapper.get('textarea').setValue('补上那天的调研')
    await wrapper.findAll('button').find(button => button.text() === '整理成变更')!.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().startsWith('确认保存'))!.trigger('click')

    await wrapper.setProps({ calendarContext: context('2026-08-18', 'generation-2') })
    resolveCommit({ results: [{ opId: 'op1', type: 'createEpisode' }] } as Awaited<ReturnType<typeof commitChangeSet>>)
    await flushPromises()

    expect(regenerate).toHaveBeenCalledWith(firstContext)
    expect(wrapper.text()).toContain('这里不对')
    expect(wrapper.text()).not.toContain('这一天的回顾已用更新后的知识重新生成')
  })
})
