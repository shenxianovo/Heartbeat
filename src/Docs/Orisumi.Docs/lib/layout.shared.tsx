import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { SiteIcon } from '@/components/site-icon';
import { appName, gitConfig } from './site-config';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <>
          <SiteIcon />
          {appName}
        </>
      ),
      url: '/start',
    },
    githubUrl: `https://github.com/${gitConfig.user}/${gitConfig.repo}`,
  };
}
