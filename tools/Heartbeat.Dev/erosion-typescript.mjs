import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const root = path.resolve(process.argv[2]);
const web = path.join(root, "src", "Frontend", "Heartbeat.Web");
const require = createRequire(pathToFileURL(path.join(process.argv[3] ?? web, "package.json")));
const ts = require("typescript");

function files(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return ["test", "tests", "__tests__"].includes(entry.name) ? [] : files(target);
    if (!/\.(?:ts|tsx|js|jsx)$/.test(entry.name)) return [];
    if (/\.(?:test|spec)\.[^.]+$/.test(entry.name) || entry.name === "next-env.d.ts") return [];
    return [target];
  });
}

function isFunction(node) {
  return ts.isFunctionLike(node) && node.body;
}

function symbol(node, source) {
  const scopes = [];
  for (let scope = node; scope && !ts.isSourceFile(scope); scope = scope.parent) {
    if (scope.name) scopes.unshift(`${ts.SyntaxKind[scope.kind]}:${scope.name.getText(source)}`);
    else if (isFunction(scope)) {
      if (scope.parent.name) {
        scopes.unshift(ts.SyntaxKind[scope.kind]);
        continue;
      }
      let owner = scope.parent;
      while (owner && !isFunction(owner) && !ts.isSourceFile(owner)) owner = owner.parent;
      const siblings = [];
      function collect(candidate) {
        if (isFunction(candidate)) siblings.push(candidate);
        else ts.forEachChild(candidate, collect);
      }
      ts.forEachChild(owner, collect);
      scopes.unshift(`${ts.SyntaxKind[scope.kind]}#${siblings.indexOf(scope)}`);
    }
  }
  return scopes.join("/");
}

const metrics = [];
for (const file of files(path.join(web, "src"))) {
  const text = fs.readFileSync(file, "utf8");
  const kind = /\.[jt]sx$/.test(file) ? ts.ScriptKind.TSX : file.endsWith(".js") ? ts.ScriptKind.JS : ts.ScriptKind.TS;
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, kind);
  function addMetric(node, head, identity, kind = "function") {
    const start = source.getLineAndCharacterOfPosition(head.getStart(source));
    const end = source.getLineAndCharacterOfPosition(node.end);
    const lines = node.getText(source).split(/\r?\n/).filter((line) => line.trim() && !/^\s*(?:\/\/|\/\*|\*)/.test(line)).length;
    metrics.push({
      language: file.endsWith(".js") || file.endsWith(".jsx") ? "JavaScript" : "TypeScript",
      path: path.relative(root, file).split(path.sep).join("/"),
      line: start.line + 1,
      column: start.character + 1,
      endLine: end.line + 1,
      endColumn: end.character + 1,
      symbol: identity,
      complexity: 1,
      lines,
      kind,
    });
  }
  function visit(node) {
    if (isFunction(node)) {
      const head = ts.isPropertyAssignment(node.parent) || ts.isPropertyDeclaration(node.parent) ? node.parent : node;
      addMetric(node, head, symbol(node, source));
    }
    if (ts.isPropertyDeclaration(node) && node.initializer) {
      addMetric(node.initializer, node.initializer, `${symbol(node, source)}/initializer`, "initializer");
    }
    if (ts.isClassStaticBlockDeclaration(node)) {
      const ordinal = node.parent.members.filter(ts.isClassStaticBlockDeclaration).indexOf(node);
      addMetric(node, node, `${symbol(node, source)}/static-block#${ordinal}`, "static-block");
    }
    ts.forEachChild(node, visit);
  }
  visit(source);
}

process.stdout.write(JSON.stringify(metrics));
