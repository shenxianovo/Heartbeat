// @vitest-environment happy-dom

import { computed, ref } from 'vue'
import { shallowMount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { resolveCalendarContext } from '../calendar/localCalendarWindow'
import Dashboard from './Dashboard.vue'
import ActivityTimeline from './ActivityTimeline.vue'
import RecapCard from './RecapCard.vue'
import StrandQuestions from './StrandQuestions.vue'

const scenarios = [
  resolveCalendarContext('2026-08-29', {
    timeZone: 'Asia/Shanghai',
    now: '2026-08-29T04:00:00Z',
    correlationIdentity: () => 'ordinary-refresh',
  }),
  resolveCalendarContext('2026-11-01', {
    timeZone: 'America/New_York',
    now: '2026-11-01T12:00:00Z',
    correlationIdentity: () => 'fall-back-refresh',
  }),
]

const calendarContext = ref(scenarios[0])

const heartbeat = {
  devices: ref([]),
  error: ref(null),
  refresh: vi.fn(),
  selectedDevice: ref(0),
  selectedDate: ref('2026-11-01'),
  usageData: ref([]),
  appNameMap: computed(() => new Map()),
  provisionalAppIds: ref(new Set<number>()),
  loading: ref(false),
  isToday: computed(() => calendarContext.value.isToday),
  isAlive: computed(() => false),
  onlinePresences: computed(() => []),
  currentApp: computed(() => null),
  currentAppId: computed(() => null),
  currentAppKey: computed(() => null),
  lastSeenStr: computed(() => ''),
  lastSeenTitle: computed(() => ''),
  isAllDevices: computed(() => true),
  appSummaries: computed(() => []),
  totalSeconds: computed(() => 0),
  awaySeconds: computed(() => 0),
  onlineSeconds: computed(() => 0),
  perDeviceSeconds: computed(() => []),
  hasConcurrentUse: computed(() => false),
  maxSeconds: computed(() => 1),
  weeklyAppSummaries: computed(() => []),
  weeklyTotalSeconds: computed(() => 0),
  includeAway: ref(false),
  keyFrequency: ref([]),
  calendarContext,
  timezoneLabel: computed(() => calendarContext.value.displayLabel),
}

vi.mock('../composables/useHeartbeat', () => ({
  useHeartbeat: vi.fn(() => heartbeat),
}))

vi.mock('../stores/auth', () => ({
  authStore: {
    isAuthenticated: true,
    username: ref('alice'),
    logout: vi.fn(),
    redirectToLogin: vi.fn(),
  },
}))

vi.mock('../api/index', () => ({
  fetchManagedSubjectStatuses: vi.fn(async () => []),
}))

describe('Dashboard Calendar Context orchestration', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    calendarContext.value = scenarios[0]
  })

  it.each(scenarios)(
    'passes the same immutable $day.timeZone refresh identity across an end-to-end Dashboard render',
    context => {
      calendarContext.value = context
      const wrapper = shallowMount(Dashboard, {
        props: { username: 'alice' },
        global: {
          stubs: { RouterLink: true },
        },
      })

      expect(wrapper.findComponent(RecapCard).props('calendarContext')).toBe(context)
      expect(wrapper.findComponent(StrandQuestions).props('calendarContext')).toBe(context)
      expect(wrapper.findComponent(ActivityTimeline).props('dayWindow')).toBe(context.day)
      expect(wrapper.findComponent(ActivityTimeline).props('isToday')).toBe(context.isToday)
      expect(wrapper.text()).toContain(context.displayLabel)

      wrapper.unmount()
    },
  )
})
