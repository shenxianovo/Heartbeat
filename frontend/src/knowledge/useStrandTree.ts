import { ref, computed, type Ref } from 'vue'
import {
  fetchStrands, createStrand, updateStrand, moveStrand, endStrand, muteMatcher,
  toApiError,
  type IStrandResponse, type ICreateStrandRequest, type IUpdateStrandRequest,
  type IMoveStrandRequest, type IEndStrandRequest, type IMatcherDto,
} from '../api/index'
import { buildTree } from './strandTreeUtils'

export { buildTree, flattenTree, type StrandTreeNode } from './strandTreeUtils'

export function useStrandTree() {
  const strands: Ref<IStrandResponse[]> = ref([])
  const loading = ref(false)
  const error: Ref<string | null> = ref(null)
  const selectedId: Ref<string | null> = ref(null)
  const expandedIds = ref(new Set<string>())
  const conflictError: Ref<string | null> = ref(null)

  const tree = computed(() => buildTree(strands.value))

  const selectedStrand = computed(() =>
    strands.value.find(s => s.id === selectedId.value) ?? null,
  )

  async function load() {
    loading.value = true
    error.value = null
    try {
      strands.value = await fetchStrands()
    } catch (e) {
      error.value = '加载脉络失败'
    } finally {
      loading.value = false
    }
  }

  function select(id: string | null) {
    selectedId.value = id
    conflictError.value = null
  }

  function toggle(id: string) {
    const s = new Set(expandedIds.value)
    if (s.has(id)) s.delete(id); else s.add(id)
    expandedIds.value = s
  }

  async function doCreate(req: ICreateStrandRequest): Promise<boolean> {
    conflictError.value = null
    try {
      const created = await createStrand(req)
      await load()
      selectedId.value = created.id!
      return true
    } catch (e) {
      return handleWriteError(e)
    }
  }

  async function doUpdate(id: string, req: IUpdateStrandRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await updateStrand(id, req)
      await load()
      return true
    } catch (e) {
      return handleWriteError(e)
    }
  }

  async function doMove(id: string, req: IMoveStrandRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await moveStrand(id, req)
      await load()
      return true
    } catch (e) {
      return handleWriteError(e)
    }
  }

  async function doEnd(id: string, req: IEndStrandRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await endStrand(id, req)
      await load()
      return true
    } catch (e) {
      return handleWriteError(e)
    }
  }

  async function doMute(matcher: IMatcherDto): Promise<boolean> {
    try {
      await muteMatcher(matcher)
      await load()
      return true
    } catch {
      return false
    }
  }

  function handleWriteError(e: unknown): false {
    const apiErr = toApiError(e)
    if (apiErr.kind === 'http' && apiErr.status === 409) {
      conflictError.value = '数据已被其他操作更新，请刷新后重试'
    } else if (apiErr.kind === 'http' && apiErr.status === 400) {
      conflictError.value = '请求无效，请检查输入'
    } else {
      conflictError.value = '操作失败，请重试'
    }
    return false
  }

  return {
    strands,
    loading,
    error,
    tree,
    selectedId,
    selectedStrand,
    expandedIds,
    conflictError,
    load,
    select,
    toggle,
    doCreate,
    doUpdate,
    doMove,
    doEnd,
    doMute,
  }
}
