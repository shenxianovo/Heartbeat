import { ref, type Ref } from 'vue'
import {
  fetchEpisodes, createEpisode, updateEpisode, relateEpisode, deleteEpisode,
  createProbe, resolveProbe, promoteEpisode,
  toApiError,
  type IEpisodeResponse, type IProbeResponse,
  type ICreateEpisodeRequest, type IUpdateEpisodeRequest,
  type IRelateEpisodeRequest, type ICreateProbeRequest,
  type IResolveProbeRequest, type IPromoteEpisodeRequest,
} from '../api/index'

export interface ProbeWithEpisode {
  probe: IProbeResponse
  episode: IEpisodeResponse
}

export function useEpisodes() {
  const episodes: Ref<IEpisodeResponse[]> = ref([])
  const loading = ref(false)
  const error: Ref<string | null> = ref(null)
  const conflictError: Ref<string | null> = ref(null)
  const filterDate: Ref<string | null> = ref(null)
  const filterStrandId: Ref<string | null> = ref(null)
  const filterUnrelated = ref(false)

  async function load() {
    loading.value = true
    error.value = null
    try {
      const params: { date?: string; strandId?: string } = {}
      if (filterDate.value) params.date = filterDate.value
      if (filterStrandId.value) params.strandId = filterStrandId.value
      let result = await fetchEpisodes(params)
      if (filterUnrelated.value) {
        result = result.filter(e => !e.relatedStrandId)
      }
      episodes.value = result
    } catch {
      error.value = '加载片段事实失败'
    } finally {
      loading.value = false
    }
  }

  function activeProbes(): ProbeWithEpisode[] {
    const result: ProbeWithEpisode[] = []
    for (const ep of episodes.value) {
      for (const p of ep.probes ?? []) {
        if (p.status === 'active') result.push({ probe: p, episode: ep })
      }
    }
    return result
  }

  function resolvedProbes(): ProbeWithEpisode[] {
    const result: ProbeWithEpisode[] = []
    for (const ep of episodes.value) {
      for (const p of ep.probes ?? []) {
        if (p.status !== 'active') result.push({ probe: p, episode: ep })
      }
    }
    return result
  }

  async function doCreate(req: ICreateEpisodeRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await createEpisode(req)
      await load()
      return true
    } catch (e) {
      return handleError(e)
    }
  }

  async function doUpdate(id: string, req: IUpdateEpisodeRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await updateEpisode(id, req)
      await load()
      return true
    } catch (e) {
      return handleError(e)
    }
  }

  async function doRelate(id: string, req: IRelateEpisodeRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await relateEpisode(id, req)
      await load()
      return true
    } catch (e) {
      return handleError(e)
    }
  }

  async function doDelete(id: string, version: number): Promise<boolean> {
    conflictError.value = null
    try {
      await deleteEpisode(id, version)
      await load()
      return true
    } catch (e) {
      return handleError(e)
    }
  }

  async function doCreateProbe(episodeId: string, req: ICreateProbeRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await createProbe(episodeId, req)
      await load()
      return true
    } catch (e) {
      return handleError(e)
    }
  }

  async function doResolveProbe(probeId: string, req: IResolveProbeRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await resolveProbe(probeId, req)
      await load()
      return true
    } catch (e) {
      return handleError(e)
    }
  }

  async function doPromote(episodeId: string, req: IPromoteEpisodeRequest): Promise<boolean> {
    conflictError.value = null
    try {
      await promoteEpisode(episodeId, req)
      await load()
      return true
    } catch (e) {
      return handleError(e)
    }
  }

  function handleError(e: unknown): false {
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
    episodes,
    loading,
    error,
    conflictError,
    filterDate,
    filterStrandId,
    filterUnrelated,
    load,
    activeProbes,
    resolvedProbes,
    doCreate,
    doUpdate,
    doRelate,
    doDelete,
    doCreateProbe,
    doResolveProbe,
    doPromote,
  }
}
