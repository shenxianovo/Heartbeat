// Checks that the project's documents are consistent: links, anchors, Core rule numbers and actors,
// deprecated terms, ADR sections, JSON examples and sidebar entries. Project documents are the
// docs site content, every README.md and AGENTS.md, and the agent skills under .agents/.
// C# sources under src/ and tests/ are checked only for references to Core rules that do not exist.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { basename, dirname, join, normalize, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  adrStructure, anchorsOf, citesRules, deprecatedTerms, findTerm, frontmatter, jsonBlocks, lineAt, linksOf,
  ruleHeadings, rulePrefixes, ruleReferences, type Finding, type Rule,
} from '../lib/docs-check.ts';

const root = execFileSync('git', ['rev-parse', '--show-toplevel'], {
  cwd: fileURLToPath(new URL('.', import.meta.url)), encoding: 'utf8',
}).trim();
const docs = 'src/Docs/Orisumi.Docs/content/docs';
const isProjectDocument = (file: string) => file.startsWith(`${docs}/`)
  || ['README.md', 'AGENTS.md'].includes(basename(file)) || file.startsWith('.agents/');

const tracked = execFileSync('git', ['-c', 'core.quotepath=off', 'ls-files', '-co', '--exclude-standard'], {
  cwd: root, encoding: 'utf8',
}).split('\n').filter((file) => file && !file.includes('node_modules/') && existsSync(join(root, file)));
const files = tracked.filter((file) => /\.mdx?$/.test(file) || file === `${docs}/api/openapi.json`)
  .filter(isProjectDocument);
const sources = tracked.filter(citesRules);

const texts = new Map(files.map((file) => [file, readFileSync(join(root, file), 'utf8')]));
const read = (file: string) => texts.get(file) ?? readFileSync(join(root, file), 'utf8');
const findings: Finding[] = [];
const anchorCache = new Map<string, Set<string>>();
const anchors = (file: string) => {
  if (!anchorCache.has(file)) anchorCache.set(file, anchorsOf(read(file)));
  return anchorCache.get(file)!;
};

/** Resolve a link to a repository file, or null when it leaves the repository's documents. */
function resolveLink(from: string, target: string): { file: string; anchor: string } | null | 'skip' {
  const [path, anchor = ''] = target.split('#');
  if (/^[a-z]+:/i.test(path) || path.startsWith('/api/') || path.startsWith('/llms') || path.startsWith('/og/')) return 'skip';
  if (!path) return { file: from, anchor };
  if (path.startsWith('/')) {
    for (const candidate of [`${docs}${path}.mdx`, `${docs}${path}/index.mdx`]) {
      if (existsSync(join(root, candidate))) return { file: candidate, anchor };
    }
    return null;
  }
  const file = normalize(join(dirname(from), decodeURIComponent(path)));
  return existsSync(join(root, file)) ? { file, anchor } : null;
}

// Core rules: headings define rules, the Conventions page's prefix table defines which page owns a prefix.
const prefixes = rulePrefixes(read(`${docs}/core/conventions.mdx`));
const rules = new Map<string, Rule>();
for (const [prefix, page] of prefixes) {
  const file = `${docs}/core/${page}`;
  const result = ruleHeadings(file, read(file), prefix);
  findings.push(...result.findings);
  for (const rule of result.rules) rules.set(rule.id, rule);
}

const glossaryFile = `${docs}/core/glossary.mdx`;
const deprecated = deprecatedTerms(read(glossaryFile));

for (const [file, text] of texts) {
  const at = (index: number) => lineAt(text, index);

  for (const link of linksOf(text)) {
    const resolved = resolveLink(file, link.target);
    if (resolved === 'skip') continue;
    if (!resolved) {
      findings.push({ file, line: at(link.index), check: 'link', message: `${link.target} does not exist` });
      continue;
    }
    const isDocument = /\.mdx?$/.test(resolved.file);
    if (resolved.anchor && isDocument && !anchors(resolved.file).has(decodeURIComponent(resolved.anchor))) {
      findings.push({ file, line: at(link.index), check: 'anchor', message: `${link.target} has no anchor #${resolved.anchor}` });
    }
    const rule = rules.get(link.text);
    if (rule && (resolved.file !== rule.file || resolved.anchor !== link.text.toLowerCase())) {
      findings.push({ file, line: at(link.index), check: 'rule-link',
        message: `[${link.text}] must link to ${relative(dirname(file), rule.file)}#${link.text.toLowerCase()}` });
    }
  }

  for (const reference of ruleReferences(text, new Set(prefixes.keys()))) {
    if (!rules.has(reference.id)) {
      findings.push({ file, line: at(reference.index), check: 'rule-reference', message: `${reference.id} does not exist` });
    }
  }

  for (const term of deprecated.terms) {
    for (const index of findTerm(text, term)) {
      if (file === glossaryFile && index >= deprecated.section[0] && index < deprecated.section[1]) continue;
      findings.push({ file, line: at(index), check: 'deprecated-term', message: `"${term}" is no longer used; see the Glossary` });
    }
  }

  if (file.endsWith('.md') || file.endsWith('.mdx')) findings.push(...jsonBlocks(file, text));

  if (file.startsWith(`${docs}/`) && file.endsWith('.mdx')) {
    const fields = frontmatter(text);
    for (const field of ['title', 'description']) {
      if (!fields?.[field]) findings.push({ file, line: 1, check: 'frontmatter', message: `missing ${field}` });
    }
    if (file.startsWith(`${docs}/adr/`) && basename(file) !== 'index.mdx') findings.push(...adrStructure(file, text));
  }
}

for (const file of sources) {
  const text = readFileSync(join(root, file), 'utf8');
  for (const reference of ruleReferences(text, new Set(prefixes.keys()))) {
    if (!rules.has(reference.id)) {
      findings.push({ file, line: lineAt(text, reference.index), check: 'rule-reference', message: `${reference.id} does not exist` });
    }
  }
}

// Sidebar: every meta.json entry exists and every page is reachable from some meta.json.
const metaDirs = new Set<string>();
const listed = new Set<string>();
const wildcard = new Set<string>();
function walk(directory: string): string[] {
  return readdirSync(join(root, directory)).flatMap((name) => {
    const path = `${directory}/${name}`;
    return statSync(join(root, path)).isDirectory() ? walk(path) : [path];
  });
}
const docFiles = walk(docs);
for (const metaFile of docFiles.filter((file) => basename(file) === 'meta.json')) {
  const directory = dirname(metaFile);
  metaDirs.add(directory);
  const pages: string[] = JSON.parse(read(metaFile)).pages ?? [];
  for (const entry of pages) {
    if (entry === '...' || entry === 'z...a') { wildcard.add(directory); continue; }
    if (/^---.*---$/.test(entry) || entry.startsWith('!') || entry.startsWith('[')) continue;
    const target = normalize(join(directory, entry));
    if (!existsSync(join(root, `${target}.mdx`)) && !existsSync(join(root, target))) {
      findings.push({ file: metaFile, line: 1, check: 'sidebar', message: `"${entry}" does not exist` });
    }
    listed.add(target);
  }
}
const folderIncluded = (directory: string): boolean => directory === docs || listed.has(directory)
  || wildcard.has(dirname(directory)) || (!metaDirs.has(dirname(directory)) && folderIncluded(dirname(directory)));
for (const file of docFiles.filter((path) => path.endsWith('.mdx'))) {
  const page = file.slice(0, -'.mdx'.length);
  const directory = dirname(file);
  const covered = listed.has(page) || wildcard.has(directory)
    || (basename(page) === 'index' && listed.has(directory))
    || (!metaDirs.has(directory) && folderIncluded(directory));
  if (!covered) findings.push({ file, line: 1, check: 'sidebar', message: 'no meta.json lists this page' });
}

for (const finding of findings) console.log(`${finding.file}:${finding.line}: [${finding.check}] ${finding.message}`);
const checked = `${files.length} documents and ${sources.length} C# sources`;
console.log(findings.length ? `\n${findings.length} problem(s) in ${checked}.` : `Checked ${checked}.`);
process.exitCode = findings.length ? 1 : 0;
