#!/usr/bin/env node
import { createHash } from 'node:crypto'
import {
  cpSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const packageSources = {
  browser: join(root, 'collection/collectors/Heartbeat.Collector.Browser/Package'),
  system: join(root, 'collection/desktop/Heartbeat.Collector.System/Package'),
  'reference-fixture': join(root, 'collection/hub/Heartbeat.Collection.Hub.Tests/Fixtures/ReferenceCollectorPackage'),
}

function sha256(content) {
  return `sha256:${createHash('sha256').update(content).digest('hex')}`
}

function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8'))
}

function checkBrowserPayload() {
  const dist = join(root, 'collection/collectors/Heartbeat.Collector.Browser/dist')
  const packaged = join(root, 'collection/collectors/Heartbeat.Collector.Browser/Package/browser-extension')
  if (!existsSync(dist))
    throw new Error('Browser dist is missing; run npm run build before contract check')
  const snapshot = directory => Object.fromEntries(listFiles(directory)
    .filter(path => !path.endsWith('collector-artifact-ref.json'))
    .map(path => [relative(directory, path).replaceAll('\\', '/'), sha256(readFileSync(path))])
    .sort(([left], [right]) => left.localeCompare(right)))
  if (JSON.stringify(snapshot(dist)) !== JSON.stringify(snapshot(packaged)))
    throw new Error('Browser source and packaged extension differ; run npm run build and sync dist into Package/browser-extension')
}

function copyPackageSource(source, destination) {
  cpSync(source, destination, {
    recursive: true,
    filter: path => {
      const name = relative(source, path).replaceAll('\\', '/')
      return name !== 'collector-manifest.json' &&
        name !== 'collector-manifest.template.json'
    },
  })
}

function listFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name)
    return entry.isDirectory() ? listFiles(path) : [path]
  })
}

function stageBrowserArtifact(destination) {
  const extension = join(destination, 'browser-extension')
  if (!existsSync(join(extension, 'background.js')))
    throw new Error('Browser Package payload is missing; run npm run build and sync dist first')
  rmSync(join(extension, 'collector-artifact-ref.json'), { force: true })
  const files = listFiles(extension)
    .filter(path => !path.endsWith('collector-artifact-ref.json'))
    .sort()
    .map(path => {
      const bytes = readFileSync(path)
      return {
        path: relative(destination, path).replaceAll('\\', '/'),
        size: bytes.length,
        contentHash: sha256(bytes),
      }
    })
  const descriptor = Buffer.from(`${JSON.stringify({
    kind: 'heartbeat.browser.external-host',
    entrypoint: 'browser-extension/manifest.json',
    files,
  }, null, 2)}\n`)
  const descriptorPath = join(destination, 'artifacts/browser-extension.artifact.json')
  mkdirSync(dirname(descriptorPath), { recursive: true })
  writeFileSync(descriptorPath, descriptor)

}

function populateContentReferences(destination, manifest) {
  if (manifest.observationDeclaration) {
    const declaration = readFileSync(join(destination, manifest.observationDeclaration.document))
    manifest.observationDeclaration.hash = sha256(declaration)
  }
  for (const artifact of manifest.artifacts) {
    const content = readFileSync(join(destination, artifact.entrypoint))
    artifact.size = content.length
    artifact.contentHash = sha256(content)
  }
}

function validateGeneratedReferencesAreNotPinned() {
  for (const [name, source] of Object.entries(packageSources)) {
    const manifest = readJson(join(source, 'collector-manifest.template.json'))
    if (manifest.observationDeclaration?.hash !== undefined)
      throw new Error(`${name}: observation declaration hash must be generated during staging`)
    for (const artifact of manifest.artifacts) {
      if (artifact.size !== undefined || artifact.contentHash !== undefined)
        throw new Error(`${name}: Artifact size/hash must be generated during staging`)
    }
  }
}

function currentPlatform() {
  const operatingSystem = {
    win32: 'windows',
    darwin: 'macos',
    linux: 'linux',
  }[process.platform]
  const architecture = {
    x64: 'x64',
    arm64: 'arm64',
  }[process.arch]
  if (!operatingSystem || !architecture)
    throw new Error(`unsupported staging platform ${process.platform}/${process.arch}`)
  return { operatingSystem, architecture }
}

function includeCurrentTestPlatform(manifest) {
  const { operatingSystem, architecture } = currentPlatform()
  for (const artifact of manifest.artifacts) {
    if (!artifact.selector.os.includes(operatingSystem)) artifact.selector.os.push(operatingSystem)
    if (!artifact.selector.arch.includes(architecture)) artifact.selector.arch.push(architecture)
  }
}

function stagePackage(name, destination, includeTestPlatform = false, version) {
  const source = packageSources[name]
  if (!source) throw new Error(`unknown package '${name}'`)
  const output = resolve(destination)
  rmSync(output, { recursive: true, force: true })
  mkdirSync(output, { recursive: true })
  copyPackageSource(source, output)
  const manifest = readJson(join(source, 'collector-manifest.template.json'))
  if (includeTestPlatform) includeCurrentTestPlatform(manifest)
  if (version !== undefined) manifest.version = version
  if (name === 'browser') {
    const extensionManifestPath = join(output, 'browser-extension/manifest.json')
    const extensionManifest = readJson(extensionManifestPath)
    extensionManifest.version = manifest.version
    writeFileSync(extensionManifestPath, `${JSON.stringify(extensionManifest, null, 2)}\n`)
    stageBrowserArtifact(output)
  }
  populateContentReferences(output, manifest)
  const manifestBytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`)
  writeFileSync(join(output, 'collector-manifest.json'), manifestBytes)
  if (name === 'browser') {
    // Bootstrap reference is derived from the final manifest, outside its descriptor's payload
    // hash to avoid a circular identity. It grants no authority: the Host compares every field.
    writeFileSync(join(output, 'browser-extension/collector-artifact-ref.json'), `${JSON.stringify({
      packageId: manifest.packageId,
      packageVersion: manifest.version,
      packageContentHash: sha256(manifestBytes),
      artifactId: manifest.artifacts[0].artifactId,
      artifactHash: manifest.artifacts[0].contentHash,
    }, null, 2)}\n`)
  }
  process.stdout.write(`Staged ${name} Collector Package at ${output}\n`)
}

const [command, ...args] = process.argv.slice(2)
try {
  if (command === 'check') {
    validateGeneratedReferencesAreNotPinned()
    checkBrowserPayload()
    process.stdout.write('Collector Package content references are consistent.\n')
  } else if (command === 'stage' && args.length >= 2) {
    let includeTestPlatform = false
    let version
    for (let index = 2; index < args.length; index++) {
      if (args[index] === '--include-current-test-platform') includeTestPlatform = true
      else if (args[index] === '--version' && args[0] === 'browser' && version === undefined) {
        version = args[++index]
        if (!/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(version ?? '') ||
            version.split('.').some(part => Number(part) > 65535) || version === '0.0.0')
          throw new Error('Browser version must be stable X.Y.Z, each part <= 65535, and nonzero')
      } else throw new Error(`unsupported stage option ${args[index]}`)
    }
    stagePackage(args[0], args[1], includeTestPlatform, version)
  } else {
    throw new Error('usage: collector-contracts.mjs check | stage <browser|system|reference-fixture> <output> [--include-current-test-platform] [--version X.Y.Z (browser only)]')
  }
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`)
  process.exitCode = 1
}
