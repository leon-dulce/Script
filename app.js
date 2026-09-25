(function () {
  'use strict';

  const STORAGE_KEY = 'flowkey-demo-v1';
  const WINDOW_NAMES = ['记事本 — 每周报告.txt', '浏览器 — 工作台', 'Excel — 任务清单.xlsx'];
  const HOTKEYS = ['F8', 'F9', 'F10', 'F12'];
  const SAMPLE_STEPS = [
    { id: 1, type: 'click', title: '点击文档编辑区', detail: '窗口坐标 (284, 156)', delay: 500 },
    { id: 2, type: 'keyboard', title: '输入文字', detail: '“本周项目进展”', delay: 500 },
    { id: 3, type: 'key', title: '按下 Enter', detail: '换到下一行', delay: 800 },
    { id: 4, type: 'scroll', title: '向下滚动', detail: '滚动 2 次', delay: 500 },
    { id: 5, type: 'key', title: '按下 Ctrl + S', detail: '保存文件', delay: 0 }
  ];
  const DEMO_ACTIONS = [
    { type: 'click', title: '点击按钮', detail: '窗口坐标 (412, 248)', delay: 500 },
    { type: 'keyboard', title: '输入文字', detail: '“示例内容”', delay: 500 },
    { type: 'key', title: '按下 Enter', detail: '确认当前输入', delay: 400 },
    { type: 'scroll', title: '向下滚动', detail: '滚动 1 次', delay: 500 }
  ];

  function createInitialState(saved) {
    const validSaved = saved && typeof saved === 'object' &&
      WINDOW_NAMES.includes(saved.windowName) && HOTKEYS.includes(saved.hotkey) &&
      Array.isArray(saved.steps) && saved.steps.every(isValidStep);
    return {
      windowName: validSaved ? saved.windowName : WINDOW_NAMES[0],
      hotkey: validSaved ? saved.hotkey : 'F10',
      steps: validSaved ? saved.steps.map(step => ({ ...step })) : SAMPLE_STEPS.map(step => ({ ...step })),
      mode: 'ready',
      currentStep: -1,
      loadedSample: !validSaved
    };
  }

  function isValidStep(step) {
    return step && Number.isSafeInteger(step.id) &&
      ['click', 'keyboard', 'key', 'scroll'].includes(step.type) &&
      typeof step.title === 'string' && typeof step.detail === 'string' &&
      Number.isInteger(step.delay) && step.delay >= 0 && step.delay <= 5000;
  }

  function addDemoStep(steps) {
    const nextId = steps.reduce((max, step) => Math.max(max, step.id), 0) + 1;
    const action = DEMO_ACTIONS[steps.length % DEMO_ACTIONS.length];
    return [...steps, { id: nextId, ...action }];
  }

  function removeStep(steps, id) {
    return steps.filter(step => step.id !== id);
  }

  function updateStepDelay(steps, id, delay) {
    if (!Number.isInteger(delay) || delay < 0 || delay > 5000) return steps;
    return steps.map(step => step.id === id ? { ...step, delay } : step);
  }

  function canRun(state) {
    return state.mode === 'ready' && state.steps.length > 0;
  }

  function init() {
    const $ = id => document.getElementById(id);
    let stored = null;
    try { stored = JSON.parse(localStorage.getItem(STORAGE_KEY)); } catch (_) { /* Ignore invalid saved data. */ }
    const state = createInitialState(stored);
    let timer = null;
    let toastTimer = null;
    const icons = { click: '↖', keyboard: '⌨', key: '↵', scroll: '↕' };

    function toast(message) {
      const box = $('toast');
      box.textContent = message;
      box.classList.add('show');
      clearTimeout(toastTimer);
      toastTimer = setTimeout(() => box.classList.remove('show'), 2600);
    }

    function renderSteps() {
      const container = $('steps');
      container.replaceChildren();
      $('step-count').textContent = `${state.steps.length} 个步骤`;
      if (state.steps.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = '暂无步骤。录制时点击下方按钮，添加模拟操作。';
        container.appendChild(empty);
      }
      state.steps.forEach((step, index) => {
        const row = document.createElement('div');
        row.className = `step${state.currentStep === index ? ' current' : ''}`;
        const number = document.createElement('span');
        number.className = 'step-number';
        number.textContent = String(index + 1).padStart(2, '0');
        const icon = document.createElement('span');
        icon.className = 'step-icon';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = icons[step.type];
        const copy = document.createElement('div');
        copy.className = 'step-copy';
        const title = document.createElement('strong');
        title.textContent = step.title;
        const detail = document.createElement('small');
        detail.textContent = step.detail;
        copy.append(title, detail);
        const tools = document.createElement('div');
        tools.className = 'step-tools';
        const delay = document.createElement('select');
        delay.className = 'delay';
        delay.setAttribute('aria-label', `步骤 ${index + 1} 的等待时间`);
        [0, 400, 500, 800, 1000, 2000, 5000].forEach(value => {
          const option = document.createElement('option');
          option.value = String(value);
          option.textContent = value === 0 ? '立即' : `${(value / 1000).toFixed(1)} 秒`;
          delay.appendChild(option);
        });
        if (![0, 400, 500, 800, 1000, 2000, 5000].includes(step.delay)) {
          const option = document.createElement('option');
          option.value = String(step.delay);
          option.textContent = `${(step.delay / 1000).toFixed(1)} 秒`;
          delay.appendChild(option);
        }
        delay.value = String(step.delay);
        delay.disabled = state.mode !== 'ready';
        delay.addEventListener('change', () => {
          state.steps = updateStepDelay(state.steps, step.id, Number(delay.value));
          state.loadedSample = false;
          render();
        });
        const remove = document.createElement('button');
        remove.className = 'step-delete';
        remove.type = 'button';
        remove.textContent = '×';
        remove.title = '删除步骤';
        remove.setAttribute('aria-label', `删除步骤 ${index + 1}`);
        remove.disabled = state.mode !== 'ready';
        remove.addEventListener('click', () => {
          state.steps = removeStep(state.steps, step.id);
          state.loadedSample = false;
          render();
        });
        tools.append(delay, remove);
        row.append(number, icon, copy, tools);
        container.appendChild(row);
      });
    }

    function render() {
      $('window-name').textContent = state.windowName;
      $('window-select').value = state.windowName;
      $('hotkey-select').value = state.hotkey;
      const recording = state.mode === 'recording';
      const paused = state.mode === 'paused';
      const running = state.mode === 'running';
      $('status-dot').className = `pulse${recording ? ' recording' : running ? ' running' : ''}`;
      $('status-text').textContent = recording ? '正在录制示例操作' : paused ? '录制已暂停' : running ? '正在执行脚本' : '就绪，等待操作';
      $('status-hint').textContent = recording ? '切换页面将暂停录制' : paused ? '返回后点击继续录制' : running ? '再次按快捷键可立即停止' : state.loadedSample ? '当前已载入示例脚本' : '本地编辑，尚未自动保存';
      $('record-button').querySelector('span').textContent = recording ? '结束录制' : paused ? '继续录制' : '开始录制';
      $('record-button').disabled = running;
      $('run-button').disabled = !running && !canRun(state);
      $('run-button').textContent = running ? '■  停止执行' : '▶  执行脚本';
      $('run-button').className = `button ${running ? 'danger' : 'primary'}`;
      $('add-step').disabled = !recording;
      $('window-select').disabled = running || recording;
      $('hotkey-select').disabled = running || recording;
      $('save-button').disabled = running || recording;
      const completed = running ? state.currentStep + 1 : 0;
      $('progress-label').textContent = running ? '执行中' : '准备就绪';
      $('progress-fraction').textContent = `${completed} / ${state.steps.length}`;
      $('progress-fill').style.width = state.steps.length ? `${100 * completed / state.steps.length}%` : '0%';
      renderSteps();
    }

    function stopRun(message) {
      clearTimeout(timer);
      timer = null;
      state.mode = 'ready';
      state.currentStep = -1;
      render();
      if (message) toast(message);
    }

    function runNext(index) {
      if (state.mode !== 'running') return;
      if (index >= state.steps.length) { stopRun('示例脚本执行完成'); return; }
      state.currentStep = index;
      render();
      const wait = Math.min(Math.max(state.steps[index].delay, 500), 1300);
      timer = setTimeout(() => runNext(index + 1), wait);
    }

    function toggleRun() {
      if (state.mode === 'running') { stopRun('已停止执行'); return; }
      if (!canRun(state)) { toast('请先结束录制并添加至少一个步骤'); return; }
      state.mode = 'running';
      state.currentStep = -1;
      toast('正在演示脚本执行，不会控制真实窗口');
      runNext(0);
    }

    $('record-button').addEventListener('click', () => {
      if (state.mode === 'running') return;
      if (state.mode === 'recording') {
        state.mode = 'ready';
        toast('录制结束，可检查并保存步骤');
      } else if (state.mode === 'paused') {
        state.mode = 'recording';
        toast('继续录制');
      } else {
        state.steps = [];
        state.currentStep = -1;
        state.mode = 'recording';
        state.loadedSample = false;
        toast('开始新的模拟录制，原步骤已清空');
      }
      render();
    });
    $('add-step').addEventListener('click', () => {
      if (state.mode !== 'recording') return;
      state.steps = addDemoStep(state.steps);
      render();
      $('steps').scrollTop = $('steps').scrollHeight;
    });
    $('run-button').addEventListener('click', toggleRun);
    $('save-button').addEventListener('click', () => {
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify({ windowName: state.windowName, hotkey: state.hotkey, steps: state.steps }));
        state.loadedSample = false;
        toast('脚本已保存在当前浏览器');
      } catch (_) { toast('保存失败，请检查浏览器存储权限'); }
      render();
    });
    $('window-select').addEventListener('change', event => {
      state.windowName = event.target.value;
      state.loadedSample = false;
      render();
      toast('已切换示例目标窗口');
    });
    $('hotkey-select').addEventListener('change', event => {
      state.hotkey = event.target.value;
      state.loadedSample = false;
      render();
      toast(`快捷键已设为 ${state.hotkey}；点击保存脚本可保留设置`);
    });
    document.addEventListener('keydown', event => {
      if (event.key !== state.hotkey || event.repeat) return;
      event.preventDefault();
      toggleRun();
    });
    document.querySelectorAll('[data-toast]').forEach(button => {
      button.addEventListener('click', () => toast(button.dataset.toast));
    });
    window.addEventListener('blur', () => {
      if (state.mode === 'recording') {
        state.mode = 'paused';
        render();
      } else if (state.mode === 'running') stopRun('页面失去焦点，已停止执行');
    });
    render();
  }

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = { createInitialState, isValidStep, addDemoStep, removeStep, updateStepDelay, canRun };
  }
  if (typeof document !== 'undefined') {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
  }
})();
