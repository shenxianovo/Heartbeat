import defaultMdxComponents from 'fumadocs-ui/mdx';
import type { MDXComponents } from 'mdx/types';
import { Mermaid } from './mermaid';
import { ModelRelations } from './model-relations';
import { ObserverStructure } from './observer-structure';
import { TestCaseResults } from './test-case-results';
import { IntegrationTestReport } from './integration-test-report';
import { DocumentationGraph } from './documentation-graph';

export function getMDXComponents(components?: MDXComponents) {
  return {
    ...defaultMdxComponents,
    Mermaid,
    ModelRelations,
    ObserverStructure,
    IntegrationTestReport,
    TestCaseResults,
    DocumentationGraph,
    ...components,
  } satisfies MDXComponents;
}

export const useMDXComponents = getMDXComponents;

declare global {
  type MDXProvidedComponents = ReturnType<typeof getMDXComponents>;
}
