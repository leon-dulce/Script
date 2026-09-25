const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('desktop UI waits for host state and sends explicit actions', () => {
  class Element {
    constructor() { this.children = []; this.listeners = {}; this.style = {}; this.dataset = {}; this.classList = { add() {}, remove() {} }; this.textContent = ''; }
    append(...children) { this.children.push(...children); }
    appendChild(child) { this.children.push(child); }
    replaceChildren(...children) { this.children = children; }
    setAttribute(name, value) { this[name] = value; }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    querySelector() { return this.childSpan || (this.childSpan = new Element()); }
    click() { this.listeners.click?.(); }
  }
  const elements = new Map();
  const get = id => elements.get(id) || (elements.set(id, new Element()), elements.get(id));
  const selectors = new Map();
  const select = name => selectors.get(name) || (selectors.set(name, new Element()), selectors.get(name));
  let ready;
  let hostMessage;
  const sent = [];
  const document = {
    activeElement: null,
    getElementById: get,
    createElement: () => new Element(),
    querySelector: select,
    querySelectorAll: () => [],
    addEventListener: (name, callback) => { if (name === 'DOMContentLoaded') ready = callback; }
  };
  const window = {
    chrome: { webview: {
      postMessage: message => sent.push(message),
      addEventListener: (name, callback) => { if (name === 'message') hostMessage = callback; }
    } },
    setTimeout: () => 1
  };
  const source = fs.readFileSync(path.join(__dirname, '..', 'desktop.js'), 'utf8');
  vm.runInNewContext(source, { document, window });
  ready();
  assert.deepEqual(Array.from(get('hotkey-select').children, option => option.value), ['F8', 'F9', 'F10', 'F11']);
  assert.equal(sent[0].action, 'refresh');

  hostMessage({ data: {
    mode: 'ready', message: '已选择窗口', currentStep: -1, selectedId: '123',
    windows: [{ id: '123', title: '记事本', process: 'notepad' }],
    script: { name: '测试脚本', hotkey: 'F10', steps: [{ type: 'Click', x: 2, y: 3, button: 'Left', delayMs: 100 }] }
  } });
  assert.equal(get('window-name').textContent, '记事本');
  assert.equal(select('.window-meta small').textContent, 'notepad · 已确认');
  assert.equal(get('script-name').value, '测试脚本');
  assert.equal(get('step-count').textContent, '1 个步骤');
  get('record-button').click();
  assert.equal(sent.at(-1).action, 'record');
  get('window-select').listeners.change({ target: { value: '123' } });
  assert.equal(sent.at(-1).action, 'select');
  assert.equal(sent.at(-1).value, '123');
  get('hotkey-select').listeners.change({ target: { value: 'F11' } });
  assert.equal(sent.at(-1).value, 'F11');

  hostMessage({ data: {
    mode: 'ready', message: '已确认目标窗口', currentStep: -1, selectedId: '123',
    windows: [{ id: '123', title: '记事本', process: 'notepad' }],
    script: { name: '测试脚本', hotkey: 'F10', steps: [] }
  } });
  assert.match(select('.help').textContent, /F10 开始录制/);
});
