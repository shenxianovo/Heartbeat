import { describe, expect, it } from 'vitest'
import {
  candidateBytes,
  classificationFingerprint,
  createExportSelection,
  previewMatches,
} from './appCatalogSettings'

describe('classification preview', () => {
  it('is valid only for the exact form state that produced it', () => {
    const draft = {
      identityKey: 'mac:com.example.app',
      targetAppKey: 'example',
      newAppDisplayName: 'Example',
    }
    const fingerprint = classificationFingerprint(draft)

    expect(previewMatches(fingerprint, draft)).toBe(true)
    expect(previewMatches(fingerprint, { ...draft, targetAppKey: 'example-beta' })).toBe(false)
    expect(previewMatches(fingerprint, { ...draft, newAppDisplayName: 'Example Beta' })).toBe(false)
  })
})

describe('catalog candidate export', () => {
  it('starts with no local override selected', () => {
    const selection = createExportSelection(['mac:com.example.one', 'win:example-two'])

    expect([...selection]).toEqual([])
  })

  it('decodes the server-provided bytes without parsing catalog JSON', () => {
    const expected = new TextEncoder().encode('{"catalogVersion":2}\n')
    let binary = ''
    for (const byte of expected) binary += String.fromCharCode(byte)

    expect(candidateBytes(btoa(binary))).toEqual(expected)
  })
})
