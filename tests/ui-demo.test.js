const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '..', 'FlowKey-UI-Demo.html'), 'utf8');
const scripts = [...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)].map(x => x[1]);
const model = vm.runInNewContext(scripts[0] + ';DemoModel');

test('demo is standalone Traditional Chinese with three views and valid JavaScript', () => {
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
