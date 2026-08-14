<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import {
  appCatalogAdminErrorOf,
  deleteAdminAppCatalogOverride,
  exportAdminAppCatalogCandidate,
  fetchAdminAppCatalog,
  fetchAdminAppCatalogAudit,
  previewAdminAppCatalogOverride,
  previewDeleteAdminAppCatalogOverride,
  setAdminAppCatalogOverride,
  toApiError,
  type AppCatalogAdminAuditResponse,
  type AppCatalogAdminInventoryResponse,
  type AppCatalogReconciliationResponse,
} from '../api/index'
import {
  candidateBytes,
  classificationFingerprint,
  createExportSelection,
  previewMatches,
} from '../appCatalog/appCatalogSettings'

const router = useRouter()
const loading = ref(true)
const busy = ref(false)
const error = ref('')
const success = ref('')
const inventory = ref<AppCatalogAdminInventoryResponse | null>(null)
const audit = ref<AppCatalogAdminAuditResponse[]>([])

const identityKey = ref('')
const targetAppKey = ref('')
const newAppDisplayName = ref('')
const preview = ref<AppCatalogReconciliationResponse | null>(null)
const previewFingerprint = ref<string | null>(null)

const deleteIdentityKey = ref('')
const deletePreview = ref<AppCatalogReconciliationResponse | null>(null)
const exportSelection = ref<Set<string>>(createExportSelection([]))

const products = computed(() => inventory.value?.products ?? [])
const provisionalProducts = computed(() => products.value.filter(product => product.isProvisional))
const formalProducts = computed(() => products.value.filter(product => !product.isProvisional))
const activeOverrides = computed(() => inventory.value?.activeOverrides ?? [])
const selectedIdentity = computed(() => products.value
  .flatMap(product => product.identities ?? [])
  .find(identity => identity.key === identityKey.value))
const targetIsExisting = computed(() => formalProducts.value.some(product => product.key === targetAppKey.value.trim()))
const draft = computed(() => ({
  identityKey: identityKey.value,
  targetAppKey: targetAppKey.value,
  newAppDisplayName: newAppDisplayName.value,
}))
const previewIsCurrent = computed(() =>
  preview.value !== null && previewMatches(previewFingerprint.value, draft.value),
)

onMounted(load)

async function load() {
  loading.value = true
  error.value = ''
  try {
    const [nextInventory, nextAudit] = await Promise.all([
      fetchAdminAppCatalog(),
      fetchAdminAppCatalogAudit(),
    ])
    inventory.value = nextInventory
    audit.value = nextAudit
    const activeKeys = new Set((nextInventory.activeOverrides ?? []).map(item => item.identityKey ?? ''))
    exportSelection.value = new Set(
      [...exportSelection.value].filter(key => activeKeys.has(key)),
    )
  } catch (caught) {
    const apiError = toApiError(caught)
    if (apiError.kind === 'http' && apiError.status === 403) {
      inventory.value = null
      audit.value = []
      await router.replace({ path: '/settings', query: { catalogDenied: '1' } })
      return
    }
    error.value = messageFor(caught, '加载 App Catalog 失败，请重试')
  } finally {
    loading.value = false
  }
}

function configure(key: string) {
  const localOverride = activeOverrides.value.find(item => item.identityKey === key)
  identityKey.value = key
  targetAppKey.value = localOverride?.targetAppKey ?? ''
  newAppDisplayName.value = ''
  preview.value = null
  previewFingerprint.value = null
  success.value = ''
  error.value = ''
}

async function runPreview() {
  if (!identityKey.value || !targetAppKey.value.trim() || busy.value) return
  if (!targetIsExisting.value && !newAppDisplayName.value.trim()) {
    error.value = '新产品需要填写 DisplayName'
    return
  }
  busy.value = true
  error.value = ''
  success.value = ''
  try {
    preview.value = await previewAdminAppCatalogOverride(
      identityKey.value,
      targetAppKey.value,
      targetIsExisting.value ? undefined : newAppDisplayName.value,
    )
    previewFingerprint.value = classificationFingerprint(draft.value)
  } catch (caught) {
    preview.value = null
    previewFingerprint.value = null
    error.value = messageFor(caught, '预览失败')
  } finally {
    busy.value = false
  }
}

async function commitOverride() {
  if (!previewIsCurrent.value || busy.value) return
  busy.value = true
  error.value = ''
  success.value = ''
  try {
    await setAdminAppCatalogOverride(
      identityKey.value,
      targetAppKey.value,
      targetIsExisting.value ? undefined : newAppDisplayName.value,
    )
    success.value = `${identityKey.value} 已归类到 ${targetAppKey.value.trim().toLowerCase()}`
    identityKey.value = ''
    preview.value = null
    previewFingerprint.value = null
    await load()
  } catch (caught) {
    error.value = messageFor(caught, '保存 Override 失败')
  } finally {
    busy.value = false
  }
}

async function runDeletePreview(key: string) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  success.value = ''
  deleteIdentityKey.value = key
  deletePreview.value = null
  try {
    deletePreview.value = await previewDeleteAdminAppCatalogOverride(key)
  } catch (caught) {
    deleteIdentityKey.value = ''
    error.value = messageFor(caught, '删除预览失败')
  } finally {
    busy.value = false
  }
}

async function commitDelete() {
  if (!deleteIdentityKey.value || !deletePreview.value || busy.value) return
  busy.value = true
  error.value = ''
  success.value = ''
  const key = deleteIdentityKey.value
  try {
    await deleteAdminAppCatalogOverride(key)
    success.value = `${key} 的本地 Override 已删除`
    deleteIdentityKey.value = ''
    deletePreview.value = null
    await load()
  } catch (caught) {
    error.value = messageFor(caught, '删除 Override 失败')
  } finally {
    busy.value = false
  }
}

function toggleExport(key: string, selected: boolean) {
  const next = new Set(exportSelection.value)
  if (selected) next.add(key)
  else next.delete(key)
  exportSelection.value = next
}

async function exportCandidate() {
  if (busy.value || exportSelection.value.size === 0) return
  busy.value = true
  error.value = ''
  success.value = ''
  try {
    const result = await exportAdminAppCatalogCandidate([...exportSelection.value].sort())
    if (!result.hasChanges || !result.content || !result.fileName) {
      error.value = '所选映射没有产生 Catalog 内容变化'
      return
    }
    const blob = new Blob([candidateBytes(result.content)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = result.fileName
    anchor.click()
    URL.revokeObjectURL(url)
    success.value = `已导出 ${result.fileName}`
  } catch (caught) {
    error.value = messageFor(caught, '导出候选 Catalog 失败')
  } finally {
    busy.value = false
  }
}

function messageFor(caught: unknown, fallback: string): string {
  const typed = appCatalogAdminErrorOf(caught)
  if (typed?.message) return typed.message
  const apiError = toApiError(caught)
  if (apiError.kind === 'network') return '网络连接失败，请检查网络后重试'
  if (apiError.kind === 'http') return `${fallback}（HTTP ${apiError.status}）`
  return fallback
}

function duration(seconds?: number): string {
  const value = seconds ?? 0
  if (value >= 3600) return `${(value / 3600).toFixed(1)}h`
  return `${Math.round(value / 60)}m`
}

function auditLabel(eventType?: string): string {
  return ({
    'catalog-applied': '内置 Catalog 协调',
    'override-created': '创建 Override',
    'override-updated': '更新 Override',
    'override-deleted': '删除 Override',
    'override-promoted': 'Override 已沉淀',
  } as Record<string, string>)[eventType ?? ''] ?? eventType ?? '未知事件'
}
</script>

<template>
  <div class="catalog-page">
    <header class="page-header">
      <div>
        <h1>App Catalog</h1>
        <p>把平台 AppIdentity 归到稳定的跨平台产品，并导出可进入代码审查的候选 JSON。</p>
      </div>
      <router-link to="/settings" class="btn">返回设置</router-link>
    </header>

    <div v-if="error" class="notice error">{{ error }}</div>
    <div v-if="success" class="notice success">{{ success }}</div>
    <p v-if="loading" class="muted">加载中…</p>
    <template v-else-if="inventory">
      <div v-if="inventory.isRollbackCompatible" class="notice warning">
        当前后端处于 Catalog 回滚兼容模式，管理与导出操作不可用。
      </div>

      <section class="summary-grid">
        <div class="summary"><span>正式版本</span><strong>v{{ inventory.catalogVersion }}</strong></div>
        <div class="summary"><span>待归类产品</span><strong>{{ provisionalProducts.length }}</strong></div>
        <div class="summary"><span>活动 Override</span><strong>{{ activeOverrides.length }}</strong></div>
      </section>

      <section class="panel">
        <div class="section-heading">
          <div>
            <h2>待归类应用</h2>
            <p>使用量只提供聚合上下文；原始标题与 Owner 数据不会出现在这里。</p>
          </div>
        </div>
        <div v-if="provisionalProducts.length" class="item-list">
          <article v-for="product in provisionalProducts" :key="product.id" class="item">
            <div class="item-main">
              <strong>{{ product.displayName }}</strong>
              <code>{{ product.key }}</code>
              <span class="muted">
                {{ product.usage?.segmentCount ?? 0 }} 段 · {{ duration(product.usage?.durationSeconds) }} ·
                {{ product.usage?.deviceCount ?? 0 }} 台设备
              </span>
            </div>
            <div class="identity-list">
              <div v-for="identity in product.identities" :key="identity.id" class="identity-row">
                <div>
                  <code>{{ identity.key }}</code>
                  <span class="source">{{ identity.effectiveSource }}</span>
                </div>
                <button
                  class="btn primary"
                  :data-test="`configure-${identity.key}`"
                  :disabled="inventory.isRollbackCompatible"
                  @click="configure(identity.key ?? '')"
                >归类</button>
              </div>
            </div>
          </article>
        </div>
        <p v-else class="muted empty">当前没有待归类产品。</p>
      </section>

      <section v-if="selectedIdentity" class="panel editor">
        <div class="section-heading">
          <div>
            <h2>归类 {{ selectedIdentity.key }}</h2>
            <p>先预览影响；任何输入变化都会使旧预览失效。</p>
          </div>
          <button class="text-button" @click="identityKey = ''">关闭</button>
        </div>
        <label>
          目标 App Key
          <input
            v-model="targetAppKey"
            data-test="target-key"
            list="formal-app-keys"
            placeholder="例如 chrome"
          />
          <datalist id="formal-app-keys">
            <option v-for="product in formalProducts" :key="product.id" :value="product.key">
              {{ product.displayName }}
            </option>
          </datalist>
        </label>
        <label v-if="targetAppKey.trim() && !targetIsExisting">
          新产品 DisplayName
          <input v-model="newAppDisplayName" placeholder="例如 Google Chrome" />
        </label>
        <div class="actions">
          <button data-test="preview" class="btn" :disabled="busy || !targetAppKey.trim()" @click="runPreview">
            {{ busy ? '处理中…' : '预览影响' }}
          </button>
          <button
            data-test="commit"
            class="btn primary"
            :disabled="busy || !previewIsCurrent"
            @click="commitOverride"
          >确认保存</button>
        </div>
        <div v-if="preview" class="impact" :class="{ stale: !previewIsCurrent }">
          <strong>{{ previewIsCurrent ? '当前预览' : '输入已变化，请重新预览' }}</strong>
          <div class="impact-grid">
            <span>Identity {{ preview.identityKeys?.length ?? 0 }}</span>
            <span>移除产品 {{ preview.productsRemoved ?? 0 }}</span>
            <span>图标处理 {{ preview.iconsMovedOrRemoved ?? 0 }}</span>
            <span>当前设备 {{ preview.currentDevicesAffected ?? 0 }}</span>
            <span>知识变更/去重 {{ preview.knowledgeRowsChangedOrDeduplicated ?? 0 }}</span>
            <span>缓存失效 {{ preview.questionCachesInvalidated ?? 0 }}</span>
          </div>
          <div v-if="preview.removedProducts?.length" class="impact-details">
            <h3>将移除的产品</h3>
            <div v-for="product in preview.removedProducts" :key="product.id">
              <code>{{ product.key }}</code> · {{ product.displayName }}
              <span v-if="product.isProvisional" class="source">provisional</span>
            </div>
          </div>
          <div v-if="preview.iconImpacts?.length" class="impact-details">
            <h3>图标处理</h3>
            <div v-for="item in preview.iconImpacts" :key="item.resolution">
              {{ item.resolution }} · {{ item.count ?? 0 }} 项
            </div>
          </div>
          <div v-if="preview.knowledgeChanges?.length" class="impact-details">
            <h3>知识引用变更</h3>
            <div v-for="(item, index) in preview.knowledgeChanges" :key="`${item.category}-${index}`">
              <span class="source">{{ item.category }}</span>
              <code>{{ item.beforeStepsJson }}</code>
              <span>→</span>
              <code>{{ item.afterStepsJson }}</code>
            </div>
          </div>
          <div v-if="preview.knowledgeDeduplications?.length" class="impact-details">
            <h3>知识去重</h3>
            <div v-for="item in preview.knowledgeDeduplications" :key="item.category">
              {{ item.category }} · 删除 {{ item.removedRows ?? 0 }} 条重复记录
            </div>
          </div>
        </div>
      </section>

      <section class="panel">
        <div class="section-heading">
          <div>
            <h2>本地 Override</h2>
            <p>默认不参与导出；只勾选确认适合沉淀到代码库的映射。</p>
          </div>
          <button
            data-test="export-candidate"
            class="btn primary"
            :disabled="busy || exportSelection.size === 0 || inventory.isRollbackCompatible"
            @click="exportCandidate"
          >导出 Catalog JSON</button>
        </div>
        <div v-if="activeOverrides.length" class="item-list">
          <article v-for="item in activeOverrides" :key="item.id" class="override-row">
            <label class="export-check">
              <input
                :data-test="`export-${item.identityKey}`"
                type="checkbox"
                :checked="exportSelection.has(item.identityKey ?? '')"
                @change="toggleExport(item.identityKey ?? '', ($event.target as HTMLInputElement).checked)"
              />
              <span><code>{{ item.identityKey }}</code> → <code>{{ item.targetAppKey }}</code></span>
            </label>
            <div class="actions compact">
              <button class="text-button" @click="configure(item.identityKey ?? '')">修改</button>
              <button
                class="text-button danger"
                :data-test="`delete-preview-${item.identityKey}`"
                @click="runDeletePreview(item.identityKey ?? '')"
              >删除…</button>
            </div>
          </article>
        </div>
        <p v-else class="muted empty">当前没有本地 Override。</p>

        <div v-if="deletePreview && deleteIdentityKey" class="delete-confirm">
          <p>
            删除后 <code>{{ deleteIdentityKey }}</code> 将回落到
            <strong>{{ deletePreview.fallbackSource === 'catalog' ? '内置 Catalog' : '独立 provisional App' }}</strong>
            （目标 <code>{{ deletePreview.targetAppKey }}</code>）。
          </p>
          <div class="actions">
            <button class="btn" @click="deleteIdentityKey = ''; deletePreview = null">取消</button>
            <button data-test="delete-commit" class="btn danger-solid" :disabled="busy" @click="commitDelete">确认删除</button>
          </div>
        </div>
      </section>

      <section class="panel">
        <div class="section-heading"><div><h2>最近审计</h2><p>Catalog 协调和 Override 变更的追加式记录。</p></div></div>
        <div v-if="audit.length" class="audit-list">
          <article v-for="entry in audit" :key="entry.id" class="audit-row">
            <div><strong>{{ auditLabel(entry.eventType) }}</strong><span>{{ entry.occurredAt?.toLocaleString() }}</span></div>
            <code>{{ entry.summaryJson }}</code>
          </article>
        </div>
        <p v-else class="muted empty">暂无审计记录。</p>
      </section>
    </template>
    <div v-else class="panel empty-state">
      <p>无法读取 App Catalog 管理数据。</p>
      <button data-test="retry-load" class="btn" @click="load">重试</button>
    </div>
  </div>
</template>

<style scoped>
.catalog-page { width: min(100%, 1100px); margin: 0 auto; padding: 2rem; }
.page-header, .section-heading, .identity-row, .override-row, .actions { display: flex; align-items: center; justify-content: space-between; gap: 1rem; }
.page-header { margin-bottom: 1.5rem; align-items: flex-start; }
h1 { font-size: 1.6rem; font-weight: 700; }
h2 { font-size: 1rem; font-weight: 650; }
p { color: var(--muted-foreground); font-size: 0.85rem; line-height: 1.5; }
.panel { margin-top: 1rem; padding: 1.1rem; border: 1px solid var(--border); border-radius: 12px; background: color-mix(in srgb, var(--card) 86%, transparent); }
.summary-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: .75rem; }
.summary { display: flex; flex-direction: column; gap: .2rem; padding: .9rem; border: 1px solid var(--border); border-radius: 10px; background: var(--card); }
.summary span, .muted { color: var(--muted-foreground); font-size: .8rem; }
.summary strong { font-size: 1.2rem; }
.item-list, .audit-list, .identity-list { display: flex; flex-direction: column; gap: .65rem; margin-top: 1rem; }
.item, .override-row, .audit-row { padding: .85rem; border: 1px solid var(--border); border-radius: 9px; background: var(--background); }
.item-main { display: flex; flex-wrap: wrap; align-items: baseline; gap: .65rem; }
.identity-row { padding-top: .55rem; border-top: 1px solid var(--border); }
.identity-row > div { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; min-width: 0; }
code { overflow-wrap: anywhere; font-size: .78rem; }
.source { padding: .1rem .4rem; border-radius: 999px; background: var(--secondary); color: var(--muted-foreground); font-size: .68rem; }
.btn { flex-shrink: 0; padding: .42rem .75rem; border: 1px solid var(--border); border-radius: 7px; background: var(--card); color: var(--foreground); cursor: pointer; text-decoration: none; font-size: .82rem; }
.btn.primary { border-color: color-mix(in srgb, var(--primary) 45%, var(--border)); color: var(--primary); }
.btn:disabled { cursor: default; opacity: .45; }
.editor label { display: flex; flex-direction: column; gap: .35rem; margin-top: .85rem; font-size: .8rem; color: var(--muted-foreground); }
input { padding: .55rem .65rem; border: 1px solid var(--border); border-radius: 7px; background: var(--background); color: var(--foreground); }
.actions { justify-content: flex-start; margin-top: .9rem; }
.actions.compact { margin-top: 0; }
.text-button { border: 0; background: none; color: var(--primary); cursor: pointer; }
.text-button.danger { color: rgb(248 113 113); }
.impact, .delete-confirm { margin-top: 1rem; padding: .85rem; border: 1px solid color-mix(in srgb, var(--primary) 35%, var(--border)); border-radius: 9px; background: color-mix(in srgb, var(--primary) 7%, transparent); }
.impact.stale { opacity: .58; border-style: dashed; }
.impact-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: .45rem; margin-top: .6rem; font-size: .78rem; color: var(--muted-foreground); }
.impact-details { display: flex; flex-direction: column; gap: .35rem; margin-top: .75rem; padding-top: .65rem; border-top: 1px solid var(--border); font-size: .75rem; color: var(--muted-foreground); }
.impact-details h3 { color: var(--foreground); font-size: .76rem; font-weight: 650; }
.impact-details > div { display: flex; flex-wrap: wrap; align-items: center; gap: .35rem; }
.export-check { display: flex; align-items: center; gap: .65rem; min-width: 0; }
.danger-solid { background: rgb(185 28 28); border-color: rgb(239 68 68 / .55); color: white; }
.audit-row { display: flex; flex-direction: column; gap: .45rem; }
.audit-row > div { display: flex; justify-content: space-between; gap: 1rem; }
.audit-row span { color: var(--muted-foreground); font-size: .72rem; }
.notice { margin: .75rem 0; padding: .65rem .8rem; border-radius: 8px; font-size: .82rem; }
.notice.error { border: 1px solid rgb(239 68 68 / .35); background: rgb(239 68 68 / .1); color: rgb(252 165 165); }
.notice.success { border: 1px solid rgb(34 197 94 / .35); background: rgb(34 197 94 / .1); color: rgb(134 239 172); }
.notice.warning { border: 1px solid rgb(245 158 11 / .35); background: rgb(245 158 11 / .1); color: rgb(253 230 138); }
.empty { padding: 1rem 0; }
.empty-state { display: flex; align-items: center; justify-content: space-between; gap: 1rem; }
@media (max-width: 700px) {
  .catalog-page { padding: 1rem; }
  .page-header, .section-heading, .override-row { align-items: stretch; flex-direction: column; }
  .summary-grid, .impact-grid { grid-template-columns: 1fr; }
}
</style>
