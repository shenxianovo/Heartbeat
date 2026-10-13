import { cpSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const docsDirectory = fileURLToPath(new URL('../', import.meta.url));
const standaloneDirectory = resolve(docsDirectory, '.next/standalone');

for (const directory of ['public', '.next/static']) {
  cpSync(resolve(docsDirectory, directory), resolve(standaloneDirectory, directory), {
    recursive: true,
  });
}

process.env.HEARTBEAT_TEST_REPORTS_DIR = resolve(
  docsDirectory,
  process.env.HEARTBEAT_TEST_REPORTS_DIR ?? '../../../TestResults',
);

await import(new URL('../.next/standalone/server.js', import.meta.url));
