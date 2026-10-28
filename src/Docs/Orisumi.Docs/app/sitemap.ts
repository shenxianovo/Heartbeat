import type { MetadataRoute } from 'next';
import { source } from '@/lib/source';
import { getCanonicalUrl } from '@/lib/metadata';

export const dynamic = 'force-static';

export default function sitemap(): MetadataRoute.Sitemap {
  return source.getPages().map((page) => ({ url: getCanonicalUrl(page.url) }));
}
