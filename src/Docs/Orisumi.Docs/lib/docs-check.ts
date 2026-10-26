/** Pure checks behind `pnpm docs:check`. File access lives in scripts/check-docs.ts. */

export type Finding = { file: string; line: number; check: string; message: string };

export type Rule = { id: string; file: string; line: number };

/** Heading anchors follow github-slugger, which fumadocs and GitHub both use. */
export function slugify(heading: string): string {
  return heading.trim().toLowerCase().replace(/[^\p{L}\p{M}\p{N}\p{Pc}\- ]/gu, '').replace(/ /g, '-');
}

export function lineAt(text: string, index: number): number {
  let line = 1;
  for (let i = 0; i < index; i++) if (text.charCodeAt(i) === 10) line++;
  return line;
}

/** Blank out fenced code blocks and inline code, keeping offsets and line numbers intact. */
export function maskCode(text: string): string {
  const blank = (match: string) => match.replace(/[^\n]/g, ' ');
  return text.replace(/^```[^\n]*\n[\s\S]*?^```[ \t]*$/gm, blank).replace(/`[^`\n]*`/g, blank);
}

/** Every anchor a page offers: explicit `[#id]` markers and slugs of the remaining headings. */
export function anchorsOf(text: string): Set<string> {
  const anchors = new Set<string>();
  for (const match of maskCode(text).matchAll(/^#{1,6}[ \t]+(.+?)[ \t]*$/gm)) {
    const explicit = match[1].match(/\[#([^\]]+)\]$/);
    anchors.add(explicit ? explicit[1] : slugify(match[1]));
  }
  return anchors;
}

export type Link = { target: string; text: string; index: number };

export function linksOf(text: string): Link[] {
  const masked = maskCode(text);
  return [...masked.matchAll(/\[([^\]\n]*)\]\(([^)\s]+)\)/g)].map((match) => ({
    text: text.slice(match.index! + 1, match.index! + 1 + match[1].length),
    target: match[2],
    index: match.index!,
  }));
}

/** The prefix table on the Core Conventions page maps each rule prefix to its page. */
export function rulePrefixes(coreIndex: string): Map<string, string> {
  const prefixes = new Map<string, string>();
  for (const match of coreIndex.matchAll(/^\| `([A-Z]+)` \| \[[^\]]+\]\(\.\/([^)#]+)\) \|$/gm)) {
    prefixes.set(match[1], match[2]);
  }
  return prefixes;
}

const ruleActors = ['后端', '写入方', '读取方'];

/** The first paragraph under a rule heading is `定义` or `执行方：` followed by actors joined with `、`. */
export function ruleActorProblem(paragraph: string | undefined): string | null {
  if (paragraph === '定义') return null;
  const actors = paragraph?.match(/^执行方：(.+)$/)?.[1].split('、');
  if (!actors) return 'the first paragraph must be 定义 or 执行方：…';
  const unknown = actors.filter((actor) => !ruleActors.includes(actor));
  if (unknown.length) return `unknown actor ${unknown.join('、')}; use ${ruleActors.join('、')}`;
  if (new Set(actors).size !== actors.length) return 'an actor is listed twice';
  return null;
}

export function ruleHeadings(file: string, text: string, prefix: string): { rules: Rule[]; findings: Finding[] } {
  const rules: Rule[] = [];
  const findings: Finding[] = [];
  let previous = 0;
  for (const match of maskCode(text).matchAll(/^### ([A-Z]+)-(\d+) (.+)$/gm)) {
    const line = lineAt(text, match.index!);
    const id = `${match[1]}-${match[2]}`;
    const report = (message: string) => findings.push({ file, line, check: 'rule-heading', message });
    if (match[1] !== prefix) report(`${id} does not belong to this page, whose prefix is ${prefix}`);
    if (!match[3].endsWith(`[#${id.toLowerCase()}]`)) report(`${id} must end with the anchor [#${id.toLowerCase()}]`);
    const number = Number(match[2]);
    if (number <= previous) report(`${id} must come after ${prefix}-${previous}; numbers are unique and ascending`);
    previous = Math.max(previous, number);
    const paragraph = text.slice(match.index! + match[0].length).split('\n').map((row) => row.trim()).find(Boolean);
    const actorProblem = ruleActorProblem(paragraph?.startsWith('#') ? undefined : paragraph);
    if (actorProblem) findings.push({ file, line, check: 'rule-actor', message: `${id}: ${actorProblem}` });
    rules.push({ id, file, line });
  }
  return { rules, findings };
}

/** Deprecated terms are the inline-code spans in the first column of the Glossary table. */
export function deprecatedTerms(glossary: string): { terms: string[]; section: [number, number] } {
  const start = glossary.indexOf('## 不再使用的术语');
  if (start < 0) return { terms: [], section: [0, 0] };
  const next = glossary.indexOf('\n## ', start + 1);
  const end = next < 0 ? glossary.length : next;
  const terms = [...glossary.slice(start, end).matchAll(/^\| ([^|]+) \|/gm)]
    .flatMap((row) => [...row[1].matchAll(/`([^`]+)`/g)].map((code) => code[1]));
  return { terms, section: [start, end] };
}

/** ASCII terms match whole words, so `OBS-` does not match inside `OBSERVATION-`. */
export function findTerm(text: string, term: string): number[] {
  const escaped = term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const before = /^[A-Za-z0-9]/.test(term) ? '(?<![A-Za-z0-9_])' : '';
  const after = /[A-Za-z0-9]$/.test(term) ? '(?![A-Za-z0-9_])' : '';
  return [...text.matchAll(new RegExp(before + escaped + after, 'g'))].map((match) => match.index!);
}

/** C# sources under src/ and tests/ cite Core rules by number, so their references must exist too. */
export function citesRules(file: string): boolean {
  return /^(src|tests)\//.test(file) && file.endsWith('.cs');
}

export function ruleReferences(text: string, prefixes: Set<string>): { id: string; index: number }[] {
  return [...text.matchAll(/(?<![A-Za-z])([A-Z]+)-(\d+)(?![\d])/g)]
    .filter((match) => prefixes.has(match[1]))
    .map((match) => ({ id: `${match[1]}-${match[2]}`, index: match.index! }));
}

const adrSections = ['背景', '决定', '理由', '代价', '规则位置'];

export function adrStructure(file: string, text: string): Finding[] {
  const headings = [...maskCode(text).matchAll(/^## (.+?)(?: \[#[^\]]+\])?[ \t]*$/gm)].map((match) => match[1]);
  const expected = headings.includes('规则') ? ['背景', '决定', '规则', '理由', '代价', '规则位置'] : adrSections;
  if (headings.join('|') === expected.join('|')) return [];
  return [{ file, line: 1, check: 'adr-structure', message: `sections are ${headings.join('、')}; expected ${expected.join('、')}` }];
}

export function frontmatter(text: string): Record<string, string> | null {
  const match = text.match(/^---\n([\s\S]*?)\n---\n/);
  if (!match) return null;
  return Object.fromEntries([...match[1].matchAll(/^(\w+):\s*"?(.*?)"?\s*$/gm)].map((field) => [field[1], field[2]]));
}

export function jsonBlocks(file: string, text: string): Finding[] {
  const findings: Finding[] = [];
  for (const match of text.matchAll(/^```json(?:[ \t][^\n]*)?\n([\s\S]*?)^```[ \t]*$/gm)) {
    try {
      JSON.parse(match[1]);
    } catch (error) {
      findings.push({ file, line: lineAt(text, match.index!), check: 'json', message: (error as Error).message });
    }
  }
  return findings;
}
