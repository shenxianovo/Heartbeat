import { asMarkdown, md } from 'fumadocs-core/server';
import type { ReactNode } from 'react';
import { DocumentationGraph } from './documentation-graph';
import { ModelRelations } from './model-relations';
import { ObserverStructure } from './observer-structure';

// Markdown forms of the Fumadocs UI components used in pages. Pages render the originals from `./mdx`.

function Callout({ title, children }: { title?: ReactNode; children?: ReactNode }) {
  asMarkdown();
  return title ? md.linePrefix('> ')`**${title}**\n\n${children}` : md.linePrefix('> ')`${children}`;
}

async function Cards({ children }: { children?: ReactNode }) {
  asMarkdown();
  // Each card is one list item, the blank lines between them would loosen the list.
  return `${(await md`${children}`).trim().replace(/\n{2,}/g, '\n')}\n\n`;
}

async function Card({ title, href, children }: { title: ReactNode; href?: string; children?: ReactNode }) {
  asMarkdown();
  const name = href ? md`[${title}](${href})` : md`**${title}**`;
  const body = (await md`${children}`).trim();
  return md`- ${name}${body && `：${body}`}\n`;
}

/**
 * Components for the exported Markdown of a page. Components left out, such as the test reports, stay as JSX.
 */
export function getMarkdownComponents() {
  return { Callout, Cards, Card, DocumentationGraph, ModelRelations, ObserverStructure };
}
