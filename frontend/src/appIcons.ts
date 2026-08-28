import { fetchAppIcon } from './api/index'

// 同一个 App 会同时出现在状态卡、排行、周图和时间轴。缓存成功与 404 结果，
// 避免每个展示位各发一次请求；object URL 会随页面卸载由浏览器回收。
const resolved = new Map<string, string | null>()
const pending = new Map<string, Promise<string | null>>()

function cacheKey(username: string, appId: number): string {
  return `${username}\u0000${appId}`
}

export async function getAppIconObjectUrl(username: string, appId: number): Promise<string | null> {
  if (!username || appId <= 0) return null

  const key = cacheKey(username, appId)
  if (resolved.has(key)) return resolved.get(key) ?? null

  const existing = pending.get(key)
  if (existing) return existing

  const load = fetchAppIcon(username, appId)
    .then(blob => {
      const url = blob ? URL.createObjectURL(blob) : null
      resolved.set(key, url)
      return url
    })
    .finally(() => pending.delete(key))

  pending.set(key, load)
  return load
}
