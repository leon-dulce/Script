const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '..', 'index.html'), 'utf8');
const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]));

function desktop() {
  const scrolled = [];
  class Element {
    constructor(tagName = 'div') {
      this.tagName = tagName;
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
    click() { if (!this.disabled) this.listeners.click?.(); }
    change(value) {
      if (value !== undefined) this.value = value;
      if (!this.disabled) this.listeners.change?.({ target: this });
    }
    scrollIntoView(options) { scrolled.push({ row: this, options }); }
    showModal() { this.open = true; this.showCount = (this.showCount || 0) + 1; }
    close() { this.open = false; }
    focus() { document.activeElement = this; this.listeners.focus?.(); }
    dispatch(name, fields = {}) {
      const event = { target: this, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...fields };
      this.listeners[name]?.(event);
      return event;
    }
  }
  const elements = new Map();
  const get = id => {
    assert.ok(ids.has(id), `Element #${id} exists in the real HTML`);
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const selectors = new Map();
  const select = name => selectors.get(name) || (selectors.set(name, new Element()), selectors.get(name));
  let ready;
  let hostMessage;
  let confirmResult = true;
  const sent = [];
  const alerts = [];
  const confirmations = [];
  const document = {
    activeElement: null,
    getElementById: get,
    createElement: tagName => new Element(tagName),
    querySelector: select,
    addEventListener: (name, callback) => { if (name === 'DOMContentLoaded') ready = callback; }
  };
  const window = {
    chrome: { webview: {
      postMessage: message => sent.push(message),
      addEventListener: (name, callback) => { if (name === 'message') hostMessage = callback; }
    } },
    setTimeout: () => 1,
    confirm: message => { confirmations.push(message); return confirmResult; },
    alert: message => alerts.push(message)
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'desktop.js'), 'utf8'), { document, window });
  ready();
  const state = (overrides = {}) => ({
    workspace: 'editor', mode: 'ready', message: '已选择窗口', currentStep: -1, currentIteration: 0,
    waitingForNextRun: false, pendingRun: false, pendingRecord: false, dirty: false, selectedId: '123',
    recordingTargetTitle: '记事本', namingRequired: false, namingError: '',
    windows: [{ id: '123', title: '记事本', process: 'notepad' }],
    savedScripts: [
      { id: 'a', name: '测试脚本', stepCount: 1, mode: 'Once' },
      { id: 'b', name: '第二脚本', stepCount: 2, mode: 'Continuous' }
    ],
    script: { id: 'a', name: '测试脚本', hotkey: 'F10', clientWidth: 800, clientHeight: 600,
      execution: { mode: 'Once', repeatCount: 1, intervalMs: 1000 },
      steps: [{ type: 'Click', x: 2, y: 3, button: 'Left', delayMs: 100 }] },
    ...overrides
  });
  return { get, select, sent, alerts, confirmations, scrolled, state,
    push: value => hostMessage({ data: value }), setConfirm: value => { confirmResult = value; } };
}

function descendants(element) {
  return element.children.flatMap(child => [child, ...descendants(child)]);
}

test('global recording starts without a target and has no target or add-text controls', () => {
  const { get, select, sent, state, push } = desktop();
  push(state({ selectedId: '', windows: [], script: { ...state().script, steps: [], clientWidth: 0, clientHeight: 0 } }));
  for (const id of ['recording-target-config', 'recording-window-card', 'add-text', 'run-button', 'recording-progress']) assert.equal(get(id).hidden, true);
  assert.equal(get('record-button').hidden, false);
  assert.equal(get('record-button').disabled, false);
  assert.match(select('.help').textContent, /切换窗口会继续记录/);
  assert.match(descendants(get('steps')).map(e => e.textContent).join(' '), /可切换到其他窗口/);
  assert.doesNotMatch(descendants(get('steps')).map(e => e.textContent).join(' '), /先设置窗口名称/);
  get('hotkey-select').change('F9');
  assert.equal(sent.at(-1).action, 'hotkey');
  get('record-button').click();
  assert.equal(sent.at(-1).action, 'record');
});

test('recording button stops a live recording but is blocked during naming and execution', () => {
  const { get, sent, state, push } = desktop();
  push(state({ mode: 'recording', selectedId: '', windows: [] }));
  assert.equal(get('record-button').disabled, false);
  assert.equal(get('record-button').querySelector('span').textContent, '停止录制');
  get('record-button').click();
  assert.equal(sent.at(-1).action, 'record');
  for (const update of [{ namingRequired: true }, { workspace: 'execution', mode: 'running' }]) {
    push(state(update));
    const before = sent.length;
    get('record-button').click();
    assert.equal(sent.length, before);
  }
});

test('an empty unsaved recording cannot add text even after its target dimensions were captured', () => {
  const { get, sent, state, push } = desktop();
  push(state({ script: { ...state().script, id: 'empty-recording', name: '', steps: [] } }));
  assert.equal(get('add-text').disabled, true);
  const before = sent.length;
  get('add-text').click();
  assert.equal(sent.length, before);
  push(state());
  assert.equal(get('add-text').disabled, true, 'saved scripts cannot add text');
});

test('recording open-script and ready progress stay absent after updates and navigation', () => {
  const { get, state, push } = desktop();
  for (const update of [{}, { pendingRecord: true }, { mode: 'recording' }, { mode: 'paused' },
    { workspace: 'execution', mode: 'running', currentStep: 0 },
    { workspace: 'editor', message: '保存失败：请重试。', dirty: true }]) {
    push(state(update));
    assert.equal(get('run-button').hidden, true);
    assert.equal(get('recording-progress').hidden, true);
    assert.equal(get('record-button').hidden, false);
    assert.equal(get('finish-button').hidden, true);
  }
  push(state({ workspace: 'execution', mode: 'running', currentStep: 0 }));
  assert.equal(get('execution-progress-label').textContent, '执行中');
  assert.equal(get('execution-progress-fraction').textContent, '1 / 1');
});

test('all saved scripts appear only on the execution page and host confirms workspace navigation', () => {
  const { get, sent, state, push } = desktop();
  const savedScripts = Array.from({ length: 40 }, (_, index) => ({
    id: `script-${index}`, name: `脚本 ${index + 1}`, stepCount: index + 1, mode: 'Once'
  }));
  push(state({ savedScripts }));
  assert.equal(get('saved-scripts').children.length, 40);
  assert.equal(get('library-count').textContent, '40');
  assert.equal(get('sidebar-library').hidden, true);
  get('saved-scripts').children[39].click();
  assert.equal(sent.length, 1);
  get('nav-execution').click();
  assert.equal(sent.at(-1).action, 'workspace');
  assert.equal(sent.at(-1).value, 'execution');
  assert.equal(get('execution-view').hidden, false);
  push(state({ workspace: 'execution', savedScripts }));
  assert.equal(get('workspace-view').hidden, true);
  assert.equal(get('sidebar-library').hidden, false);
  assert.equal(get('saved-scripts').children[39].children[0].textContent, '脚本 40');
  get('saved-scripts').children[39].click();
  assert.equal(sent.at(-1).action, 'openScript');
  assert.equal(sent.at(-1).id, 'script-39');
  get('nav-editor').click();
  assert.equal(sent.at(-1).value, 'editor');
  assert.equal(get('sidebar-library').hidden, true);
  push(state({ workspace: 'execution', savedScripts }));
  assert.equal(get('execution-view').hidden, false, 'render follows host workspace even after an optimistic navigation');
});

test('navigation and script switching are blocked during recording, playback, or pending work', () => {
  for (const busyState of [{ mode: 'recording' }, { mode: 'paused' }, { mode: 'running' },
    { pendingRecord: true }, { pendingRun: true }, { namingRequired: true }]) {
    const { get, sent, state, push } = desktop();
    push(state(busyState));
    assert.equal(get('nav-execution').disabled, true);
    assert.equal(get('nav-editor').disabled, true);
    get('nav-execution').click();
    get('run-button').click();
    get('saved-scripts').children[1].click();
    assert.equal(sent.length, 1);
    assert.equal(get('execution-view').hidden, true);
  }
});

test('ending a recording opens a naming dialog and confirmation saves then opens execution', () => {
  const { get, sent, state, push } = desktop();
  const pendingName = { namingRequired: true, script: { ...state().script, id: 'new', name: '' } };
  push(state(pendingName));
  assert.equal(get('recording-name-dialog').open, true);
  assert.equal(get('recording-name-input').value, '');
  assert.match(get('status-text').textContent, /等待命名保存/);
  assert.equal(get('add-text').disabled, true);
  assert.equal(sent.some(message => ['save', 'saveRecording'].includes(message.action)), false);
  get('recording-name-input').value = '  每日报告  ';
  push(state(pendingName));
  assert.equal(get('recording-name-input').value, '  每日报告  ', 'host updates retain typed name');
  assert.equal(get('recording-name-dialog').showCount, 1);
  get('recording-name-confirm').click();
  assert.equal(sent.at(-1).action, 'saveRecording');
  assert.equal(sent.at(-1).name, '每日报告');
  assert.equal(get('recording-name-dialog').open, true, 'dialog waits for successful host response');
  push(state({ workspace: 'execution', script: { ...state().script, name: '每日报告' }, message: '已保存「每日报告」。' }));
  assert.equal(get('recording-name-dialog').open, false);
  assert.equal(get('execution-view').hidden, false);
  assert.equal(get('sidebar-library').hidden, false);
  assert.equal(get('execution-script-name').textContent, '每日报告');
});

test('naming rejects blank and overly long names, retains errors and retries after save failures', () => {
  const { get, sent, state, push } = desktop();
  const pendingName = { namingRequired: true, script: { ...state().script, name: '' } };
  push(state(pendingName));
  get('recording-name-input').value = '   ';
  get('recording-name-confirm').click();
  assert.equal(sent.length, 1);
  assert.match(get('recording-name-error').textContent, /请输入/);
  get('recording-name-input').value = '名'.repeat(101);
  get('recording-name-confirm').click();
  assert.equal(sent.length, 1);
  assert.match(get('recording-name-error').textContent, /100/);
  get('recording-name-input').value = '名'.repeat(100);
  get('recording-name-input').dispatch('keydown', { key: 'Enter' });
  assert.equal(sent.at(-1).action, 'saveRecording');
  assert.equal(sent.at(-1).name.length, 100);
  push(state({ ...pendingName, namingError: '保存失败：磁碟已满，请释放空间后重试。' }));
  assert.equal(get('recording-name-dialog').open, true);
  assert.equal(get('recording-name-input').value.length, 100);
  assert.match(get('recording-name-error').textContent, /磁碟已满/);
  get('recording-name-input').value = '重新保存';
  get('recording-name-confirm').click();
  assert.equal(sent.at(-1).name, '重新保存');
});

test('Escape cannot silently discard naming, explicit discard requires confirmation', () => {
  const ui = desktop();
  const { get, sent, state, push } = ui;
  push(state({ namingRequired: true }));
  assert.equal(get('recording-name-dialog').dispatch('cancel').defaultPrevented, true);
  assert.equal(get('recording-name-dialog').open, true);
  assert.equal(sent.length, 1);
  ui.setConfirm(false);
  get('recording-name-discard').click();
  assert.equal(sent.length, 1);
  assert.equal(get('recording-name-dialog').open, true);
  ui.setConfirm(true);
  get('recording-name-discard').click();
  assert.equal(sent.at(-1).action, 'discardRecording');
  push(state());
  assert.equal(get('recording-name-dialog').open, false);
  const count = sent.length;
  get('recording-name-confirm').click();
  get('recording-name-discard').click();
  assert.equal(sent.length, count, 'closed dialog cannot save or discard another script');
});

test('recording shows each key down and up with its precise interval and follows new rows live', () => {
  const { get, scrolled, state, push } = desktop();
  const steps = [
    { type: 'Key', keys: [162], keyAction: 'Down', delayMs: 7 },
    { type: 'Key', keys: [65], keyAction: 'Down', delayMs: 123 },
    { type: 'Key', keys: [65], keyAction: 'Down', delayMs: 350 },
    { type: 'Key', keys: [65], keyAction: 'Up', delayMs: 13 },
    { type: 'Key', keys: [162], keyAction: 'Up', delayMs: 123456 }
  ];
  push(state({ mode: 'recording', script: { ...state().script, steps: [] } }));
  assert.equal(scrolled.length, 0);
  for (let count = 1; count <= steps.length; count++) {
    push(state({ mode: 'recording', script: { ...state().script, steps: steps.slice(0, count) } }));
    assert.equal(get('steps').children.length, count);
    assert.equal(get('step-count').textContent, `${count} 个步骤`);
    assert.equal(scrolled.length, count);
    assert.equal(scrolled.at(-1).row, get('steps').children[count - 1]);
  }
  const rows = get('steps').children;
  assert.equal(rows[0].children[2].children[0].textContent, '按下按键');
  assert.equal(rows[0].children[2].children[1].textContent, '左 Ctrl');
  assert.equal(rows[3].children[2].children[0].textContent, '松开按键');
  assert.equal(rows[3].children[2].children[1].textContent, 'A');
  for (let index = 0; index < rows.length; index++) {
    assert.equal(rows[index].children[3].children[0].textContent, `间隔 ${steps[index].delayMs} 毫秒`);
    assert.equal(rows[index].children[3].children[1].hidden, true);
  }
  push(state({ mode: 'recording', script: { ...state().script, steps } }));
  assert.equal(scrolled.length, steps.length, 'unrelated updates do not repeatedly scroll live recording');
  push(state({ mode: 'paused', script: { ...state().script, steps } }));
  assert.equal(scrolled.length, steps.length);
  assert.equal(get('finish-button').hidden, true);
});

test('key labels cover legacy combinations, modifiers, function keys, number pad and punctuation', () => {
  const { get, state, push } = desktop();
  const keyCases = [
    [[17, 83], undefined, '按键', 'Ctrl + S'],
    [[16, 9], 'Press', '按键', 'Shift + Tab'],
    [[161], 'Down', '按下按键', '右 Shift'],
    [[165], 'Up', '松开按键', '右 Alt'],
    [[112], 'Down', '按下按键', 'F1'],
    [[135], 'Up', '松开按键', 'F24'],
    [[96], 'Down', '按下按键', 'Numpad 0'],
    [[105], 'Up', '松开按键', 'Numpad 9'],
    [[107], 'Down', '按下按键', 'Numpad +'],
    [[48], 'Down', '按下按键', '0'],
    [[32], 'Up', '松开按键', 'Space'],
    [[186], 'Down', '按下按键', '; / :'],
    [[220], 'Down', '按下按键', '\\ / |'],
    [[255], 'Press', '按键', '按键 0xFF']
  ];
  const steps = keyCases.map(([keys, keyAction]) => ({ type: 'Key', keys, keyAction, delayMs: 0 }));
  push(state({ script: { ...state().script, steps } }));
  get('steps').children.forEach((row, index) => {
    assert.equal(row.children[2].children[0].textContent, keyCases[index][2]);
    assert.equal(row.children[2].children[1].textContent, keyCases[index][3]);
  });
});

test('step interval editing accepts full recorded range and rejects invalid values without host mutations', () => {
  const { get, sent, alerts, state, push } = desktop();
  push(state());
  const interval = get('steps').children[0].children[3].children[1];
  assert.equal(interval.max, '2147483647');
  assert.equal(interval.step, '1');
  for (const value of ['-1', '2147483648', '1.5', '', 'NaN']) interval.change(value);
  assert.equal(sent.length, 1);
  assert.equal(alerts.length, 5);
  assert.equal(interval.value, '100');
  for (const value of ['0', '60001', '2147483647']) {
    interval.change(value);
    assert.equal(sent.at(-1).action, 'delay');
    assert.equal(sent.at(-1).value, Number(value));
  }
  push(state({ script: { ...state().script, steps: [{ ...state().script.steps[0], delayMs: 2147483647 }] } }));
  assert.equal(get('steps').children[0].children[3].children[0].textContent, '间隔 2147483647 毫秒');
});

test('execution mode and hotkey changes apply directly without save or editor controls', () => {
  const { get, sent, state, push } = desktop();
  push(state({ workspace: 'execution', dirty: true }));
  assert.equal(get('execution-script-name').textContent, '测试脚本');
  assert.equal(get('repeat-count').disabled, true);
  assert.equal(get('repeat-interval').disabled, true);
  get('mode-once').checked = false;
  get('mode-count').checked = true;
  get('mode-count').change();
  assert.equal(sent.at(-1).action, 'execution');
  assert.equal(sent.at(-1).mode, 'Count');
  push(state({ workspace: 'execution', script: { ...state().script, execution: { mode: 'Count', repeatCount: 3, intervalMs: 500 } } }));
  assert.equal(get('repeat-count').disabled, false);
  assert.equal(get('repeat-interval').disabled, false);
  get('repeat-count').change('4');
  assert.equal(sent.at(-1).count, 4);
  get('repeat-interval').change('800');
  assert.equal(sent.at(-1).intervalMs, 800);
  get('mode-count').checked = false;
  get('mode-continuous').checked = true;
  get('mode-continuous').change();
  assert.equal(sent.at(-1).mode, 'Continuous');
  get('execution-hotkey').change('F11');
  assert.equal(sent.at(-1).action, 'hotkey');
  assert.equal(sent.at(-1).value, 'F11');
  push(state({ workspace: 'execution', message: '执行设置已自动保存。' }));
  assert.equal(get('execution-view').hidden, false);
  assert.match(get('execution-message').textContent, /自动保存/);
  assert.equal(sent.some(message => message.action === 'save'), false);
});

test('execution input validation covers limits and rejects invalid count or interval', () => {
  const { get, sent, alerts, state, push } = desktop();
  push(state({ workspace: 'execution', script: { ...state().script, execution: { mode: 'Count', repeatCount: 1, intervalMs: 100 } } }));
  for (const value of ['0', '10001', '1.5', 'NaN']) get('repeat-count').change(value);
  assert.equal(sent.length, 1);
  assert.equal(alerts.length, 4);
  get('repeat-count').value = '1';
  for (const value of ['99', '60001', '100.5', '']) get('repeat-interval').change(value);
  assert.equal(sent.length, 1);
  assert.equal(alerts.length, 8);
  get('repeat-interval').change('100');
  assert.equal(sent.at(-1).count, 1);
  assert.equal(sent.at(-1).intervalMs, 100);
  get('repeat-count').value = '10000';
  get('repeat-interval').change('60000');
  assert.equal(sent.at(-1).count, 10000);
  assert.equal(sent.at(-1).intervalMs, 60000);
  push(state({ workspace: 'execution', mode: 'running' }));
  const sentBefore = sent.length;
  get('mode-count').change();
  get('execution-hotkey').change('F8');
  assert.equal(sent.length, sentBefore);
});

test('execution displays every readonly step, highlights the current action, and follows new rounds', () => {
  const { get, scrolled, state, push } = desktop();
  const steps = [
    { type: 'Click', x: 2, y: 3, button: 'Left', delayMs: 100 },
    { type: 'Key', keys: [17, 83], delayMs: 0 },
    { type: 'Text', text: '<script>text stays text</script>', delayMs: 400 },
    { type: 'Scroll', wheelDelta: -2, delayMs: 300 },
    { type: 'DoubleClick', x: 8, y: 9, button: 'Left', delayMs: 200 }
  ];
  const running = { workspace: 'execution', mode: 'running', currentStep: 2, currentIteration: 2,
    script: { ...state().script, steps, execution: { mode: 'Count', repeatCount: 3, intervalMs: 500 } } };
  push(state(running));
  const rows = get('execution-steps').children;
  assert.equal(rows.length, 5);
  assert.equal(get('execution-step-count').textContent, '5 个步骤');
  assert.equal(descendants(get('execution-steps')).some(item => ['input', 'button', 'select'].includes(item.tagName)), false);
  assert.equal(rows.filter(row => row.className.includes('current')).length, 1);
  assert.equal(rows[2]['aria-current'], 'step');
  assert.equal(rows[2].children[2].children[1].textContent, '<script>text stays text</script>');
  assert.equal(rows[1].children[3].textContent, '间隔 0 毫秒');
  assert.match(get('execution-current-action').textContent, /第 3 步 · 输入文字/);
  assert.equal(get('execution-progress-fraction').textContent, '3 / 5');
  assert.equal(get('execution-progress-fill').style.width, '60%');
  assert.equal(scrolled.length, 1);
  assert.equal(scrolled[0].row, rows[2]);
  assert.equal(scrolled[0].options.block, 'nearest');
  push(state(running));
  assert.equal(scrolled.length, 1, 'unrelated updates do not repeatedly scroll the steps');
  push(state({ ...running, currentStep: -1, waitingForNextRun: true }));
  assert.equal(get('execution-steps').children.some(row => row.className.includes('current')), false);
  assert.match(get('execution-current-action').textContent, /第 2 轮已完成，等待 500 毫秒/);
  assert.equal(get('execution-progress-label').textContent, '等待下一轮');
  assert.equal(get('cycle-number').textContent, '2 / 3 · 等待下一轮');
  push(state({ ...running, currentIteration: 3, currentStep: 0 }));
  assert.equal(scrolled.length, 2);
  assert.equal(get('execution-steps').children[0]['aria-current'], 'step');
  push(state({ workspace: 'execution', script: running.script }));
  assert.equal(get('execution-steps').children.some(row => row.className.includes('current')), false);
  assert.match(get('execution-current-action').textContent, /等待执行/);
});

test('execution can be requested without manual target selection and displays pending or failure feedback', () => {
  const { get, sent, state, push } = desktop();
  push(state({ workspace: 'execution', selectedId: '' }));
  assert.equal(get('execution-start').disabled, false);
  assert.match(get('execution-help').textContent, /目标窗口按 F10/);
  get('execution-start').click();
  assert.equal(sent.at(-1).action, 'run');
  push(state({ workspace: 'execution', selectedId: '', pendingRun: true, message: '切回目标窗口后开始。' }));
  assert.equal(get('execution-start').textContent, '■ 停止执行');
  assert.equal(get('execution-progress-label').textContent, '等待目标窗口');
  assert.equal(get('execution-current-action').textContent, '等待目标窗口进入前台');
  assert.match(get('execution-help').textContent, /再次点击可取消等待/);
  assert.doesNotMatch(get('execution-help').textContent, /按快捷键可取消/);
  get('execution-start').click();
  assert.equal(sent.at(-1).action, 'run');
  push(state({ workspace: 'execution', selectedId: '', message: '窗口尺寸与录制时不同，无法执行。' }));
  assert.equal(get('execution-view').hidden, false);
  assert.match(get('execution-message').textContent, /无法执行/);
  assert.equal(get('execution-start').disabled, false);
  get('execution-window-select').change('123');
  assert.equal(sent.at(-1).action, 'select');
  assert.equal(sent.at(-1).value, '123');
});

test('execution does not expose an unsaved recording draft when no saved script is selected', () => {
  const { get, state, push } = desktop();
  push(state({ workspace: 'execution', savedScripts: [], dirty: true }));
  assert.equal(get('execution-script-name').textContent, '请选择已保存的脚本');
  assert.equal(get('execution-step-count').textContent, '0 个步骤');
  assert.equal(get('execution-start').disabled, true);
  assert.equal(get('delete-script').disabled, true);
  assert.equal(get('mode-once').disabled, true);
  assert.match(get('saved-scripts').children[0].textContent, /结束录制后/);
  assert.match(get('execution-help').textContent, /选择/);
  assert.doesNotMatch(get('execution-script-name').textContent, /未保存|未命名/);
});

test('switching protects only failed autosaves and deletion requires confirmation', () => {
  const ui = desktop();
  const { get, sent, state, push, confirmations } = ui;
  push(state({ workspace: 'execution', dirty: true }));
  ui.setConfirm(false);
  get('saved-scripts').children[1].click();
  assert.equal(sent.length, 1);
  assert.match(confirmations[0], /自动保存失败/);
  ui.setConfirm(true);
  get('saved-scripts').children[1].click();
  assert.equal(sent.at(-1).action, 'openScript');
  assert.equal(sent.at(-1).id, 'b');
  ui.setConfirm(false);
  get('delete-script').click();
  assert.equal(sent.at(-1).action, 'openScript');
  ui.setConfirm(true);
  get('delete-script').click();
  assert.equal(sent.at(-1).action, 'deleteScript');
});

test('desktop markup has no manual save or new-script control in execution and includes live steps', () => {
  assert.match(html, /id="execution-steps"/);
  assert.match(html, /id="execution-current-action"/);
  assert.match(html, /id="mode-continuous"/);
  assert.match(html, /id="recording-window-title"[^>]+list="recording-window-options"/);
  assert.match(html, /id="recording-window-card"/);
  assert.match(html, /<dialog id="recording-name-dialog"/);
  assert.match(html, /id="recording-name-input"[^>]+maxlength="100"/);
  assert.doesNotMatch(html, /id="save-execution"|id="new-script"|只需三步/);
  const executionMarkup = html.slice(html.indexOf('<div id="execution-view"'));
  assert.doesNotMatch(executionMarkup, /保存设置|未命名脚本|id="script-name"|id="add-text"/);
  assert.equal([...html.matchAll(/\bid="([^"]+)"/g)].length, ids.size, 'HTML IDs remain unique');
});


test('administrator restart preserves busy recordings and explains elevated foreground limits', () => {
  const { get, sent, state, push } = desktop();
  push(state({ elevated: false }));
  assert.equal(get('restart-admin').hidden, false);
  get('restart-admin').click();
  assert.equal(sent.at(-1).action, 'restartAdmin');
  for (const update of [{ mode: 'recording', recordingAccessWarning: '前台程序以管理员权限运行' }, { namingRequired: true }]) {
    push(state(update));
    const count = sent.length;
    get('restart-admin').click();
    assert.equal(sent.length, count);
  }
  push(state({ mode: 'recording', recordingAccessWarning: '前台程序以管理员权限运行' }));
  assert.equal(get('recording-access-warning').hidden, false);
  assert.match(get('recording-access-warning').textContent, /管理员/);
  push(state({ elevated: true }));
  assert.equal(get('restart-admin').hidden, true);
  assert.equal(get('recording-access-warning').hidden, true);
});


test('settings page exposes four flow combinations and confirms changes through host', () => {
  const {get,sent,state,push}=desktop();
  push(state()); get('nav-settings').click();
  assert.equal(sent.at(-1).value,'settings');
  for(const autoSwitch of [false,true]) for(const returnToApp of [false,true]) {
    push(state({workspace:'settings',flow:{autoSwitch,returnToApp,targetId:'123',targetTitle:'记事本',message:'已自动保存'}}));
    assert.equal(get('settings-view').hidden,false);
    assert.equal(get('workspace-view').hidden,true);
    assert.equal(get('execution-view').hidden,true);
    assert.equal(get('sidebar-library').hidden,true);
    assert.equal(get('flow-target-panel').hidden,!autoSwitch);
    assert.equal(get('flow-auto').checked,autoSwitch);
    assert.equal(get('flow-return').checked,returnToApp);
    assert.ok(get('flow-preview').textContent.includes(returnToApp ? '回到 FlowKey':'保持当前窗口'));
    get('flow-auto').change();
    assert.equal(sent.at(-1).action,'flowSettings');
    assert.equal(sent.at(-1).autoSwitch,autoSwitch);
    assert.equal(sent.at(-1).returnToApp,returnToApp);
  }
  get('flow-target').change('123'); assert.equal(sent.at(-1).targetId,'123');
  get('flow-refresh').click(); assert.equal(sent.at(-1).action,'refresh');
  push(state({workspace:'settings',flow:{autoSwitch:true,returnToApp:false,targetId:'',targetTitle:'旧窗口',message:'保存失败'}}));
  assert.match(get('flow-target-note').textContent,/未找到/);
  assert.equal(get('flow-save-status').textContent,'保存失败');
});

test('settings cannot interrupt recording, execution, pending start or naming', () => {
  const {get,sent,state,push}=desktop();
  for(const update of [{mode:'recording'},{mode:'running'},{pendingRun:true},{namingRequired:true}]) {
    push(state({...update,workspace:'settings'})); const before=sent.length;
    get('nav-settings').click(); get('flow-auto').change(); get('flow-target').change('123'); get('flow-refresh').click();
    assert.equal(sent.length,before);
  }
});


test('settings navigation sits immediately above the local library footer', () => {
  assert.match(html, /class="nav sidebar-settings"[\s\S]*?id="nav-settings"[\s\S]*?<\/nav>\s*<div class="sidebar-bottom">/);
  assert.equal((html.match(/id="nav-settings"/g)||[]).length,1);
  assert.match(html, /\.sidebar-settings\{margin-top:auto;/);
});
test('automatic switching shows pending feedback and supports cancelling instead of asking for manual focus', () => {
  const {get,state,push,sent}=desktop();
  push(state({workspace:'execution',pendingRun:true,switchingWindow:true,flow:{autoSwitch:true,returnToApp:true,targetTitle:'目标',targetId:'123'}}));
  assert.match(get('execution-help').textContent,/正在自动切换/);
  get('execution-start').click(); assert.equal(sent.at(-1).action,'run');
  push(state({workspace:'execution',message:'未找到指定窗口「目标」。请先开启该程序。'}));
  assert.match(get('execution-message').textContent,/未找到指定窗口/);
});
