import { rehypeCodeDefaultOptions, remarkMdxMermaid, remarkSteps } from 'fumadocs-core/mdx-plugins';
import { transformerMetaHighlight } from '@shikijs/transformers';
import { defineConfig } from 'fumadocs-mdx/config';
import { remarkGitChangelog } from 'fumadocs-page-history/remark';
import { gitConfig } from './lib/site-config';

export default defineConfig({
  mdxOptions: {
    rehypeCodeOptions: {
      ...rehypeCodeDefaultOptions,
      // `{1,3}` after the language highlights lines and keeps JSON examples valid.
      transformers: [...rehypeCodeDefaultOptions.transformers!, transformerMetaHighlight()],
    },
    remarkPlugins: [
      remarkSteps,
      remarkMdxMermaid,
      [remarkGitChangelog, {
        repository: { url: `https://github.com/${gitConfig.user}/${gitConfig.repo}` },
        authorMappings: [{ gitName: 'shenxianovo', githubUsername: 'shenxianovo' }],
        display: { locale: 'zh-CN', displayAuthorsInsideCommitLine: true },
        workspaceChanges: false,
      }],
    ],
  },
});
