import { findPath, type Root } from 'fumadocs-core/page-tree';
import { appName, siteUrl } from './site-config.ts';

export function getCanonicalUrl(path: string) {
  return new URL(path.endsWith('/') ? path : `${path}/`, siteUrl).href;
}

export function getPageTitle(page: { url: string; data: { title?: string } }, tree: Root) {
  if (page.url === '/start') return `${appName} 文档：保存一个人经历了什么`;

  const path = findPath(tree.children, (node) =>
    node.type === 'page' ? node.url === page.url : node.type === 'folder' && node.index?.url === page.url);
  const title = page.data.title ?? String(path?.at(-1)?.name ?? page.url);
  const folders = (path ?? []).flatMap((node) =>
    node.type === 'folder' && node.name !== title ? [String(node.name)] : []);

  return `${[title, ...folders.reverse()].join(' · ')} | ${appName}`;
}
