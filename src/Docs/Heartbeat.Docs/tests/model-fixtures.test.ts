import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

for (const [name, count] of [['scenarios', 100], ['controls', 18], ['captured', 6]] as const) {
  test(`downloaded ${name} exposes ${count} uniquely identified, reviewable records`, async () => {
    const file = JSON.parse(await readFile(resolve(root, 'public/model-stress', `${name}.json`), 'utf8'));
    assert.equal(file.count, count);
    assert.equal(file.cases.length, count);
    assert.equal(new Set(file.cases.map((item: { id: string }) => item.id)).size, count);
    for (const item of file.cases) {
      assert.ok(item.source);
      assert.ok(item.evidence);
      assert.ok(item.fields);
      assert.ok(item.references);
      assert.ok(item.properties);
      assert.equal(typeof item.expectedValid, 'boolean');
    }
  });
}
