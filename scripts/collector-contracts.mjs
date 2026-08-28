#!/usr/bin/env node
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
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
const factsDirectory = join(root, 'collection/contracts/facts')
const baselinePath = join(factsDirectory, 'baseline.json')

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

function factContracts() {
  return readdirSync(factsDirectory)
    .filter(name => name.endsWith('.schema.json'))
    .sort()
    .map(name => {
      const path = join(factsDirectory, name)
      const bytes = readFileSync(path)
      const document = JSON.parse(bytes)
      return { name, path, bytes, document, hash: sha256(bytes) }
    })
}

function validateContracts(contracts) {
  const identities = new Set()
  for (const contract of contracts) {
    const value = contract.document
    for (const field of ['schemaId', 'schemaMajor', 'schemaRevision', 'factKind', 'payloadSchema']) {
      if (value[field] === undefined) throw new Error(`${contract.name}: missing ${field}`)
    }
    if (!['segment', 'event'].includes(value.factKind))
      throw new Error(`${contract.name}: executable Collector Protocol v1 supports only segment/event`)
    const identity = `${value.schemaId}@${value.schemaMajor}.${value.schemaRevision}`
    if (identities.has(identity)) throw new Error(`duplicate Fact Schema identity ${identity}`)
    identities.add(identity)
  }
  if (contracts.length !== 5) throw new Error(`expected exactly 5 authoritative Fact Schemas, found ${contracts.length}`)
}

function baselineFor(contracts) {
  return {
    formatVersion: 1,
    contracts: contracts.map(contract => ({
      schemaId: contract.document.schemaId,
      schemaMajor: contract.document.schemaMajor,
      schemaRevision: contract.document.schemaRevision,
      hash: contract.hash,
      document: contract.name,
    })),
  }
}

function compareBaseline(expected, actual, label) {
  const expectedText = `${JSON.stringify(expected, null, 2)}\n`
  const actualText = `${JSON.stringify(actual, null, 2)}\n`
  if (expectedText !== actualText)
    throw new Error(`${label} is stale; run: node scripts/collector-contracts.mjs baseline`)
}

function checkBaseRef(current, baseRef) {
  let old
  try {
    old = JSON.parse(execFileSync('git', ['show', `${baseRef}:collection/contracts/facts/baseline.json`], {
      cwd: root,
      encoding: 'utf8',
    }))
  } catch {
    process.stdout.write(`Contract baseline does not exist at ${baseRef}; treating this branch as the initial baseline.\n`)
    return
  }
  const currentByIdentity = new Map(current.contracts.map(item => [
    `${item.schemaId}@${item.schemaMajor}.${item.schemaRevision}`,
    item,
  ]))
  for (const previous of old.contracts) {
    const identity = `${previous.schemaId}@${previous.schemaMajor}.${previous.schemaRevision}`
    const candidate = currentByIdentity.get(identity)
    if (candidate && candidate.hash !== previous.hash)
      throw new Error(`${identity} changed bytes without changing schemaMajor/schemaRevision`)
  }
}

function checkBrowserPayload() {
  const dist = join(root, 'collection/collectors/Heartbeat.Collector.Browser/dist')
  const packaged = join(root, 'collection/collectors/Heartbeat.Collector.Browser/Package/browser-extension')
  if (!existsSync(dist))
    throw new Error('Browser dist is missing; run npm run build before contract check')
  const snapshot = directory => Object.fromEntries(listFiles(directory)
    .filter(path => !path.endsWith('package-metadata.json'))
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
        name !== 'collector-manifest.template.json' &&
        name !== 'schemas' && !name.startsWith('schemas/')
    },
  })
}

function listFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name)
    return entry.isDirectory() ? listFiles(path) : [path]
  })
}

function stageBrowserArtifact(destination, manifest) {
  const extension = join(destination, 'browser-extension')
  if (!existsSync(join(extension, 'background.js')))
    throw new Error('Browser Package payload is missing; run npm run build and sync dist first')
  rmSync(join(extension, 'package-metadata.json'), { force: true })
  const files = listFiles(extension)
    .filter(path => !path.endsWith('package-metadata.json'))
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
  const descriptorPath = join(destination, 'artifacts/browser-extension.json')
  mkdirSync(dirname(descriptorPath), { recursive: true })
  writeFileSync(descriptorPath, descriptor)
  const artifactHash = sha256(descriptor)
  writeFileSync(
    join(extension, 'package-metadata.json'),
    `${JSON.stringify({ artifactHash }, null, 2)}\n`,
  )
  const artifact = manifest.artifacts.find(item => item.artifactId === 'browser.extension')
  artifact.size = descriptor.length
  artifact.contentHash = artifactHash
}

function stagePackage(name, destination) {
  const source = packageSources[name]
  if (!source) throw new Error(`unknown package '${name}'`)
  const output = resolve(destination)
  rmSync(output, { recursive: true, force: true })
  mkdirSync(output, { recursive: true })
  copyPackageSource(source, output)
  const manifest = readJson(join(source, 'collector-manifest.template.json'))
  if (name === 'browser') stageBrowserArtifact(output, manifest)
  const contracts = factContracts()
  const byId = new Map(contracts.map(contract => [contract.document.schemaId, contract]))
  for (const outputDeclaration of manifest.outputs) {
    const contract = byId.get(outputDeclaration.schema.id)
    if (!contract) throw new Error(`${name}: unknown schema ${outputDeclaration.schema.id}`)
    const schema = outputDeclaration.schema
    if (schema.major !== contract.document.schemaMajor || schema.revision !== contract.document.schemaRevision)
      throw new Error(`${name}: manifest identity does not match ${contract.name}`)
    const target = join(output, schema.document)
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, contract.bytes)
    schema.hash = contract.hash
  }
  writeFileSync(join(output, 'collector-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`)
  process.stdout.write(`Staged ${name} Collector Package at ${output}\n`)
}

const [command, ...args] = process.argv.slice(2)
try {
  const contracts = factContracts()
  validateContracts(contracts)
  const baseline = baselineFor(contracts)
  if (command === 'baseline') {
    writeFileSync(baselinePath, `${JSON.stringify(baseline, null, 2)}\n`)
  } else if (command === 'check') {
    compareBaseline(baseline, readJson(baselinePath), 'Fact Schema baseline')
    checkBrowserPayload()
    const baseIndex = args.indexOf('--base-ref')
    if (baseIndex >= 0) checkBaseRef(baseline, args[baseIndex + 1])
    process.stdout.write('Collector Fact Schemas and evolution baseline are consistent.\n')
  } else if (command === 'stage' && args.length === 2) {
    stagePackage(args[0], args[1])
  } else {
    throw new Error('usage: collector-contracts.mjs baseline | check [--base-ref REF] | stage <browser|system|reference-fixture> <output>')
  }
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`)
  process.exitCode = 1
}
