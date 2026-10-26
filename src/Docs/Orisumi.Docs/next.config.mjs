import { createMDX } from 'fumadocs-mdx/next';
import { PHASE_DEVELOPMENT_SERVER } from 'next/constants.js';

const withMDX = createMDX();

/** @type {import('next').NextConfig} */
const config = {
  reactStrictMode: true,
  trailingSlash: true,
  images: { unoptimized: true },
};

/** @param {string} phase */
export default (phase) => withMDX(phase === PHASE_DEVELOPMENT_SERVER
  ? {
    ...config,
    // The dev server maps /page.md to the Markdown export route; the static build writes the same files in scripts/finish-export.mjs.
    rewrites: async () => [{ source: '/:path((?!llms\\.mdx/).+)\\.md', destination: '/llms.mdx/:path/content.md' }],
  }
  : { ...config, output: 'export' });
