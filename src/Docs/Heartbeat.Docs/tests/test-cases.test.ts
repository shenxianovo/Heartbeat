import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { TUnitReport } from 'fumadocs-test-reports/tunit';
import { selectCaseResults } from '../lib/test-cases.ts';

const summary = { total: 3, passed: 3, failed: 0, skipped: 0, cancelled: 0, timedOut: 0, flaky: 0 };
const report: TUnitReport = {
  schemaVersion: 1, assemblyName: 'Integration', summary, totalDurationMs: 5000,
  groups: [{ className: 'Registration', summary, tests: [
    { id: 'a', displayName: 'renamed (a)', status: 'Passed', customProperties: [{ key: 'caseId', value: 'registration' }] },
    { id: 'b', displayName: 'renamed (b)', status: 'Failed', customProperties: [{ key: 'caseId', value: 'registration' }] },
    { id: 'c', displayName: 'registration', status: 'Passed', customProperties: [{ key: 'other', value: 'registration' }] },
  ] }],
};

test('matches stable properties and keeps all parameterized results without full-run counts', () => {
  const selected = selectCaseResults(report, 'registration')!;
  assert.deepEqual(selected.groups[0].tests.map((test) => test.id), ['a', 'b']);
  assert.equal(selected.summary, undefined);
  assert.equal(selected.groups[0].summary, undefined);
  assert.equal(selected.totalDurationMs, undefined);
  assert.equal(report.summary, summary);
  assert.equal(report.groups[0].tests.length, 3);
});

test('distinguishes absent report from a report without matching evidence', () => {
  assert.equal(selectCaseResults(null, 'registration'), null);
  assert.deepEqual(selectCaseResults(report, 'missing')!.groups, []);
});
