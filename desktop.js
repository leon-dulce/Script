(function () {
  'use strict';
  if (!(window.chrome && window.chrome.webview)) return;

  const $ = id => document.getElementById(id);
  const send = (action, fields = {}) => window.chrome.webview.postMessage({ action, ...fields });
  const icons = { Click: '↖', DoubleClick: '↖', Scroll: '↕', Key: '↵', Text: '⌨' };
  let state = null;
  let lastMessage = '';

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
    $('add-text').disabled = state.mode !== 'ready';
    $('run-button').disabled = state.mode !== 'running' && (!selected || !state.script.steps.length || state.mode !== 'ready');
    $('run-button').textContent = state.mode === 'running' ? '■ 停止执行' : '▶ 执行脚本';
    $('run-button').className = `button ${state.mode === 'running' ? 'danger' : 'primary'}`;
    $('progress-label').textContent = state.mode === 'running' ? '执行中' : '准备就绪';
    $('progress-fraction').textContent = `${state.mode === 'running' ? state.currentStep + 1 : 0} / ${state.script.steps.length}`;
    $('progress-fill').style.width = state.script.steps.length && state.mode === 'running' ? `${(state.currentStep + 1) * 100 / state.script.steps.length}%` : '0%';
    renderSteps();
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
    document.querySelector('.window-meta small').textContent = '当前可见窗口 · Windows 桌面应用';
    document.querySelectorAll('.nav button:not(.active)').forEach(button => { button.hidden = true; });
    $('add-text').hidden = false;
    $('script-name-row').hidden = false;
    $('add-step').hidden = true;
    document.querySelector('.demo-badge').textContent = 'DESKTOP APP';
    document.querySelector('.sidebar-bottom').innerHTML = '<strong><span class="dot"></span>本地模式</strong>脚本保存在本机。录制敏感输入前请暂停。';
    document.querySelector('.footer-note').textContent = '脚本仅在选定窗口位于前台且尺寸一致时执行。';
    document.querySelector('.help').textContent = '请切回目标窗口后按快捷键启动或停止。点击执行会检查目标窗口是否在前台。';
    $('hotkey-select').replaceChildren(...['F8', 'F9', 'F10', 'F11'].map(key => {
      const option = document.createElement('option'); option.value = key; option.textContent = key; return option;
    }));
    $('refresh-windows').addEventListener('click', () => send('refresh'));
    $('window-select').addEventListener('change', event => send('select', { value: event.target.value }));
    $('hotkey-select').addEventListener('change', event => send('hotkey', { value: event.target.value }));
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
    send('refresh');
  });
})();
