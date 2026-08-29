import { ref, type Ref } from 'vue'
import { toApiError, type ApiError } from '../api/index'

/**
 * 取数策略层:把一次取数收敛成 { data, error, pending, run } 四态。
 *
 * 契约:api 层的取数函数只抛错、绝不吞错(见 api/index.ts)。run() 成功时刷新
 * data 并清空 error;失败时点亮 error 但**保留上次成功的 data**——轮询场景下
 * 断一次网不该把整屏清空。UI 据此区分"出错"(error 非空)与"没数据"(data 为空)。
 */
export function useAsyncData<T>(fetcher: () => Promise<T>, initial: T) {
  const data = ref(initial) as Ref<T>
  const error = ref<ApiError | null>(null)
  const pending = ref(false)
  let inFlight = 0

  async function run(commitIf: () => boolean = () => true) {
    inFlight++
    pending.value = true
    try {
      const next = await fetcher()
      if (commitIf()) {
        data.value = next
        error.value = null
      }
    } catch (e) {
      if (commitIf()) error.value = toApiError(e)
    } finally {
      inFlight--
      pending.value = inFlight > 0
    }
  }

  return { data, error, pending, run }
}

/** 只提交仍属于当前 refresh identity 的响应；旧请求可以完成，但不能覆盖新状态。 */
export async function runForCurrentIdentity(
  source: { run: (commitIf?: () => boolean) => Promise<void> },
  currentIdentity: () => string,
) {
  const expected = currentIdentity()
  await source.run(() => currentIdentity() === expected)
}
