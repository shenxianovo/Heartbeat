import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { SiteIcon } from '@/components/site-icon';
import { appName, gitConfig } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <>
          <SiteIcon />
          {appName}
        </>
      ),
      url: '/core',
    },
    githubUrl: `https://github.com/${gitConfig.user}/${gitConfig.repo}`,
  };
}
