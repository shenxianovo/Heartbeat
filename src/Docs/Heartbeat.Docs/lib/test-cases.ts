import type { TUnitReport } from 'fumadocs-test-reports/tunit';

/** A case may produce several parameterized results. Keep the native report intact. */
export function selectCaseResults(report: TUnitReport | null, caseId: string): TUnitReport | null {
  if (!report) return null;
  return {
    ...report,
    summary: undefined,
    totalDurationMs: undefined,
    groups: report.groups.flatMap((group) => {
      const tests = group.tests.filter((test) => test.customProperties?.some(
        (property) => property.key === 'caseId' && property.value === caseId,
      ));
      return tests.length ? [{ ...group, summary: undefined, tests }] : [];
    }),
  };
}
