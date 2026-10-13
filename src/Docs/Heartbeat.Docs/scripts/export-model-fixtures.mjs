import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const docs = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = process.env.HEARTBEAT_MODEL_FIXTURES_DIR
  ?? resolve(docs, '../../../tests/Heartbeat.Model.Tests/Fixtures');
const output = resolve(docs, 'public/model-stress');
const files = ['digital', 'systems', 'physical', 'provenance', 'provenance-negative', 'captured-systems'];
const records = (await Promise.all(files.map(async (name) => JSON.parse(await readFile(resolve(source, `${name}.json`), 'utf8'))))).flat();
const groups = {
  scenarios: records.filter((record) => /^[ABCD]\d{2}$/.test(record.id)),
  captured: records.filter((record) => record.id.startsWith('B-RAW-')),
  controls: records.filter((record) => !/^[ABCD]\d{2}$/.test(record.id) && !record.id.startsWith('B-RAW-')),
};
if (groups.scenarios.length !== 100 || groups.controls.length !== 18 || groups.captured.length !== 6)
  throw new Error('The documented scenario inventory no longer matches the canonical fixtures. Update evidence and counts deliberately.');
await mkdir(output, { recursive: true });
for (const [name, cases] of Object.entries(groups)) {
  await writeFile(resolve(output, `${name}.json`), JSON.stringify({
    baselineCommit: '1330e68e4b9373c8e844d95cf5e4d7c012445675',
    scope: 'Reviewable typed record slices; most scenarios are source-documentation-grounded constructed fixtures, not live production integrations. See each evidence field.',
    count: cases.length,
    cases,
  }, null, 2) + '\n');
}
await writeFile(resolve(output, 'source-evidence.json'), await readFile(resolve(source, '../Evidence/source-evidence.json')));
console.log('Exported 100 scenarios, 18 controls, and 6 captured-record fixtures.');
