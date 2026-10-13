import { remarkMdxMermaid } from 'fumadocs-core/mdx-plugins';
import { defineConfig } from 'fumadocs-mdx/config';
import { remarkGitChangelog } from 'fumadocs-page-history/remark';
import { gitConfig } from './lib/shared';

export default defineConfig({
  mdxOptions: {
    remarkPlugins: [
      remarkMdxMermaid,
      [remarkGitChangelog, {
        repoURL: `https://github.com/${gitConfig.user}/${gitConfig.repo}`,
        registry: [{ name: 'shenxianovo', username: 'shenxianovo' }],
        display: { locale: 'zh-CN', timeZone: 'Asia/Shanghai' },
      }],
    ],
  },
});
