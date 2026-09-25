(function () {
  'use strict';
  if (!(window.chrome && window.chrome.webview)) return;

  const $ = id => document.getElementById(id);
  const send = (action, fields = {}) => window.chrome.webview.postMessage({ action, ...fields });
  const icons = { Click: '↖', DoubleClick: '↖', Scroll: '↕', Key: '↵', Text: '⌨' };
  let state = null;
  let lastMessage = '';
  let view = 'editor';

  function executionLabel(plan) {
    if (plan.mode === 'Count') return `执行 ${plan.repeatCount} 次 · 每轮间隔 ${plan.intervalMs} 毫秒`;
    if (plan.mode === 'Continuous') return `持续执行 · 每轮间隔 ${plan.intervalMs} 毫秒`;
    return '只执行一次';
  }

  function showView(next) {
    view = next;
    $('workspace-view').hidden = next !== 'editor';
    $('execution-view').hidden = next !== 'execution';
    $('sidebar-library').hidden = next !== 'execution';
    for (const [name, id] of [['editor', 'nav-editor'], ['execution', 'nav-execution']]) {
      const button = $(id);
      button.classList.toggle('active', name === next);
      if (name === next) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    }
  }

  function confirmDiscard() {
    return !state?.dirty || window.confirm('当前脚本有尚未保存的修改，确定要离开吗？');
  }

  function renderLibrary() {
    const library = $('saved-scripts');
    library.replaceChildren();
    if (!state.savedScripts.length) {
      const empty = document.createElement('div');
      empty.className = 'library-empty';
      empty.textContent = '尚无已保存脚本。录制完成后点击保存。';
      library.append(empty);
    }
    state.savedScripts.forEach(script => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = `saved-script${script.id === state.script.id ? ' active' : ''}`;
      button.disabled = state.mode !== 'ready';
      const title = document.createElement('strong');
      title.textContent = script.name;
      const detail = document.createElement('small');
      detail.textContent = `${script.stepCount} 个步骤 · ${script.mode === 'Continuous' ? '持续' : script.mode === 'Count' ? '指定次数' : '单次'}`;
      button.append(title, detail);
      button.addEventListener('click', () => {
        if (view === 'execution' && script.id !== state.script.id && confirmDiscard()) send('openScript', { id: script.id });
      });
      library.append(button);
    });
    $('new-script').disabled = state.mode !== 'ready';
    $('delete-script').disabled = state.mode !== 'ready' || !state.savedScripts.some(script => script.id === state.script.id);
  }

  function renderExecution(selected) {
    const plan = state.script.execution || { mode: 'Once', repeatCount: 1, intervalMs: 1000 };
    $('plan-summary').textContent = executionLabel(plan);
    $('execution-script-name').textContent = state.script.name + (state.dirty ? ' · 未保存' : '');
    $('execution-target').textContent = selected ? `${selected.process} · ${selected.title} · 已确认` : '请在下方选择执行目标窗口';
    const targetSelect = $('execution-window-select');
    targetSelect.replaceChildren();
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '选择执行目标窗口';
    targetSelect.append(placeholder);
    state.windows.forEach(item => {
      const option = document.createElement('option');
      option.value = item.id;
      option.textContent = `${item.process} · ${item.title}`;
      targetSelect.append(option);
    });
    targetSelect.value = state.selectedId;
    targetSelect.disabled = state.mode !== 'ready';
    $('execution-refresh-windows').disabled = state.mode !== 'ready';
    $('execution-message').textContent = state.message;
    for (const mode of ['Once', 'Count', 'Continuous']) $(`mode-${mode.toLowerCase()}`).checked = plan.mode === mode;
    $('repeat-count').value = String(plan.repeatCount);
    $('repeat-count').disabled = plan.mode !== 'Count' || state.mode !== 'ready';
    $('repeat-interval').value = String(plan.intervalMs);
    $('repeat-interval').disabled = plan.mode === 'Once' || state.mode !== 'ready';
    $('execution-description').textContent = plan.mode === 'Once'
      ? '脚本将从第一步到最后一步执行一次。'
      : plan.mode === 'Count'
        ? `脚本将执行 ${plan.repeatCount} 轮；每轮完成后等待 ${plan.intervalMs} 毫秒，再开始下一轮。`
        : `脚本将持续循环；每轮完成后等待 ${plan.intervalMs} 毫秒。再次按 ${state.script.hotkey} 可停止。`;
    $('execution-hotkey').value = state.script.hotkey;
    $('execution-hotkey').disabled = state.mode !== 'ready';
    $('save-execution').disabled = state.mode !== 'ready';
    const active = state.mode === 'running' || state.pendingRun;
    $('execution-start').disabled = !active && state.mode !== 'ready';
    $('execution-start').textContent = active ? '■ 停止执行' : '▶ 开始执行';
    $('execution-start').className = `button ${active ? 'danger' : 'primary'}`;
    $('execution-help').textContent = state.pendingRun
      ? '已准备执行，切回目标窗口后开始；再次点击可取消。'
      : state.mode === 'running' ? `再按 ${state.script.hotkey} 可停止执行。`
        : !selected ? '请先在上方选择执行目标窗口。'
          : !state.script.steps.length ? '当前脚本还没有步骤，请到录制与编辑页完成录制。'
            : `切回目标窗口按 ${state.script.hotkey} 执行当前脚本；再次按下停止。`;
    $('cycle-number').textContent = state.mode === 'running'
      ? `${state.currentIteration}${plan.mode === 'Count' ? ` / ${plan.repeatCount}` : ''}${state.waitingForNextRun ? ' · 等待下一轮' : ''}`
      : '—';
    $('execution-progress-label').textContent = state.waitingForNextRun ? '等待下一轮' : state.mode === 'running' ? '执行中' : state.pendingRun ? '等待目标窗口' : '准备就绪';
    const current = state.mode === 'running' && !state.waitingForNextRun ? state.currentStep + 1 : 0;
    $('execution-progress-fraction').textContent = `${current} / ${state.script.steps.length}`;
    $('execution-progress-fill').style.width = state.script.steps.length ? `${current * 100 / state.script.steps.length}%` : '0%';
  }

  function submitExecution() {
    const mode = ['Once', 'Count', 'Continuous'].find(value => $(`mode-${value.toLowerCase()}`).checked);
    const count = Number($('repeat-count').value);
    const intervalMs = Number($('repeat-interval').value);
    if (!mode || !Number.isInteger(count) || count < 1 || count > 10000 ||
        !Number.isInteger(intervalMs) || intervalMs < 100 || intervalMs > 60000) {
      window.alert('执行次数须为 1–10000，轮次间隔须为 100–60000 毫秒。');
      return;
    }
    send('execution', { mode, count, intervalMs });
  }

  function stepLabel(step) {
    switch (step.type) {
      case 'Click': return ['点击', `${step.button} · (${step.x}, ${step.y})`];
      case 'DoubleClick': return ['双击', `${step.button} · (${step.x}, ${step.y})`];
      case 'Scroll': return ['滚动', `${step.wheelDelta > 0 ? '向上' : '向下'} ${Math.abs(step.wheelDelta)} 格`];
      case 'Key': return ['按键', step.keys.map(key => ({ 17: 'Ctrl', 18: 'Alt', 16: 'Shift', 91: 'Win', 13: 'Enter', 9: 'Tab', 27: 'Esc' })[key] || (key >= 65 && key <= 90 ? String.fromCharCode(key) : `VK ${key}`)).join(' + ')];
      case 'Text': return ['输入文字', step.text];
      default: return ['未知步骤', ''];
    }
  }

  function renderSteps() {
    const container = $('steps');
    container.replaceChildren();
    $('step-count').textContent = `${state.script.steps.length} 个步骤`;
    if (!state.script.steps.length) {
      const empty = document.createElement('div');
      empty.className = 'empty';
      empty.textContent = '暂无步骤。选中目标窗口并开始录制。';
      container.appendChild(empty);
    }
    state.script.steps.forEach((step, index) => {
      const row = document.createElement('div');
      row.className = `step${state.currentStep === index ? ' current' : ''}`;
      const number = document.createElement('span');
      number.className = 'step-number';
      number.textContent = String(index + 1).padStart(2, '0');
      const icon = document.createElement('span');
      icon.className = 'step-icon';
      icon.textContent = icons[step.type] || '?';
      const copy = document.createElement('div');
      copy.className = 'step-copy';
      const title = document.createElement('strong');
      const detail = document.createElement('small');
      [title.textContent, detail.textContent] = stepLabel(step);
      copy.append(title, detail);
      const controls = document.createElement('div');
      controls.className = 'step-tools';
      const delay = document.createElement('input');
      delay.type = 'number';
      delay.min = '0';
      delay.max = '60000';
      delay.step = '50';
      delay.value = String(step.delayMs);
      delay.className = 'delay';
      delay.style.width = '74px';
      delay.title = '执行前等待毫秒数';
      delay.setAttribute('aria-label', `步骤 ${index + 1} 的等待毫秒数`);
      delay.disabled = state.mode !== 'ready';
      delay.addEventListener('change', () => send('delay', { index, value: Number(delay.value) }));
      controls.append(delay);
      if (step.type === 'Text') {
        const edit = document.createElement('button');
        edit.className = 'step-delete';
        edit.textContent = '✎';
        edit.title = '编辑文字';
        edit.disabled = state.mode !== 'ready';
        edit.addEventListener('click', () => {
          const value = window.prompt('编辑输入文字步骤', step.text);
          if (value) send('text', { index, value });
        });
        controls.append(edit);
      }
      const remove = document.createElement('button');
      remove.className = 'step-delete';
      remove.textContent = '×';
      remove.title = '删除步骤';
      remove.disabled = state.mode !== 'ready';
      remove.addEventListener('click', () => send('delete', { index }));
      controls.append(remove);
      row.append(number, icon, copy, controls);
      container.append(row);
    });
  }

  function render() {
    const selected = state.windows.find(item => item.id === state.selectedId);
    const select = $('window-select');
    select.replaceChildren();
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '选择窗口';
    select.append(placeholder);
    state.windows.forEach(item => {
      const option = document.createElement('option');
      option.value = item.id;
      option.textContent = `${item.process} · ${item.title}`;
      select.append(option);
    });
    select.value = state.selectedId;
    select.disabled = state.mode !== 'ready';
    $('window-name').textContent = selected ? selected.title : '尚未选择目标窗口';
    document.querySelector('.window-meta small').textContent = selected ? `${selected.process} · 已确认` : '请选择当前可见窗口';
    $('hotkey-select').value = state.script.hotkey;
    if (document.activeElement !== $('script-name')) $('script-name').value = state.script.name;
    $('script-name').disabled = state.mode !== 'ready';
    $('hotkey-select').disabled = state.mode !== 'ready';
    $('status-text').textContent = ({ ready: '就绪，等待操作', recording: '正在录制', paused: '录制已暂停', running: '正在执行脚本' })[state.mode] || '错误';
    $('status-hint').textContent = state.message;
    $('status-dot').className = `pulse${state.mode === 'recording' ? ' recording' : state.mode === 'running' ? ' running' : ''}`;
    $('record-button').querySelector('span').textContent = state.mode === 'recording' ? '结束录制' : state.mode === 'paused' ? '继续录制' : '开始录制';
    $('record-button').disabled = state.mode === 'running' || !selected;
    $('pause-button').hidden = state.mode !== 'recording';
    $('finish-button').hidden = state.mode !== 'paused';
    $('save-button').disabled = state.mode !== 'ready';
    $('load-button').disabled = state.mode !== 'ready';
    $('new-editor-script').disabled = state.mode !== 'ready';
    $('add-text').disabled = state.mode !== 'ready';
    const active = state.mode === 'running' || state.pendingRun;
    $('run-button').disabled = !active && (!selected || !state.script.steps.length || state.mode !== 'ready');
    $('run-button').textContent = active ? '■ 停止执行' : '▶ 执行脚本';
    $('run-button').className = `button ${active ? 'danger' : 'primary'}`;
    $('progress-label').textContent = state.waitingForNextRun ? '等待下一轮' : state.mode === 'running' ? '执行中' : state.pendingRun ? '等待目标窗口' : '准备就绪';
    const current = state.mode === 'running' && !state.waitingForNextRun ? state.currentStep + 1 : 0;
    $('progress-fraction').textContent = `${current} / ${state.script.steps.length}`;
    $('progress-fill').style.width = state.script.steps.length ? `${current * 100 / state.script.steps.length}%` : '0%';
    document.querySelector('.help').textContent = state.mode === 'recording'
      ? `按 ${state.script.hotkey} 结束录制。`
      : state.script.steps.length === 0
        ? `确认目标窗口后，切回该窗口按 ${state.script.hotkey} 开始录制；再次按下结束。`
        : `切回目标窗口按 ${state.script.hotkey} 执行脚本；再次按下停止。`;
    renderSteps();
    renderLibrary();
    renderExecution(selected);
    if (state.message !== lastMessage) {
      lastMessage = state.message;
      const toast = $('toast');
      toast.textContent = state.message;
      toast.classList.add('show');
      window.setTimeout(() => toast.classList.remove('show'), 2800);
    }
  }

  document.addEventListener('DOMContentLoaded', () => {
    $('load-button').hidden = false;
    $('pause-button').hidden = true;
    $('refresh-windows').hidden = false;
    $('window-select').style.maxWidth = '240px';
    document.querySelector('.window-meta small').textContent = '请选择当前可见窗口';
    $('plan-summary').hidden = false;
    $('open-execution').hidden = false;
    $('new-editor-script').hidden = false;
    $('add-text').hidden = false;
    $('script-name-row').hidden = false;
    $('add-step').hidden = true;
    document.querySelector('.demo-badge').textContent = 'DESKTOP APP';
    document.querySelector('.sidebar-bottom').innerHTML = '<strong><span class="dot"></span>本地模式</strong>脚本保存在本机。录制敏感输入前请暂停。';
    document.querySelector('.footer-note').textContent = '脚本仅在选定窗口位于前台且尺寸一致时执行。';
    document.querySelector('.help').textContent = '确认目标窗口后，切回该窗口按快捷键开始录制。';
    $('hotkey-select').replaceChildren(...['F8', 'F9', 'F10', 'F11'].map(key => {
      const option = document.createElement('option'); option.value = key; option.textContent = key; return option;
    }));
    $('refresh-windows').addEventListener('click', () => send('refresh'));
    $('window-select').addEventListener('change', event => send('select', { value: event.target.value }));
    $('execution-refresh-windows').addEventListener('click', () => send('refresh'));
    $('execution-window-select').addEventListener('change', event => send('select', { value: event.target.value }));
    $('hotkey-select').addEventListener('change', event => send('hotkey', { value: event.target.value }));
    $('execution-hotkey').addEventListener('change', event => send('hotkey', { value: event.target.value }));
    $('nav-editor').addEventListener('click', () => showView('editor'));
    $('nav-execution').addEventListener('click', () => showView('execution'));
    $('open-execution').addEventListener('click', () => showView('execution'));
    const createScript = () => { if (confirmDiscard()) { send('newScript'); showView('editor'); } };
    $('new-script').addEventListener('click', createScript);
    $('new-editor-script').addEventListener('click', createScript);
    $('delete-script').addEventListener('click', () => {
      if (state && window.confirm(`确定删除「${state.script.name}」吗？此操作无法恢复。`)) send('deleteScript', { id: state.script.id });
    });
    for (const id of ['mode-once', 'mode-count', 'mode-continuous', 'repeat-count', 'repeat-interval'])
      $(id).addEventListener('change', submitExecution);
    $('save-execution').addEventListener('click', () => send('save'));
    $('execution-start').addEventListener('click', () => send('run'));
    $('script-name').addEventListener('change', event => send('name', { value: event.target.value }));
    $('record-button').addEventListener('click', () => send('record'));
    $('pause-button').addEventListener('click', () => send('pause'));
    $('finish-button').addEventListener('click', () => send('finish'));
    $('run-button').addEventListener('click', () => send('run'));
    $('save-button').addEventListener('click', () => send('save'));
    $('load-button').addEventListener('click', () => send('load'));
    $('add-text').addEventListener('click', () => {
      const value = window.prompt('输入回放时要写入的文字');
      if (value) send('text', { value });
    });
    window.chrome.webview.addEventListener('message', event => { state = event.data; render(); });
    showView('editor');
    send('refresh');
  });
})();
