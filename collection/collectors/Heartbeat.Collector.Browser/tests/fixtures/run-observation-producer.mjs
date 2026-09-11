// Bundle the real TypeScript producer with the same esbuild dependency used by Vite.
// The parent HTTP test supplies only an isolated loopback address and disposable storage.
import { createRequire } from 'node:module'
import { writeFileSync, rmSync } from 'node:fs'
import { pathToFileURL, fileURLToPath } from 'node:url'
const require = createRequire(new URL('../../package.json', import.meta.url))
const { build } = require('esbuild')
const result = await build({
  entryPoints: [fileURLToPath(new URL('./observation-producer.ts', import.meta.url))],
  bundle: true, platform: 'node', format: 'esm', write: false,
  define: { 'import.meta.env.MODE': '"production"' },
})
const compiled = `${process.argv[4]}.mjs`
try {
  writeFileSync(compiled, result.outputFiles[0].text)
  await import(pathToFileURL(compiled).href)
} finally {
  rmSync(compiled, { force: true })
}
