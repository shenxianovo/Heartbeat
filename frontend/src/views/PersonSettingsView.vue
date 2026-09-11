<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Button } from '@/components/ui/button'
import { establishPerson, fetchPersonFacts, fetchPersonSettings, removePersonAssociation, savePersonAssociation, type PersonAssociation, type PersonFactPage, type PersonSettings } from '../api/person'

const settings = ref<PersonSettings | null>(null)
const page = ref<PersonFactPage | null>(null)
const busy = ref(false)
const error = ref('')
const editing = ref<PersonAssociation | null>(null)
const target = ref('')
const lower = ref('')
const upper = ref('')
const allHistory = ref(false)
const family = ref('segments')
const queryStart = ref('')
const queryEnd = ref('')
const applied = ref({ family: 'segments', start: null as string | null, end: null as string | null })
const offset = ref(0)
const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone

function local(iso: string | null) {
  if (!iso) return ''
  const date = new Date(iso)
  return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, -1)
}
function bound(value: string, original: string | null = null) {
  if (!value) return null
  const date = new Date(value)
  if (!Number.isFinite(date.getTime())) throw new Error('请输入有效时间。')
  if (original && date.getTime() === new Date(original).getTime()) return original
  return date.toISOString()
}
function display(iso: string | null | undefined, empty = '无限') {
  return iso ? new Date(iso).toLocaleString() : empty
}
function objectName(id: string) {
  const object = settings.value?.objects.find(o => o.id === id)
  return object?.name || object?.key || '未知对象'
}
function details(fact: PersonFactPage['items'][number]['fact']) {
  if (fact.aspect === 'input') {
    return Object.fromEntries(Object.entries(fact).filter(([key]) => key !== 'payload' && key !== 'result'))
  }
  return fact
}
function title(fact: PersonFactPage['items'][number]['fact']) {
  if (!['desktop-activity', 'selected-page', 'account-location', 'activity'].includes(fact.aspect ?? '')) return '观测事实'
  const payload = fact.payload
  return payload && typeof payload === 'object' && 'title' in payload && typeof payload.title === 'string' ? payload.title : '观测事实'
}
async function run(action: () => Promise<void>) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try { await action() } catch (e) { error.value = e instanceof Error ? e.message : '操作失败，请重试。' }
  finally { busy.value = false }
}
async function history() {
  page.value = null
  page.value = await fetchPersonFacts(applied.value.family, applied.value.start, applied.value.end, offset.value)
}
async function refresh() {
  page.value = null
  settings.value = await fetchPersonSettings()
  offset.value = 0
  await history()
}
function reset() {
  editing.value = null; target.value = ''; lower.value = ''; upper.value = ''; allHistory.value = false
}
function edit(link: PersonAssociation) {
  editing.value = link
  target.value = link.objectId
  lower.value = local(link.start); upper.value = local(link.end)
  allHistory.value = link.start === null && link.end === null
}
async function save() {
  await run(async () => {
    if (!target.value) throw new Error('请选择设备或账号。')
    if (!lower.value && !upper.value && !allHistory.value) throw new Error('请明确确认全部历史，或填写适用边界。')
    const start = bound(lower.value, editing.value?.start)
    const end = bound(upper.value, editing.value?.end)
    // PostgreSQL validates the interval at microsecond precision; Date cannot compare
    // preserved bounds that fall within the same millisecond without losing information.
    await savePersonAssociation(editing.value?.id ?? null, { objectId: target.value, start, end })
    reset()
    await refresh()
  })
}
async function filter() {
  await run(async () => {
    const start = bound(queryStart.value), end = bound(queryEnd.value)
    if (start && end && new Date(start) >= new Date(end)) throw new Error('查询终点必须晚于起点。')
    applied.value = { family: family.value, start, end }; offset.value = 0
    await history()
  })
}
onMounted(() => run(refresh))
</script>

<template>
  <main class="person-settings">
    <header><div><h1>本人关联与事实</h1><p>明确的使用者关系让过去的观测可按本人回看。</p></div>
      <Button variant="glass" as-child><router-link to="/settings">返回设置</router-link></Button>
    </header>
    <p v-if="error" role="alert" class="error">{{ error }}</p>
    <p v-if="!settings">{{ busy ? '加载中…' : '未能加载设置。' }}</p>
    <Button v-if="!settings && !busy" @click="run(refresh)">重试</Button>
    <template v-if="settings">
      <section v-if="!settings.person">
        <h2>建立本人资料</h2><p>本人是观测对象。创建后，可明确关联设备或账号的适用时间。</p>
        <Button :disabled="busy" aria-label="建立本人资料" @click="run(async () => { await establishPerson(); await refresh() })">建立本人资料</Button>
      </section>
      <section v-else>
        <h2>确认使用时间</h2>
        <p>关联只改变查询结果，原始事实保持不变。空起点表示此前，空终点表示此后；起点包含，终点不包含。多个区间可重叠或不连续。</p>
        <p class="hint">以下时间使用 {{ timezone }}。设备离线后仍可关联和查询历史。</p>
        <form aria-label="维护本人关联" @submit.prevent="save">
          <label>设备或账号<select v-model="target" aria-label="关联设备或账号" :disabled="busy" required>
            <option value="" disabled>选择已观测的设备或账号</option>
            <option v-for="t in settings.objects" :key="t.id" :value="t.id">{{ t.kind === 'machine' ? '设备' : '账号' }} · {{ t.name || t.key }}</option>
          </select></label>
          <div class="bounds">
            <label>适用起点（包含）<input v-model="lower" aria-label="适用起点" type="datetime-local" step="0.001" :disabled="busy"></label>
            <label>适用终点（不包含）<input v-model="upper" aria-label="适用终点" type="datetime-local" step="0.001" :disabled="busy"></label>
          </div>
          <label v-if="!lower && !upper" class="confirmation"><input v-model="allHistory" type="checkbox" :disabled="busy">我确认该设备或账号的全部历史均适用于本人</label>
          <div class="actions"><Button type="submit" :disabled="busy">{{ editing ? '保存纠正' : '创建关联' }}</Button><Button v-if="editing" type="button" variant="glass" :disabled="busy" @click="reset">取消纠正</Button></div>
        </form>
        <p v-if="!settings.objects.length" class="hint">尚无可关联的设备或账号；摄入的个人对象 事实仍可直接回看。</p>
        <p v-if="!settings.associations.length" class="hint">尚未建立使用者关联。</p>
        <ul class="associations">
          <li v-for="link in settings.associations" :key="link.id">
            <div><strong>{{ objectName(link.objectId) }}</strong>
              <p :title="`${link.start ?? '无下界'} / ${link.end ?? '无上界'}`">[{{ display(link.start, '无下界') }}, {{ display(link.end, '无上界') }})</p></div>
            <div class="actions"><Button variant="glass" :aria-label="`纠正关联 ${link.id}`" :disabled="busy" @click="edit(link)">纠正</Button>
              <Button variant="glass" :aria-label="`移除关联 ${link.id}`" :disabled="busy" @click="run(async () => { await removePersonAssociation(link.id); reset(); await refresh() })">移除</Button></div>
          </li>
        </ul>
        <details><summary>个人对象 引用</summary><code>{{ settings.person.reference }}</code><p class="hint">供明确描述本人的事实使用；采集器不会从登录身份自动推断。</p></details>
      </section>
      <section>
        <h2>本人事实</h2><p>包含个人对象、关联机器及有证据关联的 App、关联账号在适用期内的事实。</p>
        <form aria-label="按本人筛选" @submit.prevent="filter">
          <label>事实家族<select v-model="family" aria-label="事实家族" :disabled="busy"><option value="segments">Segment · 持续区间</option><option value="events">Event · 发生时刻</option></select></label>
          <div class="bounds"><label>查询起点<input v-model="queryStart" type="datetime-local" step="0.001" :disabled="busy"></label><label>查询终点<input v-model="queryEnd" type="datetime-local" step="0.001" :disabled="busy"></label></div>
          <Button type="submit" :disabled="busy">按本人筛选</Button>
        </form>
        <p class="hint">各来源的事实可能同时发生。Browser、VRChat 和 System 时长不能相加解释为注意力。</p>
        <template v-if="page">
          <p aria-live="polite">{{ page.totalCount }} 条事实 <span v-for="source in page.sources" :key="source.source ?? ''" class="source-count">{{ source.source ?? '未提供来源' }}：{{ source.count }}</span></p>
          <article v-for="item in page.items" :key="item.fact.id ?? undefined" class="fact">
            <h3>{{ title(item.fact) }}</h3><p class="hint">{{ item.fact.source ?? '未提供来源' }} · {{ item.fact.foi?.name || item.fact.foi?.key || '未知对象' }}</p>
            <template v-if="item.fact.start">
              <p>原始区间：{{ display(item.fact.start) }} → {{ display(item.fact.end) }}</p>
              <p>有效覆盖：{{ item.effectiveSeconds }} 秒</p>
              <ul><li v-for="range in item.effectiveIntervals" :key="range.start" :title="`${range.start} / ${range.end}`">{{ display(range.start) }} → {{ display(range.end) }}</li></ul>
            </template>
            <p v-else>发生时刻：{{ display(item.fact.occurredAt) }}</p>
            <details><summary>{{ item.fact.aspect === 'input' ? '事实身份与时间' : '原始事实详情' }}</summary><pre>{{ JSON.stringify(details(item.fact), null, 2) }}</pre></details>
          </article>
          <p v-if="!page.items.length" class="hint">这个范围内没有适用事实。</p>
          <div class="actions"><Button variant="glass" :disabled="busy || offset === 0" @click="run(async () => { offset = Math.max(0, offset - 20); await history() })">上一页</Button>
            <span>{{ page.totalCount === 0 ? 0 : offset + 1 }}–{{ Math.min(offset + page.items.length, page.totalCount) }} / {{ page.totalCount }}</span>
            <Button variant="glass" :disabled="busy || offset + 20 >= page.totalCount" @click="run(async () => { offset += 20; await history() })">下一页</Button></div>
        </template>
      </section>
    </template>
  </main>
</template>

<style scoped>
.person-settings { position: relative; z-index: 10; max-width: 960px; margin: auto; padding: 2rem; }
header, .actions, .associations li { display: flex; align-items: center; justify-content: space-between; gap: 1rem; }
h1 { font-size: 1.6rem; font-weight: 700; } h2 { font-size: 1.2rem; font-weight: 600; margin-bottom: .7rem; } h3 { font-weight: 600; }
section { background: color-mix(in srgb, var(--card) 85%, transparent); border-radius: .8rem; padding: 1.3rem; margin-top: 2rem; padding-top: 1.5rem; border-top: 1px solid var(--border); }
p { margin: .5rem 0; line-height: 1.6; } .hint, header p { color: var(--muted-foreground); font-size: .9rem; }
form { display: grid; gap: .8rem; margin: 1.2rem 0; } label { display: grid; gap: .4rem; } .bounds { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
input, select { border: 1px solid var(--border); background: var(--card); color: var(--foreground); border-radius: .5rem; padding: .55rem; min-width: 0; }
.confirmation { display: flex; align-items: center; } .actions { justify-content: flex-start; flex-wrap: wrap; }
.associations { margin: 1.5rem 0; } .associations li { padding: 1rem 0; border-bottom: 1px solid var(--border); }
.fact { margin: 1rem 0; padding: 1rem; border: 1px solid var(--border); border-radius: .7rem; }
.fact ul { padding-left: 1rem; } summary { cursor: pointer; margin: .6rem 0; } pre { white-space: pre-wrap; overflow-wrap: anywhere; font-size: .8rem; } code { overflow-wrap: anywhere; }
.source-count { display: inline-block; margin-left: 1rem; font-size: .85rem; } .error { color: var(--destructive); }
@media (max-width: 640px) { .person-settings { padding: 1rem; } .bounds { grid-template-columns: 1fr; } header, .associations li { align-items: flex-start; flex-direction: column; } }
</style>
