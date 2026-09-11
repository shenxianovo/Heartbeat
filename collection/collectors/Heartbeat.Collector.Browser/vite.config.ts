import { defineConfig } from 'vitest/config'
import { copyFileSync, cpSync, rmSync } from 'node:fs'

const outputDirectory = process.env.HEARTBEAT_BROWSER_BUILD_DIR ?? 'dist'

export default defineConfig({
  build: {
    outDir: outputDirectory,
    emptyOutDir: true,
    target: 'es2022',
    minify: false,
    rollupOptions: {
      input: { background: 'src/background.ts', options: 'options.html' },
      output: { entryFileNames: '[name].js', format: 'es' },
    },
  },
  plugins: [
    {
      name: 'copy-manifest',
      apply: 'build',
      closeBundle() {
        copyFileSync('manifest.json', `${outputDirectory}/manifest.json`)
        if (process.env.HEARTBEAT_BROWSER_BUILD_DIR) return
        rmSync('Package/browser-extension', { recursive: true, force: true })
        cpSync('dist', 'Package/browser-extension', { recursive: true })
      },
    },
  ],
  test: { environment: 'node' },
})
