// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AppCatalogSettingsView from './AppCatalogSettingsView.vue'

const mocks = vi.hoisted(() => ({
  replace: vi.fn(),
  fetchInventory: vi.fn(),
  fetchAudit: vi.fn(),
  preview: vi.fn(),
  setOverride: vi.fn(),
  previewDelete: vi.fn(),
  deleteOverride: vi.fn(),
  exportCandidate: vi.fn(),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ replace: mocks.replace }),
}))

vi.mock('../api/index', () => ({
  fetchAdminAppCatalog: mocks.fetchInventory,
  fetchAdminAppCatalogAudit: mocks.fetchAudit,
  previewAdminAppCatalogOverride: mocks.preview,
  setAdminAppCatalogOverride: mocks.setOverride,
  previewDeleteAdminAppCatalogOverride: mocks.previewDelete,
  deleteAdminAppCatalogOverride: mocks.deleteOverride,
  exportAdminAppCatalogCandidate: mocks.exportCandidate,
  appCatalogAdminErrorOf: () => null,
  toApiError: (error: unknown) => error,
}))

const inventory = {
  schemaVersion: 1,
  catalogVersion: 1,
  isRollbackCompatible: false,
  activeOverrides: [],
  products: [
    {
      id: 1,
      key: 'example',
      displayName: 'Example',
      isProvisional: false,
      identities: [{ id: 1, key: 'win:example', effectiveSource: 'built-in' }],
      usage: { segmentCount: 5, durationSeconds: 300, deviceCount: 1 },
    },
    {
      id: 2,
      key: 'unknown',
      displayName: 'Unknown',
      isProvisional: true,
      identities: [{ id: 2, key: 'mac:com.example.unknown', effectiveSource: 'provisional' }],
      usage: { segmentCount: 3, durationSeconds: 120, deviceCount: 1 },
    },
  ],
}

describe('AppCatalogSettingsView', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    vi.clearAllMocks()
    mocks.fetchAudit.mockResolvedValue([])
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: vi.fn(() => 'blob:candidate'),
    })
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: vi.fn(),
    })
  })

  it('returns to Settings when the server rejects a direct non-admin visit', async () => {
    mocks.fetchInventory.mockRejectedValue({ kind: 'http', status: 403 })

    mount(AppCatalogSettingsView, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()

    expect(mocks.replace).toHaveBeenCalledWith({
      path: '/settings',
      query: { catalogDenied: '1' },
    })
  })

  it('invalidates a successful preview as soon as the target changes', async () => {
    mocks.fetchInventory.mockResolvedValue(inventory)
    mocks.preview.mockResolvedValue({
      targetAppKey: 'example',
      identityKeys: ['mac:com.example.unknown'],
      removedProducts: [{ id: 2, key: 'unknown', displayName: 'Unknown', isProvisional: true }],
      iconImpacts: [{ resolution: 'move-source', count: 1 }],
      knowledgeChanges: [{ category: 'strand-matcher', beforeStepsJson: '[old]', afterStepsJson: '[new]' }],
      knowledgeDeduplications: [{ category: 'strand-matcher', removedRows: 1 }],
    })

    const wrapper = mount(AppCatalogSettingsView, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()
    await wrapper.get('[data-test="configure-mac:com.example.unknown"]').trigger('click')
    await wrapper.get('[data-test="target-key"]').setValue('example')
    await wrapper.get('[data-test="preview"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-test="commit"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).toContain('将移除的产品')
    expect(wrapper.text()).toContain('知识去重')

    await wrapper.get('[data-test="target-key"]').setValue('example-beta')

    expect(wrapper.get('[data-test="commit"]').attributes('disabled')).toBeDefined()
  })

  it('passes a new product display name through preview', async () => {
    mocks.fetchInventory.mockResolvedValue(inventory)
    mocks.preview.mockResolvedValue({ targetAppKey: 'example-beta' })

    const wrapper = mount(AppCatalogSettingsView, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()
    await wrapper.get('[data-test="configure-mac:com.example.unknown"]').trigger('click')
    await wrapper.get('[data-test="target-key"]').setValue('example-beta')
    await wrapper.get('input[placeholder="例如 Google Chrome"]').setValue('Example Beta')
    await wrapper.get('[data-test="preview"]').trigger('click')
    await flushPromises()

    expect(mocks.preview).toHaveBeenCalledWith(
      'mac:com.example.unknown',
      'example-beta',
      'Example Beta',
    )
  })

  it('keeps export private by default and downloads the selected server bytes', async () => {
    const withOverride = {
      ...inventory,
      activeOverrides: [{
        id: 9,
        identityKey: 'mac:com.example.unknown',
        targetAppKey: 'example',
        status: 'active',
      }],
    }
    mocks.fetchInventory.mockResolvedValue(withOverride)
    mocks.exportCandidate.mockResolvedValue({
      hasChanges: true,
      proposedCatalogVersion: 2,
      fileName: 'app-catalog.v2.candidate.json',
      content: btoa('{"catalogVersion":2}\n'),
    })
    let downloadedName = ''
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      downloadedName = this.download
    })

    const wrapper = mount(AppCatalogSettingsView, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()

    expect(wrapper.get('[data-test="export-candidate"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-test="export-mac:com.example.unknown"]').setValue(true)
    await wrapper.get('[data-test="export-candidate"]').trigger('click')
    await flushPromises()

    expect(mocks.exportCandidate).toHaveBeenCalledWith(['mac:com.example.unknown'])
    expect(downloadedName).toBe('app-catalog.v2.candidate.json')
  })

  it('shows the server fallback before deleting an override', async () => {
    const withOverride = {
      ...inventory,
      activeOverrides: [{
        id: 9,
        identityKey: 'mac:com.example.unknown',
        targetAppKey: 'example',
        status: 'active',
      }],
    }
    mocks.fetchInventory.mockResolvedValue(withOverride)
    mocks.previewDelete.mockResolvedValue({ fallbackSource: 'catalog', targetAppKey: 'example' })
    mocks.deleteOverride.mockResolvedValue({ fallbackSource: 'catalog', targetAppKey: 'example' })

    const wrapper = mount(AppCatalogSettingsView, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()
    await wrapper.get('[data-test="delete-preview-mac:com.example.unknown"]').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('内置 Catalog')
    await wrapper.get('[data-test="delete-commit"]').trigger('click')
    await flushPromises()

    expect(mocks.deleteOverride).toHaveBeenCalledWith('mac:com.example.unknown')
  })

  it('shows audit event semantics and can recover from a failed initial load', async () => {
    mocks.fetchInventory
      .mockRejectedValueOnce({ kind: 'network' })
      .mockResolvedValueOnce(inventory)
    mocks.fetchAudit.mockResolvedValue([{
      id: 1,
      eventType: 'override-promoted',
      occurredAt: new Date('2026-08-14T00:00:00Z'),
      summaryJson: '{}',
    }])

    const wrapper = mount(AppCatalogSettingsView, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('网络连接失败')
    await wrapper.get('[data-test="retry-load"]').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Override 已沉淀')
  })
})
