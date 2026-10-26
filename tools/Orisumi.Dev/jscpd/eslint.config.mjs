import parser from '@typescript-eslint/parser';

export default [{
  files: ['**/*.{js,jsx,mjs,cjs,ts,tsx,mts,cts}'],
  languageOptions: { parser, parserOptions: { ecmaFeatures: { jsx: true } } },
  // Only complexity is measured. Inline directives name rules from each project's own config.
  linterOptions: { noInlineConfig: true, reportUnusedDisableDirectives: 'off' },
  rules: { complexity: ['warn', 0] },
}];
