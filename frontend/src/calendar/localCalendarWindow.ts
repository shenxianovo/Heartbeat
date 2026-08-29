import { Temporal } from '@js-temporal/polyfill'

export const CALENDAR_WINDOW_VERSION = 1 as const

export type CalendarWindowEnvelope<Kind extends 'day' | 'week' = 'day' | 'week'> = Readonly<{
  version: typeof CALENDAR_WINDOW_VERSION
  kind: Kind
  localDate: string
  timeZone: string
  start: string
  endExclusive: string
}>

export type CalendarContext = Readonly<{
  day: CalendarWindowEnvelope<'day'>
  week: CalendarWindowEnvelope<'week'>
  isToday: boolean
  displayLabel: string
  correlationIdentity: string
}>

/** 比较两个 Browser envelope 是否描述同一个规范窗口；刷新 correlation 不是窗口身份。 */
export function sameCalendarWindow<Kind extends 'day' | 'week'>(
  left: CalendarWindowEnvelope<Kind>,
  right: CalendarWindowEnvelope<Kind>,
): boolean {
  return left.version === right.version
    && left.kind === right.kind
    && left.localDate === right.localDate
    && left.timeZone === right.timeZone
    && left.start === right.start
    && left.endExclusive === right.endExclusive
}

export type CalendarContextErrorCode =
  | 'invalid_local_date'
  | 'unsupported_timezone'
  | 'nonexistent_civil_date'

export class CalendarContextError extends Error {
  constructor(public readonly code: CalendarContextErrorCode, message: string) {
    super(message)
    this.name = 'CalendarContextError'
  }
}

interface ResolveCalendarContextOptions {
  timeZone?: string
  now?: string
  correlationIdentity?: () => string
}

function parseLocalDate(value: string): Temporal.PlainDate {
  if (!/^(?!0000)\d{4}-\d{2}-\d{2}$/.test(value)) {
    throw new CalendarContextError('invalid_local_date', `Invalid local date: ${value}`)
  }

  try {
    const date = Temporal.PlainDate.from(value, { overflow: 'reject' })
    if (date.toString() !== value) throw new RangeError('Date is not canonical')
    return date
  } catch {
    throw new CalendarContextError('invalid_local_date', `Invalid local date: ${value}`)
  }
}

function civilDayStart(date: Temporal.PlainDate, timeZone: string): Temporal.ZonedDateTime {
  const start = date.toZonedDateTime({ timeZone, plainTime: '00:00:00' })
  if (!start.toPlainDate().equals(date)) {
    throw new CalendarContextError(
      'nonexistent_civil_date',
      `Civil date ${date} does not exist in ${timeZone}`,
    )
  }
  return start
}

function currentTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone
}

function defaultCorrelationIdentity(): string {
  return globalThis.crypto.randomUUID()
}

function offsetLabel(start: Temporal.ZonedDateTime, end: Temporal.ZonedDateTime): string {
  const first = `UTC${start.offset}`
  const last = `UTC${end.offset}`
  return first === last ? first : `${first} → ${last}`
}

export function resolveCalendarContext(
  localDateValue: string,
  options: ResolveCalendarContextOptions = {},
): CalendarContext {
  const localDate = parseLocalDate(localDateValue)
  const timeZone = options.timeZone ?? currentTimeZone()

  let start: Temporal.ZonedDateTime
  let end: Temporal.ZonedDateTime
  let weekStart: Temporal.ZonedDateTime
  let weekEnd: Temporal.ZonedDateTime
  try {
    start = civilDayStart(localDate, timeZone)
    end = civilDayStart(localDate.add({ days: 1 }), timeZone)
    const monday = localDate.subtract({ days: localDate.dayOfWeek - 1 })
    weekStart = civilDayStart(monday, timeZone)
    weekEnd = civilDayStart(monday.add({ days: 7 }), timeZone)
  } catch (error) {
    if (error instanceof CalendarContextError) throw error
    throw new CalendarContextError('unsupported_timezone', `Unsupported timezone: ${timeZone}`)
  }

  let now: Temporal.Instant
  try {
    now = options.now ? Temporal.Instant.from(options.now) : Temporal.Now.instant()
  } catch {
    throw new CalendarContextError('invalid_local_date', `Invalid current instant: ${options.now}`)
  }

  const day = Object.freeze<CalendarWindowEnvelope<'day'>>({
    version: CALENDAR_WINDOW_VERSION,
    kind: 'day',
    localDate: localDateValue,
    timeZone,
    start: start.toInstant().toString(),
    endExclusive: end.toInstant().toString(),
  })
  const week = Object.freeze<CalendarWindowEnvelope<'week'>>({
    version: CALENDAR_WINDOW_VERSION,
    kind: 'week',
    localDate: localDateValue,
    timeZone,
    start: weekStart.toInstant().toString(),
    endExclusive: weekEnd.toInstant().toString(),
  })

  return Object.freeze<CalendarContext>({
    day,
    week,
    isToday: now.toZonedDateTimeISO(timeZone).toPlainDate().equals(localDate),
    displayLabel: `${localDateValue} · ${timeZone} (${offsetLabel(start, end)})`,
    correlationIdentity: (options.correlationIdentity ?? defaultCorrelationIdentity)(),
  })
}
