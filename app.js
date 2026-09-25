(function () {
  'use strict';

  const STORAGE_KEY = 'flowkey-demo-v1';
  const WINDOW_NAMES = ['記事本 — 每週報告.txt', '瀏覽器 — 工作臺', 'Excel — 任務清單.xlsx'];
  const HOTKEYS = ['F8', 'F9', 'F10', 'F12'];
  const SAMPLE_STEPS = [
    { id: 1, type: 'click', title: '點選文件編輯區', detail: '視窗座標 (284, 156)', delay: 500 },
    { id: 2, type: 'keyboard', title: '輸入文字', detail: '“本週專案進展”', delay: 500 },
    { id: 3, type: 'key', title: '按下 Enter', detail: '換到下一行', delay: 800 },
    { id: 4, type: 'scroll', title: '向下滾動', detail: '滾動 2 次', delay: 500 },
    { id: 5, type: 'key', title: '按下 Ctrl + S', detail: '儲存檔案', delay: 0 }
  ];
  const DEMO_ACTIONS = [
    { type: 'click', title: '點選按鈕', detail: '視窗座標 (412, 248)', delay: 500 },
    { type: 'keyboard', title: '輸入文字', detail: '“示例內容”', delay: 500 },
    { type: 'key', title: '按下 Enter', detail: '確認當前輸入', delay: 400 },
    { type: 'scroll', title: '向下滾動', detail: '滾動 1 次', delay: 500 }
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
      $('step-count').textContent = `${state.steps.length} 個步驟`;
      if (state.steps.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = '暫無步驟。錄製時點選下方按鈕，新增模擬操作。';
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
        delay.setAttribute('aria-label', `步驟 ${index + 1} 的等待時間`);
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
        remove.title = '刪除步驟';
        remove.setAttribute('aria-label', `刪除步驟 ${index + 1}`);
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
      $('status-text').textContent = recording ? '正在錄製示例操作' : paused ? '錄製已暫停' : running ? '正在執行腳本' : '就緒，等待操作';
      $('status-hint').textContent = recording ? '切換頁面將暫停錄製' : paused ? '返回後點選繼續錄製' : running ? '再次按快捷鍵可立即停止' : state.loadedSample ? '當前已載入示例腳本' : '本地編輯，尚未自動儲存';
      $('record-button').querySelector('span').textContent = recording ? '結束錄製' : paused ? '繼續錄製' : '開始錄製';
      $('record-button').disabled = running;
      $('run-button').disabled = !running && !canRun(state);
      $('run-button').textContent = running ? '■  停止執行' : '▶  執行腳本';
      $('run-button').className = `button ${running ? 'danger' : 'primary'}`;
      $('add-step').disabled = !recording;
      $('window-select').disabled = running || recording;
      $('hotkey-select').disabled = running || recording;
      $('save-button').disabled = running || recording;
      const completed = running ? state.currentStep + 1 : 0;
      $('progress-label').textContent = running ? '執行中' : '準備就緒';
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
      if (index >= state.steps.length) { stopRun('示例腳本執行完成'); return; }
      state.currentStep = index;
      render();
      const wait = Math.min(Math.max(state.steps[index].delay, 500), 1300);
      timer = setTimeout(() => runNext(index + 1), wait);
    }

    function toggleRun() {
      if (state.mode === 'running') { stopRun('已停止執行'); return; }
      if (!canRun(state)) { toast('請先結束錄製並新增至少一個步驟'); return; }
      state.mode = 'running';
      state.currentStep = -1;
      toast('正在演示腳本執行，不會控制真實視窗');
      runNext(0);
    }

    $('record-button').addEventListener('click', () => {
      if (state.mode === 'running') return;
      if (state.mode === 'recording') {
        state.mode = 'ready';
        toast('錄製結束，可檢查並儲存步驟');
      } else if (state.mode === 'paused') {
        state.mode = 'recording';
        toast('繼續錄製');
      } else {
        state.steps = [];
        state.currentStep = -1;
        state.mode = 'recording';
        state.loadedSample = false;
        toast('開始新的模擬錄製，原步驟已清空');
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
        toast('腳本已儲存在當前瀏覽器');
      } catch (_) { toast('儲存失敗，請檢查瀏覽器儲存許可權'); }
      render();
    });
    $('window-select').addEventListener('change', event => {
      state.windowName = event.target.value;
      state.loadedSample = false;
      render();
      toast('已切換示例目標視窗');
    });
    $('hotkey-select').addEventListener('change', event => {
      state.hotkey = event.target.value;
      state.loadedSample = false;
      render();
      toast(`快捷鍵已設為 ${state.hotkey}；點選儲存腳本可保留設定`);
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
      } else if (state.mode === 'running') stopRun('頁面失去焦點，已停止執行');
    });
    render();
  }

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = { createInitialState, isValidStep, addDemoStep, removeStep, updateStepDelay, canRun };
  }
  if (typeof document !== 'undefined' && !(window.chrome && window.chrome.webview)) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
  }
})();
