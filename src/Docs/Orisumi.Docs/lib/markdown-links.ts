/** The Markdown address of a page URL, keeping its anchor: `/core/model/version#members` → `/core/model/version.md#members`. */
export function markdownUrl(url: string): string {
  const [path, anchor] = url.split('#');
  return `${path}.md${anchor ? `#${anchor}` : ''}`;
}

/**
 * Point the links of exported Markdown at the Markdown address of their pages. Authors link to source files such as
 * `../../adr/entity-model.mdx`, which only resolve in the repository. `resolve` returns the page URL of a link, or
 * undefined to keep the link as written.
 */
export function linkToMarkdown(markdown: string, resolve: (href: string) => string | undefined): string {
  return markdown.replace(/\]\(([^)\s]+)\)/g, (link, href: string) => {
    if (/^([a-z]+:|#)/i.test(href)) return link;
    const url = resolve(href);
    return url ? `](${markdownUrl(url)})` : link;
  });
}
