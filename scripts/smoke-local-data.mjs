#!/usr/bin/env node

import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

function usage() {
  console.log(`Usage: node scripts/smoke-local-data.mjs <check|baseline|verify> [options]

Validate the restored local dataset and prove that a newly run client advances it.

Commands:
  check       Validate aggregate data invariants without writing a baseline.
  baseline    Validate and save the current aggregate watermark.
  verify      Validate again and require Segment or InputEvent data to advance.

Options:
  --compose-file PATH   Compose file (default: compose.local.yml)
  --env-file PATH       Environment file (default: .env.local)
  --baseline-file PATH  Watermark file (default: .local/local-data-smoke-baseline.json)
  -h, --help            Show this help
`)
}

const args = process.argv.slice(2)
if (args.length === 0 || args.includes('-h') || args.includes('--help')) {
  usage()
  process.exit(args.length === 0 ? 2 : 0)
}

const command = args.shift()
if (!['check', 'baseline', 'verify'].includes(command)) {
  console.error(`Unknown command: ${command}`)
  usage()
  process.exit(2)
}

let composeFile = resolve(repositoryRoot, 'compose.local.yml')
let envFile = resolve(repositoryRoot, '.env.local')
let baselineFile = resolve(repositoryRoot, '.local/local-data-smoke-baseline.json')

while (args.length > 0) {
  const option = args.shift()
  const value = args.shift()
  if (!value) {
    console.error(`Missing value for ${option}.`)
    process.exit(2)
  }
  if (option === '--compose-file') composeFile = resolve(repositoryRoot, value)
  else if (option === '--env-file') envFile = resolve(repositoryRoot, value)
  else if (option === '--baseline-file') baselineFile = resolve(repositoryRoot, value)
  else {
    console.error(`Unknown option: ${option}`)
    process.exit(2)
  }
}

for (const [label, path] of [['Compose file', composeFile], ['Environment file', envFile]]) {
  if (!existsSync(path)) {
    console.error(`${label} not found: ${path}`)
    process.exit(1)
  }
}

const sql = String.raw`
SELECT json_build_object(
  'capturedAtUtc', to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
  'users', (SELECT count(*) FROM "Users"),
  'devices', (SELECT count(*) FROM "Devices"),
  'segments', (SELECT count(*) FROM "ActivitySegments"),
  'inputEvents', (SELECT count(*) FROM "InputEvents"),
  'latestSegmentEndUtc', (SELECT to_char(max("EndTime") AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') FROM "ActivitySegments"),
  'latestInputUtc', (SELECT to_char(max("Timestamp") AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') FROM "InputEvents"),
  'latestDeviceSeenUtc', (SELECT to_char(max("LastSeen") AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') FROM "Devices"),
  'sourceCounts', coalesce((
    SELECT json_object_agg("Source", total)
    FROM (SELECT "Source", count(*) AS total FROM "ActivitySegments" GROUP BY "Source") sources
  ), '{}'::json),
  'qualitySignals', json_build_object(
    'exactDuplicateSemanticRows', (SELECT coalesce(sum(total - 1), 0) FROM (
      SELECT count(*) AS total FROM "ActivitySegments"
      GROUP BY "DeviceId", "Source", "IdentityKey", "StartTime", "EndTime"
      HAVING count(*) > 1
    ) duplicate_groups),
    'systemOverlapRows', (SELECT count(*) FROM (
      SELECT "StartTime", max("EndTime") OVER (
        PARTITION BY "DeviceId" ORDER BY "StartTime", "EndTime", "Id"
        ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
      ) AS previous_max_end
      FROM "ActivitySegments" WHERE "Source" = 'system'
    ) ordered_system WHERE "StartTime" < previous_max_end),
    'appForeignKeyMismatch', (SELECT count(*) FROM "ActivitySegments" s
      JOIN "AppIdentities" i ON i."Id" = s."AppIdentityId"
      WHERE s."AppId" IS NOT NULL AND s."AppId" <> i."AppId"),
    'segmentsOver24Hours', (SELECT count(*) FROM "ActivitySegments"
      WHERE "EndTime" - "StartTime" > interval '24 hours')
  ),
  'violations', json_build_object(
    'invalidSegmentRanges', (SELECT count(*) FROM "ActivitySegments" WHERE "EndTime" < "StartTime"),
    'blankSegmentSource', (SELECT count(*) FROM "ActivitySegments" WHERE btrim("Source") = ''),
    'blankSegmentIdentity', (SELECT count(*) FROM "ActivitySegments" WHERE btrim("IdentityKey") = ''),
    'systemWithoutAppIdentity', (SELECT count(*) FROM "ActivitySegments" WHERE "Source" = 'system' AND "AppIdentityId" IS NULL),
    'inputWithoutCodeSet', (SELECT count(*) FROM "InputEvents" WHERE btrim("CodeSet") = ''),
    'unknownInputCodeSet', (SELECT count(*) FROM "InputEvents" WHERE "CodeSet" NOT IN ('heartbeat-key-position-v1', 'windows-vk-v1')),
    'orphanSegmentDevice', (SELECT count(*) FROM "ActivitySegments" s LEFT JOIN "Devices" d ON d."Id" = s."DeviceId" WHERE d."Id" IS NULL),
    'orphanInputDevice', (SELECT count(*) FROM "InputEvents" i LEFT JOIN "Devices" d ON d."Id" = i."DeviceId" WHERE d."Id" IS NULL),
    'orphanAppIdentity', (SELECT count(*) FROM "ActivitySegments" s LEFT JOIN "AppIdentities" a ON a."Id" = s."AppIdentityId" WHERE s."AppIdentityId" IS NOT NULL AND a."Id" IS NULL),
    'futureSegments', (SELECT count(*) FROM "ActivitySegments" WHERE "EndTime" > now() + interval '5 minutes'),
    'futureInputs', (SELECT count(*) FROM "InputEvents" WHERE "Timestamp" > now() + interval '5 minutes'),
    'futureDevices', (SELECT count(*) FROM "Devices" WHERE "LastSeen" > now() + interval '5 minutes')
  )
)::text;
`

const docker = spawnSync('docker', [
  'compose', '--file', composeFile, '--env-file', envFile,
  'exec', '-T', 'db',
  'psql', '--username=heartbeat', '--dbname=heartbeat', '--no-align', '--tuples-only',
  '--command', sql
], { cwd: repositoryRoot, encoding: 'utf8' })

if (docker.error) {
  console.error(`Could not run Docker: ${docker.error.message}`)
  process.exit(1)
}
if (docker.status !== 0) {
  process.stderr.write(docker.stderr)
  process.exit(docker.status ?? 1)
}

let current
try {
  current = JSON.parse(docker.stdout.trim())
} catch {
  console.error('The local database did not return the expected aggregate JSON.')
  process.stderr.write(docker.stderr)
  process.exit(1)
}

const failures = Object.entries(current.violations)
  .filter(([, count]) => Number(count) !== 0)
  .map(([name, count]) => `${name}=${count}`)

if (command === 'verify') {
  if (!existsSync(baselineFile)) {
    console.error(`Baseline not found: ${baselineFile}`)
    console.error('Run the baseline command before starting the client.')
    process.exit(1)
  }

  const baseline = JSON.parse(readFileSync(baselineFile, 'utf8'))
  if (Number(current.segments) < Number(baseline.segments)) {
    failures.push(`segment count regressed (${baseline.segments} -> ${current.segments})`)
  }
  if (Number(current.inputEvents) < Number(baseline.inputEvents)) {
    failures.push(`input event count regressed (${baseline.inputEvents} -> ${current.inputEvents})`)
  }
  for (const [name, count] of Object.entries(current.qualitySignals)) {
    const before = Number(baseline.qualitySignals?.[name] ?? count)
    if (Number(count) > before) failures.push(`${name} worsened (${before} -> ${count})`)
  }

  const advanced = [
    ['Segment', baseline.latestSegmentEndUtc, current.latestSegmentEndUtc],
    ['InputEvent', baseline.latestInputUtc, current.latestInputUtc]
  ].filter(([, before, after]) => after && (!before || Date.parse(after) > Date.parse(before)))

  if (advanced.length === 0) {
    failures.push('no Segment or InputEvent watermark advanced after the baseline')
  } else {
    console.log(`New client data observed: ${advanced.map(([name]) => name).join(', ')}`)
  }
}

console.log(`Dataset: users=${current.users}, devices=${current.devices}, segments=${current.segments}, inputEvents=${current.inputEvents}`)
console.log(`Segments by source: ${JSON.stringify(current.sourceCounts)}`)
console.log(`Quality signals: ${JSON.stringify(current.qualitySignals)}`)
console.log(`Latest watermarks: segment=${current.latestSegmentEndUtc ?? 'none'}, input=${current.latestInputUtc ?? 'none'}, device=${current.latestDeviceSeenUtc ?? 'none'}`)

if (failures.length > 0) {
  console.error(`Local data smoke failed: ${failures.join('; ')}`)
  process.exit(1)
}

if (command === 'baseline') {
  mkdirSync(dirname(baselineFile), { recursive: true })
  writeFileSync(baselineFile, `${JSON.stringify(current, null, 2)}\n`)
  console.log(`Baseline saved: ${baselineFile}`)
} else {
  console.log('Local data smoke passed.')
}
