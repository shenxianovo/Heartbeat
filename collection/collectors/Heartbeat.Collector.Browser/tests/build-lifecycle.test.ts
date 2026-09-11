import { execFileSync } from 'node:child_process'
import { cpSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { expect, it } from 'vitest'

const browser = resolve(import.meta.dirname, '..')

function files(directory: string): Record<string, string> {
  return Object.fromEntries(readdirSync(directory, { recursive: true, withFileTypes: true })
    .filter(entry => entry.isFile()).map(entry => {
      const path = join(entry.parentPath, entry.name)
      return [path.slice(directory.length + 1), readFileSync(path, 'utf8')]
    }))
}

it('tests leave stale dist and Package untouched while a real build stages current extension bytes', () => {
  const directory = mkdtempSync(join(tmpdir(), 'heartbeat-browser-build-lifecycle-'))
  const env = { ...process.env }
  delete env.HEARTBEAT_BROWSER_BUILD_DIR
  try {
    symlinkSync(join(browser, 'node_modules'), join(directory, 'node_modules'), 'dir')
    cpSync(join(browser, 'vite.config.ts'), join(directory, 'vite.config.ts'))
    for (const path of ['src', 'tests', 'dist', 'Package/browser-extension']) mkdirSync(join(directory, path), { recursive: true })
    writeFileSync(join(directory, 'package.json'), '{"type":"module"}')
    writeFileSync(join(directory, 'manifest.json'), '{"manifest_version":3,"name":"fixture","version":"1.0.0"}')
    writeFileSync(join(directory, 'src/background.ts'), 'globalThis.currentBuildEvidence = "current-source"')
    writeFileSync(join(directory, 'src/options.ts'), 'globalThis.currentOptionsEvidence = true')
    writeFileSync(join(directory, 'options.html'), '<script type="module" src="/src/options.ts"></script>')
    writeFileSync(join(directory, 'tests/smoke.test.ts'), "import { it, expect } from 'vitest'; it('fixture runs', () => expect(1).toBe(1))")
    writeFileSync(join(directory, 'dist/background.js'), 'stale-dist')
    writeFileSync(join(directory, 'Package/browser-extension/background.js'), 'previously-staged-package')
    const packageDirectory = join(directory, 'Package/browser-extension')
    const before = files(packageDirectory), oldDist = files(join(directory, 'dist'))

    execFileSync(process.execPath, [join(browser, 'node_modules/vitest/vitest.mjs'), 'run'], { cwd: directory, env, stdio: 'pipe' })
    expect(files(packageDirectory)).toEqual(before)
    expect(files(join(directory, 'dist'))).toEqual(oldDist)

    execFileSync(process.execPath, [join(browser, 'node_modules/vite/bin/vite.js'), 'build'], { cwd: directory, env, stdio: 'pipe' })
    expect(files(packageDirectory)).toEqual(files(join(directory, 'dist')))
    expect(readFileSync(join(packageDirectory, 'background.js'), 'utf8')).toContain('current-source')
    expect(readFileSync(join(packageDirectory, 'manifest.json'), 'utf8')).toBe(readFileSync(join(directory, 'manifest.json'), 'utf8'))
  } finally { rmSync(directory, { recursive: true, force: true }) }
}, 15_000)
