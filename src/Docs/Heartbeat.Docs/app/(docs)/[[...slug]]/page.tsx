import { source } from '@/lib/source';
import {
  DocsBody,
  DocsDescription,
  DocsPage,
  DocsTitle,
  MarkdownCopyButton,
  ViewOptionsPopover,
} from 'fumadocs-ui/layouts/spacious/page';
import { notFound, redirect } from 'next/navigation';
import { getMDXComponents } from '@/components/mdx';
import { OpenAPIPage } from '@/components/api-page';
import type { ReactNode } from 'react';
import type { Metadata } from 'next';
import { createRelativeLink } from 'fumadocs-ui/mdx';
import {
  getPageImageUrl,
  getPageMarkdownUrl,
  gitConfig,
} from '@/lib/shared';

export default async function Page(props: PageProps<'/[[...slug]]'>) {
  const params = await props.params;
  if (!params.slug?.length) redirect('/core');
  const page = source.getPage(params.slug);
  if (!page) notFound();

  const markdownUrl = getPageMarkdownUrl(page).url;
  const contentPath = page.type === 'openapi' ? 'api/openapi.json' : page.path;
  let content: ReactNode;

  if (page.type === 'openapi') {
    content = <OpenAPIPage {...page.data.getOpenAPIPageProps()} />;
  } else {
    const MDX = page.data.body;
    content = (
      <MDX
        components={getMDXComponents({
          a: createRelativeLink(source, page),
        })}
      />
    );
  }

  return (
    <DocsPage
      toc={page.data.toc}
      full={page.type === 'openapi' ? true : page.data.full}
      tableOfContent={{ style: 'clerk' }}
    >
      <DocsTitle>{page.data.title}</DocsTitle>
      {page.type === 'docs' && (
        <DocsDescription className="mb-0">{page.data.description}</DocsDescription>
      )}
      <div className="flex flex-row gap-2 items-center border-b pb-6">
        <MarkdownCopyButton markdownUrl={markdownUrl} />
        <ViewOptionsPopover
          markdownUrl={markdownUrl}
          githubUrl={`https://github.com/${gitConfig.user}/${gitConfig.repo}/blob/${gitConfig.branch}/${gitConfig.contentPath}/${contentPath}`}
        />
      </div>
      <DocsBody>{content}</DocsBody>
    </DocsPage>
  );
}

export async function generateStaticParams() {
  return source.generateParams();
}

export async function generateMetadata(props: PageProps<'/[[...slug]]'>): Promise<Metadata> {
  const params = await props.params;
  if (!params.slug?.length) redirect('/core');
  const page = source.getPage(params.slug);
  if (!page) notFound();

  return {
    title: page.data.title,
    description: page.data.description,
    openGraph: {
      images: getPageImageUrl(page).url,
    },
  };
}
