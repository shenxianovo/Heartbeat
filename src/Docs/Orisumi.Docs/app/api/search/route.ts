import { source } from '@/lib/source';
import { createFromSource } from 'fumadocs-core/search/server';

export const dynamic = 'force-static';

export const { staticGET: GET } = createFromSource(source, {
  buildIndex: (page) => ({
    id: page.url,
    url: page.url,
    title: page.data.title ?? page.url,
    description: page.data.description,
    structuredData: page.data.structuredData,
    // The section of the page, such as `core` or `adr`, used by the section filter of the search dialog.
    tag: page.slugs[0],
  }),
});
