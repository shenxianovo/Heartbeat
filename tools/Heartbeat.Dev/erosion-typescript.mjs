import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const root = path.resolve(process.argv[2]);
const web = path.join(root, "src", "Frontend", "Heartbeat.Web");
const require = createRequire(pathToFileURL(path.join(web, "package.json")));
const ts = require("typescript");

function files(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return entry.name === "tests" || entry.name === "__tests__" ? [] : files(target);
    if (!/\.(?:ts|tsx|js|jsx)$/.test(entry.name)) return [];
    if (/\.(?:test|spec)\.[^.]+$/.test(entry.name) || entry.name === "next-env.d.ts") return [];
    return [target];
  });
}

function isFunction(node) {
  return ts.isFunctionLike(node) && node.body;
}

function symbol(node, source) {
  if (node.name) return node.name.getText(source);
  const parent = node.parent;
  if ((ts.isVariableDeclaration(parent) || ts.isPropertyAssignment(parent) || ts.isPropertyDeclaration(parent)) && parent.name) {
    return parent.name.getText(source);
  }
  return `anonymous@${source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1}`;
}

const metrics = [];
for (const file of files(path.join(web, "src"))) {
  const text = fs.readFileSync(file, "utf8");
  const kind = /\.[jt]sx$/.test(file) ? ts.ScriptKind.TSX : file.endsWith(".js") ? ts.ScriptKind.JS : ts.ScriptKind.TS;
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, kind);
  function visit(node) {
    if (isFunction(node)) {
      const start = source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
      const lines = node.getText(source).split(/\r?\n/).filter((line) => line.trim() && !/^\s*(?:\/\/|\/\*|\*)/.test(line)).length;
      metrics.push({
        language: file.endsWith(".js") || file.endsWith(".jsx") ? "JavaScript" : "TypeScript",
        path: path.relative(root, file).split(path.sep).join("/"),
        line: start,
        symbol: symbol(node, source),
        complexity: 1,
        lines,
      });
    }
    ts.forEachChild(node, visit);
  }
  visit(source);
}

process.stdout.write(JSON.stringify(metrics));
