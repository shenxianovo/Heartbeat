import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const root = path.resolve(process.argv[2]);
const require = createRequire(pathToFileURL(path.join(import.meta.dirname, "jscpd", "package.json")));
const ts = require("typescript");
const files = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));

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
for (const relative of files) {
  const file = path.join(root, relative);
  const text = fs.readFileSync(file, "utf8");
  const kind = /\.[jt]sx$/.test(file) ? ts.ScriptKind.TSX : /\.(?:c|m)?js$/.test(file) ? ts.ScriptKind.JS : ts.ScriptKind.TS;
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, kind);
  function addMetric(node, head, identity, kind = "function") {
    const start = source.getLineAndCharacterOfPosition(head.getStart(source));
    const end = source.getLineAndCharacterOfPosition(node.end);
    const lines = node.getText(source).split(/\r?\n/).filter((line) => line.trim() && !/^\s*(?:\/\/|\/\*|\*)/.test(line)).length;
    metrics.push({
      language: /\.(?:[cm]?js|jsx)$/.test(file) ? "JavaScript" : "TypeScript",
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
