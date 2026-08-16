function pad(value: number): string {
  return String(value).padStart(2, '0')
}

function isSameLocalDate(left: Date, right: Date): boolean {
  return left.getFullYear() === right.getFullYear()
    && left.getMonth() === right.getMonth()
    && left.getDate() === right.getDate()
}

export function latestDate(values: Array<Date | null>): Date | null {
  let latest: Date | null = null
  for (const value of values) {
    if (!value || Number.isNaN(value.getTime())) continue
    if (!latest || value.getTime() > latest.getTime()) latest = value
  }
  return latest
}

export function formatLastSeen(value: Date | null, now = new Date()): string {
  if (!value || Number.isNaN(value.getTime())) return ''

  const time = `${pad(value.getHours())}:${pad(value.getMinutes())}`
  if (isSameLocalDate(value, now)) return `今天 ${time}`

  const yesterday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1)
  if (isSameLocalDate(value, yesterday)) return `昨天 ${time}`

  const monthAndDay = `${value.getMonth() + 1}月${value.getDate()}日 ${time}`
  return value.getFullYear() === now.getFullYear()
    ? monthAndDay
    : `${value.getFullYear()}年${monthAndDay}`
}

export function formatExactLocalDateTime(value: Date | null): string {
  if (!value || Number.isNaN(value.getTime())) return ''
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())} ${pad(value.getHours())}:${pad(value.getMinutes())}:${pad(value.getSeconds())}`
}
