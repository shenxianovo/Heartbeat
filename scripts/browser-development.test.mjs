import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, renameSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { publishDevelopmentExtension } from './browser-development.mjs'

test('updating a development extension keeps its binding and refuses retargeting retained storage', () => {
  const root = mkdtempSync(join(tmpdir(), 'heartbeat-extension-update-'))
  try {
    const source = join(root, 'source'), output = join(root, 'extension')
    mkdirSync(source)
    writeFileSync(join(source, 'background.js'), 'v1')
    writeFileSync(join(source, 'manifest.json'), JSON.stringify({ key: 'stable-key' }))
    const binding = { profileId: 'a'.repeat(32), token: 'b'.repeat(64), port: 32001 }
    publishDevelopmentExtension(source, output, binding)
    writeFileSync(join(source, 'background.js'), 'v2')
    publishDevelopmentExtension(source, output, binding)
    assert.equal(readFileSync(join(output, 'background.js'), 'utf8'), 'v2')
    assert.deepEqual(JSON.parse(readFileSync(join(output, 'desktop-binding.json'), 'utf8')), binding)
    assert.throws(() => publishDevelopmentExtension(source, output, { ...binding, profileId: 'c'.repeat(32) }), /Profile/)
    assert.equal(readFileSync(join(output, 'background.js'), 'utf8'), 'v2')
    writeFileSync(join(source, 'manifest.json'), JSON.stringify({ key: 'different-key' }))
    assert.throws(() => publishDevelopmentExtension(source, output, binding), /identity/)
    writeFileSync(join(source, 'manifest.json'), JSON.stringify({ key: 'stable-key' }))
    renameSync(output, output + '.previous')
    assert.throws(() => publishDevelopmentExtension(source, output, { ...binding, profileId: 'c'.repeat(32) }), /Profile/)
    assert.equal(readFileSync(join(output, 'background.js'), 'utf8'), 'v2')
  } finally { rmSync(root, { recursive: true, force: true }) }
})
