import { docsLlms } from '@/lib/source';
import { getPublicUrl } from '@/lib/shared';

export const revalidate = false;

export async function GET() {
  const index = (await docsLlms.index()).replace(/\]\((\/[^)]*)\)/g, (_, path) => `](${getPublicUrl(path)})`);
  return new Response(index);
}
