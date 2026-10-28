import { RootProvider } from 'fumadocs-ui/provider/next';
import './global.css';
import { Inter } from 'next/font/google';
import { i18nProvider } from 'fumadocs-ui/i18n';
import { translations } from '@/lib/translations';
import { appName, siteUrl } from '@/lib/site-config';
import { getSections } from '@/lib/source';
import { SectionSearchDialog } from '@/components/search-dialog';
import type { Metadata } from 'next';
import { SiteFavicon } from '@/components/site-icon';
import Script from 'next/script';

const inter = Inter({
  subsets: ['latin'],
});

// One search filter per section of the sidebar. The value is the first URL segment, the `tag` of the search index.
const searchTags = getSections().map((section) => ({ name: section.name, value: section.slug }));

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: { default: appName, template: `%s | ${appName}` },
  description: 'A lasting record of your experiences.',
};

export default function Layout({ children }: LayoutProps<'/'>) {
  return (
    <html lang="zh-CN" className={inter.className} suppressHydrationWarning>
      <body className="flex flex-col min-h-screen">
        <SiteFavicon />
        <RootProvider
          i18n={i18nProvider(translations)}
          search={{ SearchDialog: SectionSearchDialog, options: { tags: searchTags } }}
        >
          {children}
        </RootProvider>
        <Script src="/analytics.js" strategy="afterInteractive" />
      </body>
    </html>
  );
}
