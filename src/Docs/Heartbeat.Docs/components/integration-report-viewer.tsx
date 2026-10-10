'use client';

import { useEffect, useState } from 'react';
import type { TUnitReport as Report } from 'fumadocs-test-reports/tunit';
import { TUnitReport } from 'fumadocs-test-reports/tunit/ui';

export function IntegrationReportViewer({ report }: { report: Report }) {
  const [selectedTestId, setSelectedTestId] = useState<string | null>(null);
  useEffect(() => {
    const readSelection = () => setSelectedTestId(new URL(window.location.href).searchParams.get('test'));
    readSelection();
    window.addEventListener('popstate', readSelection);
    return () => window.removeEventListener('popstate', readSelection);
  }, []);
  return <TUnitReport report={report} selectedTestId={selectedTestId} onTestSelect={(test) => {
    const id = test?.id ?? null;
    setSelectedTestId(id);
    const url = new URL(window.location.href);
    if (id) url.searchParams.set('test', id);
    else url.searchParams.delete('test');
    window.history.replaceState(null, '', url);
  }} />;
}
