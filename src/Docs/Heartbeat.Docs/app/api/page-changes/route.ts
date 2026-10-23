import { createWorkspaceHandler } from 'fumadocs-page-history/workspace';

export const runtime = 'nodejs';
export const dynamic = 'force-dynamic';

export const GET = createWorkspaceHandler({
  contentDirectory: 'src/Docs/Heartbeat.Docs/content/docs',
});
