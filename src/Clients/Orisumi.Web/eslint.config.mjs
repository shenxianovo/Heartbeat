// @ts-check
import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import { globalIgnores } from "eslint/config";

export default tseslint.config(
  globalIgnores(["dist", "coverage", "playwright-report", "test-results", "src/routeTree.gen.ts"]),
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ["**/*.{ts,tsx}"],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: {
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // shadcn 组件惯例同时导出组件与 variants；路由文件同时导出 Route，均为项目既定模式。
      "react-refresh/only-export-components": "off",
    },
  },
  {
    files: ["**/*.config.{ts,mjs}"],
    languageOptions: {
      globals: globals.node,
    },
  },
);
