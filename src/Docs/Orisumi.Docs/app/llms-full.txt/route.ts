import { docsLlms } from '@/lib/llms';

export const dynamic = 'force-static';

export async function GET() {
  return new Response(await docsLlms.full());
}
