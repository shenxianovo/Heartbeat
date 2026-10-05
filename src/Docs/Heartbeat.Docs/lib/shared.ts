import { createGetUrl } from 'fumadocs-core/source';

export const appName = 'Heartbeat';
// Next links use internal paths; fetches, metadata and SVG links use public paths.
export const docsBasePath = '/docs';
export const docsRoute = '/';
export const docsImageRoute = '/og';
export const docsContentRoute = '/llms.mdx';

export const gitConfig = {
  user: 'shenxianovo',
  repo: 'Heartbeat',
  branch: 'rewrite/observation-model',
  contentPath: 'src/Docs/Heartbeat.Docs/content/docs',
};

export function getPublicUrl(path: string) {
  return `${docsBasePath}${path === '/' ? '' : path}`;
}

const getContentUrl = createGetUrl(docsContentRoute);

export function getPageMarkdownUrl(page: { slugs: string[]; locale?: string }) {
  const segments = [...page.slugs, 'content.md'];

  return { segments, url: getPublicUrl(getContentUrl(segments, page.locale)) };
}

const getImageUrl = createGetUrl(docsImageRoute);

export function getPageImageUrl(page: { slugs: string[]; locale?: string }) {
  const segments = [...page.slugs, 'image.png'];

  return { segments, url: getPublicUrl(getImageUrl(segments, page.locale)) };
}
