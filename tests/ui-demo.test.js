const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '..', 'FlowKey-UI-Demo.html'), 'utf8');
const scripts = [...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)].map(x => x[1]);
const model = vm.runInNewContext(scripts[0] + ';DemoModel');

test('demo works offline in Traditional Chinese with three views and valid JavaScript', () => {
  assert.match(html, /lang="zh-Hant"/);
  assert.doesNotMatch(html, /(?:src|href)="https?:/);
  for (const name of ['record', 'execute', 'settings']) assert.ok(html.includes(`id="view-${name}"`));
  const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(x => x[1]);
  assert.equal(new Set(ids).size, ids.length);
  scripts.forEach(source => new vm.Script(source));
  assert.ok(html.indexOf('data-view="settings"') < html.indexOf('class="local-card"'));
});

test('stored demo data round trips and corrupt data falls back safely', () => {
  const initial = model.initial();
  assert.equal(JSON.stringify(model.load(JSON.stringify(initial))), JSON.stringify(initial));
  for (const raw of [null, '{', 'null', '{}']) assert.equal(model.load(raw).scripts.length, 3);
  for (const mutate of [v => v.scripts.push(v.scripts[0]), v => v.scripts[0].steps[0].delay = -1, v => v.scripts[0].steps[0].action = 'bad', v => v.settings.target = 'unknown', v => v.scripts[0].name = ' ']) {
    const value = model.initial(); mutate(value);
    assert.equal(JSON.stringify(model.load(JSON.stringify(value))), JSON.stringify(initial));
  }
});

test('TailAdmin demo overview retains live counters and responsive layout', () => {
  const overview = html.slice(html.indexOf('<div class="record-overview"'), html.indexOf('<div class="panel record-toolbar"'));
  assert.match(overview, /aria-label="錄製概覽"/);
  for (const id of ['record-count', 'record-duration']) {
    assert.ok(overview.includes(`id="${id}"`));
    assert.ok(html.includes(`$('${id}').textContent=`));
  }
  assert.match(html, /--accent:#465fff/);
  assert.match(html, /--nav:#fff/);
  assert.match(html, /@media\(max-width:620px\)/);
  assert.match(html, /\.record-overview\{grid-template-columns:1fr 1fr\}/);
  assert.match(html, /\.run-panel\{grid-template-columns:1fr\}/);
  const sample = model.sample();
  assert.equal(sample.length, 8);
  assert.equal((sample.reduce((sum, step) => sum + step.delay, 0) / 1000).toFixed(1), '2.5');
});

test('held keys produce one down and one up with delay between accepted events', () => {
  const held = new Set(), steps = []; let last = 0;
  for (const [key, up, now] of [['Q',false,100],['Q',false,150],['Alt',false,200],['Q',true,250],['Q',true,270],['Alt',true,400]]) last = model.keyEvent(held,steps,key,up,now,last);
  assert.equal(JSON.stringify(steps), JSON.stringify([{key:'Q',action:'down',delay:100},{key:'Alt',action:'down',delay:100},{key:'Q',action:'up',delay:50},{key:'Alt',action:'up',delay:150}]));
  assert.equal(held.size,0);
  model.keyEvent(held,steps,'Q',false,450,last);
  assert.equal(steps.length,5);
});

test('name and execution settings validate limits and failure cases', () => {
  assert.equal(model.name('  測試腳本  '),'測試腳本');
  for (const value of ['', ' ', '字'.repeat(101)]) assert.throws(() => model.name(value));
  for (const mode of ['once','count','continuous']) assert.equal(model.plan(mode,1,100).mode,mode);
  assert.equal(model.plan('count',10000,60000).count,10000);
  for (const args of [['bad',1,100],['count',0,100],['count',1.5,100],['count',10001,100],['once',1,99],['once',1,60001]]) assert.throws(() => model.plan(...args));
});

test('restored TailAdmin theme ignores previously saved Rem preference without losing scripts', () => {
  assert.doesNotMatch(html, /rem-theme|rem-character|rem-banner|雷姆|withTheme/);
  const state = model.initial(); state.settings.rem = true;
  state.scripts[0].name = '保留我的腳本';
  const restored = model.load(JSON.stringify(state));
  assert.equal(restored.settings.rem, undefined);
  assert.equal(restored.scripts[0].name, '保留我的腳本');
  assert.equal(restored.settings.target, state.settings.target);
});

test('demo preview serves offline assets with correct types and rejects unknown routes', async () => {
  const server = require('node:http').createServer(require('./preview-demo.cjs').handle);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const base = `http://127.0.0.1:${server.address().port}`;
    for (const [route, type] of [['/', 'text/html'], ['/branding.css', 'text/css'], ['/assets/flowkey.svg', 'image/svg+xml'], ['/assets/NotoSerifTC.ttf', 'font/ttf'], ['/favicon.ico', 'image/x-icon']]) {
      const response = await fetch(base + route);
      assert.equal(response.status, 200);
      assert.ok(response.headers.get('content-type').startsWith(type));
      assert.ok((await response.arrayBuffer()).byteLength > 0);
    }
    assert.equal((await fetch(base + '/missing.svg')).status, 404);
    assert.equal((await fetch(base + '/package.json')).status, 404);
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});
