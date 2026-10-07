import parser from '@typescript-eslint/parser';

export default [{
  files: ['**/*.{js,jsx,mjs,cjs,ts,tsx,mts,cts}'],
  languageOptions: { parser, parserOptions: { ecmaFeatures: { jsx: true } } },
  rules: { complexity: ['warn', 0] },
}];
