import { readdir, readFile, stat } from 'node:fs/promises';
import { resolve } from 'node:path';
import { parseTUnitReport } from 'fumadocs-test-reports/tunit';

export async function readLatestIntegrationReport() {
  const directory = process.env.HEARTBEAT_TEST_REPORTS_DIR
    ?? resolve(process.cwd(), '../../../TestResults');

  // Reports belong to the runtime-mounted directory, outside the server bundle.
  try {
    const entries = await readdir(/* turbopackIgnore: true */ directory, { withFileTypes: true });
    const files = await Promise.all(entries
      .filter((entry) => entry.isFile()
        && entry.name.startsWith('Heartbeat.Integration.Tests-')
        && entry.name.endsWith('.tunit-report.json'))
      .map(async (entry) => {
        const path = resolve(directory, entry.name);
        return { path, modifiedAt: (await stat(/* turbopackIgnore: true */ path)).mtimeMs };
      }));
    files.sort((a, b) => b.modifiedAt - a.modifiedAt || a.path.localeCompare(b.path));
    if (!files.length) return null;

    return parseTUnitReport(JSON.parse(await readFile(/* turbopackIgnore: true */ files[0].path, 'utf8')));
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw error;
  }
}
