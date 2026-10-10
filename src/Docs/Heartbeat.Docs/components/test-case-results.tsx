import { connection } from 'next/server';
import { TUnitReport } from 'fumadocs-test-reports/tunit/ui';
import { readLatestIntegrationReport } from '@/lib/test-reports';
import { selectCaseResults } from '@/lib/test-cases';

export async function TestCaseResults({ caseId }: { caseId: string }) {
  await connection();
  const report = await readLatestIntegrationReport();
  const results = selectCaseResults(report, caseId);
  return <>
    <p>Case ID：<code>{caseId}</code></p>
    {!report ? <p>尚未生成集成测试报告，此 Case 没有执行证据。</p> : <>
      {report.timestamp && <p>报告时间：<time>{report.timestamp}</time></p>}
      {report.commitSha ? <p>报告代码版本：<code>{report.commitSha}</code></p> : <p>此报告未记录代码版本。</p>}
      <p><a href="/testing/reports/integration">查看最新原报告</a></p>
      {results!.groups.length ? <>
        <p>以下仅展示此 Case 在该报告中的执行结果。</p>
        <ul>{results!.groups.flatMap((group) => group.tests).map((test) =>
          <li key={test.id}><a href={`/testing/reports/integration?test=${encodeURIComponent(test.id)}`}>在原报告中定位：{test.displayName}</a></li>
        )}</ul>
        <TUnitReport report={results!} showHeader={false} />
      </> : <p>最新报告未包含此 Case，没有可展示的执行结果。</p>}
    </>}
  </>;
}
