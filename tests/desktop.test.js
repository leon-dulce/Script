const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function desktop() {
  class Element {
    constructor() {
      this.children = [];
      this.listeners = {};
      this.style = {};
      this.classes = new Set();
      this.classList = {
        add: value => this.classes.add(value),
        remove: value => this.classes.delete(value),
        toggle: (value, on) => on ? this.classes.add(value) : this.classes.delete(value)
      };
      this.textContent = '';
    }
    append(...children) { this.children.push(...children); }
    appendChild(child) { this.children.push(child); }
    replaceChildren(...children) { this.children = children; }
    setAttribute(name, value) { this[name] = value; }
    removeAttribute(name) { delete this[name]; }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    querySelector() { return this.childSpan || (this.childSpan = new Element()); }
    click() { this.listeners.click?.(); }
    change(value) { if (value !== undefined) this.value = value; this.listeners.change?.({ target: this }); }
  }
  const elements = new Map();
  const get = id => elements.get(id) || (elements.set(id, new Element()), elements.get(id));
  const selectors = new Map();
  const select = name => selectors.get(name) || (selectors.set(name, new Element()), selectors.get(name));
  let ready;
  let hostMessage;
  let confirmResult = true;
  const sent = [];
  const alerts = [];
  const document = {
    activeElement: null,
    getElementById: get,
    createElement: () => new Element(),
    querySelector: select,
    addEventListener: (name, callback) => { if (name === 'DOMContentLoaded') ready = callback; }
  };
  const window = {
    chrome: { webview: {
      postMessage: message => sent.push(message),
      addEventListener: (name, callback) => { if (name === 'message') hostMessage = callback; }
    } },
    setTimeout: () => 1,
    confirm: () => confirmResult,
    alert: message => alerts.push(message)
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'desktop.js'), 'utf8'), { document, window });
  ready();
  const state = (overrides = {}) => ({
    mode: 'ready', message: '已选择窗口', currentStep: -1, currentIteration: 0,
    waitingForNextRun: false, pendingRun: false, dirty: false, selectedId: '123',
    windows: [{ id: '123', title: '记事本', process: 'notepad' }],
    savedScripts: [
      { id: 'a', name: '测试脚本', stepCount: 1, mode: 'Once' },
      { id: 'b', name: '第二脚本', stepCount: 2, mode: 'Continuous' }
    ],
    script: { id: 'a', name: '测试脚本', hotkey: 'F10',
      execution: { mode: 'Once', repeatCount: 1, intervalMs: 1000 },
      steps: [{ type: 'Click', x: 2, y: 3, button: 'Left', delayMs: 100 }] },
    ...overrides
  });
  return { get, select, sent, alerts, state, push: value => hostMessage({ data: value }), setConfirm: value => { confirmResult = value; } };
}

test('desktop displays saved scripts and supports navigation, selection, creation, and deletion', () => {
  const { get, select, sent, state, push } = desktop();
  assert.equal(sent[0].action, 'refresh');
  assert.equal(get('sidebar-library').hidden, true);
  push(state());
  assert.equal(get('window-name').textContent, '记事本');
  assert.equal(select('.window-meta small').textContent, 'notepad · 已确认');
  assert.equal(get('step-count').textContent, '1 个步骤');
  assert.equal(get('saved-scripts').children.length, 2);
  assert.equal(get('saved-scripts').children[0].children[0].textContent, '测试脚本');
  get('nav-execution').click();
  assert.equal(get('execution-view').hidden, false);
  assert.equal(get('workspace-view').hidden, true);
  assert.equal(get('sidebar-library').hidden, false);
  get('saved-scripts').children[1].click();
  assert.equal(sent.at(-1).action, 'openScript');
  assert.equal(sent.at(-1).id, 'b');
  get('execution-window-select').change('123');
  assert.equal(sent.at(-1).action, 'select');
  assert.equal(sent.at(-1).value, '123');
  get('execution-refresh-windows').click();
  assert.equal(sent.at(-1).action, 'refresh');
  get('nav-editor').click();
  assert.equal(get('workspace-view').hidden, false);
  assert.equal(get('sidebar-library').hidden, true);
  get('saved-scripts').children[1].click();
  assert.equal(sent.at(-1).action, 'refresh');
  get('new-editor-script').click();
  assert.equal(sent.at(-1).action, 'newScript');
  get('nav-execution').click();
  get('new-script').click();
  assert.equal(sent.at(-1).action, 'newScript');
  get('nav-execution').click();
  get('delete-script').click();
  assert.equal(sent.at(-1).action, 'deleteScript');
  assert.equal(sent.at(-1).id, 'a');
});

test('desktop protects unsaved changes when switching scripts', () => {
  const ui = desktop();
  const { get, sent, state, push } = ui;
  push(state({ dirty: true }));
  get('nav-execution').click();
  ui.setConfirm(false);
  get('saved-scripts').children[1].click();
  get('new-script').click();
  assert.equal(sent.length, 1);
  ui.setConfirm(true);
  get('saved-scripts').children[1].click();
  assert.equal(sent.at(-1).action, 'openScript');
});

test('execution screen saves plans and exposes run progress', () => {
  const { get, sent, state, push } = desktop();
  push(state());
  get('nav-execution').click();
  assert.equal(get('mode-once').checked, true);
  assert.equal(get('execution-window-select').value, '123');
  assert.equal(get('execution-target').textContent.includes('已确认'), true);
  assert.equal(get('repeat-count').disabled, true);
  assert.equal(get('repeat-interval').disabled, true);
  get('mode-once').checked = false;
  get('mode-count').checked = true;
  get('repeat-count').change('3');
  assert.equal(sent.at(-1).action, 'execution');
  assert.equal(sent.at(-1).mode, 'Count');
  assert.equal(sent.at(-1).count, 3);
  push(state({ script: { ...state().script, execution: { mode: 'Count', repeatCount: 3, intervalMs: 500 } } }));
  assert.equal(get('repeat-count').disabled, false);
  assert.equal(get('repeat-interval').disabled, false);
  assert.match(get('plan-summary').textContent, /3 次/);
  get('mode-count').checked = false;
  get('mode-continuous').checked = true;
  get('mode-continuous').change();
  assert.equal(sent.at(-1).mode, 'Continuous');
  get('execution-hotkey').change('F11');
  assert.equal(sent.at(-1).action, 'hotkey');
  get('save-execution').click();
  assert.equal(sent.at(-1).action, 'save');
  push(state({ message: '已保存「测试脚本」到脚本库。' }));
  assert.equal(get('execution-view').hidden, false);
  assert.match(get('execution-message').textContent, /已保存/);
  get('execution-start').click();
  assert.equal(sent.at(-1).action, 'run');
  push(state({ mode: 'running', currentStep: -1, currentIteration: 2, waitingForNextRun: true,
    script: { ...state().script, execution: { mode: 'Count', repeatCount: 3, intervalMs: 500 } } }));
  assert.match(get('cycle-number').textContent, /2 \/ 3/);
  assert.equal(get('execution-progress-label').textContent, '等待下一轮');
  assert.equal(get('execution-start').textContent, '■ 停止执行');
  push(state({ pendingRun: true }));
  assert.equal(get('execution-view').hidden, false);
  assert.equal(get('execution-progress-label').textContent, '等待目标窗口');
  assert.equal(get('run-button').disabled, false);
  push(state({ selectedId: '', message: '请先选择目标窗口。' }));
  assert.equal(get('execution-start').disabled, false);
  assert.equal(get('execution-message').textContent, '请先选择目标窗口。');
  assert.match(get('execution-help').textContent, /上方选择/);
});

test('execution inputs reject out-of-range values before sending a plan', () => {
  const { get, sent, alerts, state, push } = desktop();
  push(state());
  get('mode-once').checked = false;
  get('mode-count').checked = true;
  get('repeat-count').change('10001');
  assert.equal(sent.length, 1);
  assert.equal(alerts.length, 1);
  get('repeat-count').value = '3';
  get('repeat-interval').change('99');
  assert.equal(sent.length, 1);
  assert.equal(alerts.length, 2);
});

test('desktop markup contains the execution workspace and omits the three-step guide', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', 'index.html'), 'utf8');
  assert.match(html, /id="saved-scripts"/);
  assert.match(html, /id="execution-view"/);
  assert.match(html, /id="execution-window-select"/);
  assert.match(html, /id="mode-continuous"/);
  assert.doesNotMatch(html, /只需三步/);
});
