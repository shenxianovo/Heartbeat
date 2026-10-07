import { connection } from 'next/server';
import { TUnitReport } from 'fumadocs-test-reports/tunit/ui';
import { readLatestIntegrationReport } from '@/lib/test-reports';

export async function IntegrationTestReport() {
  await connection();
  const report = await readLatestIntegrationReport();
  if (!report) return <p>尚未生成集成测试报告。运行集成测试后刷新此页。</p>;

  return <TUnitReport report={report} />;
}
