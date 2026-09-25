const test = require('node:test');
const assert = require('node:assert/strict');
const {
  createInitialState,
  isValidStep,
  addDemoStep,
  removeStep,
  updateStepDelay,
  canRun
} = require('../app.js');

test('starts with a reviewable sample script and F10 shortcut', () => {
  const state = createInitialState(null);
  assert.equal(state.hotkey, 'F10');
  assert.equal(state.mode, 'ready');
  assert.equal(state.steps.length, 5);
  assert.equal(canRun(state), true);
});

test('loads a saved script without retaining runtime playback state', () => {
  const saved = {
    windowName: '瀏覽器 — 工作臺', hotkey: 'F12', mode: 'running',
    steps: [{ id: 9, type: 'click', title: '點選', detail: '位置', delay: 500 }]
  };
  const state = createInitialState(saved);
  assert.equal(state.windowName, saved.windowName);
  assert.equal(state.hotkey, 'F12');
  assert.equal(state.mode, 'ready');
  assert.notEqual(state.steps[0], saved.steps[0]);
});

test('rejects invalid saved steps and restores a safe sample', () => {
  const state = createInitialState({
    windowName: '瀏覽器 — 工作臺', hotkey: 'F12',
    steps: [{ id: 1, type: 'bad', title: '錯誤', detail: '', delay: -1 }]
  });
  assert.equal(state.loadedSample, true);
  assert.equal(state.steps.length, 5);
  assert.equal(isValidStep(state.steps[0]), true);
});

test('adding, editing, and deleting steps keeps prior state untouched', () => {
  const original = [];
  const one = addDemoStep(original);
  const two = addDemoStep(one);
  assert.equal(original.length, 0);
  assert.equal(one[0].id, 1);
  assert.equal(two[1].id, 2);
  assert.equal(two[1].type, 'keyboard');
  const edited = updateStepDelay(two, 2, 1000);
  assert.equal(two[1].delay, 500);
  assert.equal(edited[1].delay, 1000);
  assert.equal(updateStepDelay(edited, 2, -1), edited);
  assert.deepEqual(removeStep(edited, 1).map(step => step.id), [2]);
});

test('script cannot start while recording, paused, running, or empty', () => {
  const state = createInitialState(null);
  for (const mode of ['recording', 'paused', 'running']) {
    assert.equal(canRun({ ...state, mode }), false);
  }
  assert.equal(canRun({ ...state, steps: [] }), false);
});
