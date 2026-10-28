import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';

const source = await readFile(new URL('../public/analytics.js', import.meta.url), 'utf8');

function visit(origin: string) {
  const scripts: { async?: boolean; src?: string }[] = [];
  const window = { location: { origin }, dataLayer: [] as IArguments[], gtag: undefined };
  runInNewContext(source, {
    window,
    document: {
      createElement: () => ({}),
      head: { appendChild: (script: typeof scripts[number]) => scripts.push(script) },
    },
  });
  return { scripts, window };
}

test('local and preview visits do not load Google or queue analytics events', () => {
  for (const origin of ['http://localhost:3000', 'http://127.0.0.1:3000',
    'https://shenxianovo.github.io', 'https://orisumi.dev', 'https://docs.orisumi.dev.example.com']) {
    const { scripts, window } = visit(origin);
    assert.equal(scripts.length, 0, origin);
    assert.equal(window.dataLayer.length, 0, origin);
    assert.equal(window.gtag, undefined, origin);
  }
});

test('the public docs site initializes its GA4 stream without advertising signals', () => {
  const { scripts, window } = visit('https://docs.orisumi.dev');
  assert.equal(scripts.length, 1);
  assert.equal(scripts[0].src, 'https://www.googletagmanager.com/gtag/js?id=G-WKHX52M7JW');
  assert.equal(scripts[0].async, true);
  assert.equal(window.dataLayer[0][0], 'js');
  const [command, measurementId, config] = window.dataLayer[1];
  assert.equal(command, 'config');
  assert.equal(measurementId, 'G-WKHX52M7JW');
  assert.equal(config.allow_google_signals, false);
  assert.equal(config.allow_ad_personalization_signals, false);
});
