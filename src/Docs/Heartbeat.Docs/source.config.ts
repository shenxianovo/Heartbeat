import { remarkMdxMermaid } from 'fumadocs-core/mdx-plugins';
import { defineConfig } from 'fumadocs-mdx/config';
import { remarkGitChangelog } from 'fumadocs-page-history/remark';
import { gitConfig } from './lib/site-config';

export default defineConfig({
  mdxOptions: {
    remarkPlugins: [
      remarkMdxMermaid,
      [remarkGitChangelog, {
        repository: { url: `https://github.com/${gitConfig.user}/${gitConfig.repo}` },
        authorMappings: [{ gitName: 'shenxianovo', githubUsername: 'shenxianovo' }],
        display: { locale: 'zh-CN' },
      }],
    ],
  },
});
