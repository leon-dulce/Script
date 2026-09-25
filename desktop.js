(function () {
  'use strict';
  if (!(window.chrome && window.chrome.webview)) return;

  const $ = id => document.getElementById(id);
  const send = (action, fields = {}) => window.chrome.webview.postMessage({ action, ...fields });
  const icons = { Click: '↖', DoubleClick: '↖', Scroll: '↕', Key: '↵', Text: '⌨' };
  let state = null;
  let lastMessage = '';
  let view = 'editor';
  let lastHighlightedStep = '';

  const isBusy = () => !state || state.mode !== 'ready' || state.pendingRecord || state.pendingRun;
  const hasSavedScript = () => state.savedScripts.some(script => script.id === state.script.id);

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

  function navigate(next) {
    if (isBusy() || view === next) return;
    showView(next);
    send('workspace', { value: next });
  }

  function renderLibrary() {
    const library = $('saved-scripts');
    library.replaceChildren();
    $('library-count').textContent = String(state.savedScripts.length);
    if (!state.savedScripts.length) {
      const empty = document.createElement('div');
      empty.className = 'library-empty';
      empty.textContent = '尚无已保存脚本。结束录制后，脚本会自动出现在这里。';
      library.append(empty);
    }
    state.savedScripts.forEach(script => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = `saved-script${script.id === state.script.id ? ' active' : ''}`;
      button.disabled = isBusy();
      button.setAttribute('aria-pressed', String(script.id === state.script.id));
      const title = document.createElement('strong');
      title.textContent = script.name;
      const detail = document.createElement('small');
      detail.textContent = `${script.stepCount} 个步骤 · ${script.mode === 'Continuous' ? '持续' : script.mode === 'Count' ? '指定次数' : '单次'}`;
      button.append(title, detail);
      button.addEventListener('click', () => {
        if (view === 'execution' && !isBusy() && script.id !== state.script.id &&
            (!state.dirty || window.confirm('上次自动保存失败。确定切换脚本并放弃尚未保存的修改吗？')))
          send('openScript', { id: script.id });
      });
      library.append(button);
    });
    $('delete-script').disabled = isBusy() || !hasSavedScript();
  }

  function renderExecution(selected) {
    const plan = state.script.execution || { mode: 'Once', repeatCount: 1, intervalMs: 1000 };
    const saved = hasSavedScript();
    const settingsDisabled = isBusy() || !saved;
    $('plan-summary').textContent = executionLabel(plan);
    $('execution-script-name').textContent = saved ? state.script.name : '请选择已保存的脚本';
    $('execution-target').textContent = selected ? `${selected.process} · ${selected.title} · 已确认` : '在目标窗口按快捷键，即可确认窗口并执行';
    const targetSelect = $('execution-window-select');
    targetSelect.replaceChildren();
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '自动使用快捷键所在窗口';
    targetSelect.append(placeholder);
    state.windows.forEach(item => {
      const option = document.createElement('option');
      option.value = item.id;
      option.textContent = `${item.process} · ${item.title}`;
      targetSelect.append(option);
    });
    targetSelect.value = state.selectedId;
    targetSelect.disabled = settingsDisabled;
    $('execution-refresh-windows').disabled = settingsDisabled;
    $('execution-message').textContent = state.message;
    for (const mode of ['Once', 'Count', 'Continuous']) {
      const input = $(`mode-${mode.toLowerCase()}`);
      input.checked = plan.mode === mode;
      input.disabled = settingsDisabled;
    }
    $('repeat-count').value = String(plan.repeatCount);
    $('repeat-count').disabled = plan.mode !== 'Count' || settingsDisabled;
    $('repeat-interval').value = String(plan.intervalMs);
    $('repeat-interval').disabled = plan.mode === 'Once' || settingsDisabled;
    $('execution-description').textContent = !saved ? '从左侧选择一个已保存脚本，即可设置运行方式。' : plan.mode === 'Once'
      ? '脚本将从第一步到最后一步执行一次。'
      : plan.mode === 'Count'
        ? `脚本将执行 ${plan.repeatCount} 轮；每轮完成后等待 ${plan.intervalMs} 毫秒，再开始下一轮。`
        : `脚本将持续循环；每轮完成后等待 ${plan.intervalMs} 毫秒。再次按 ${state.script.hotkey} 可停止。`;
    $('execution-hotkey').value = state.script.hotkey;
    $('execution-hotkey').disabled = settingsDisabled;
    const active = state.mode === 'running' || state.pendingRun;
    $('execution-start').disabled = !active && (isBusy() || !saved || !state.script.steps.length);
    $('execution-start').textContent = active ? '■ 停止执行' : '▶ 开始执行';
    $('execution-start').className = `button ${active ? 'danger' : 'primary'}`;
    $('execution-help').textContent = state.pendingRun
      ? '已准备执行，切回目标窗口后开始；再次点击可取消等待。'
      : state.mode === 'running' ? `再按 ${state.script.hotkey} 可停止执行。`
        : !saved ? '请从左侧选择要执行的已保存脚本。'
          : !state.script.steps.length ? '此脚本没有可执行的步骤。'
            : `在目标窗口按 ${state.script.hotkey} 开始执行；再次按下停止。也可点击「开始执行」后切回目标窗口。`;
    $('cycle-number').textContent = state.mode === 'running'
      ? `${state.currentIteration}${plan.mode === 'Count' ? ` / ${plan.repeatCount}` : ''}${state.waitingForNextRun ? ' · 等待下一轮' : ''}`
      : '—';
    $('execution-progress-label').textContent = state.waitingForNextRun ? '等待下一轮' : state.mode === 'running' ? '执行中' : state.pendingRun ? '等待目标窗口' : '准备就绪';
    const current = state.mode === 'running' && !state.waitingForNextRun ? state.currentStep + 1 : 0;
    const steps = saved ? state.script.steps : [];
    $('execution-progress-fraction').textContent = `${current} / ${steps.length}`;
    $('execution-progress-fill').style.width = steps.length ? `${current * 100 / steps.length}%` : '0%';
    $('execution-step-count').textContent = `${steps.length} 个步骤`;
    const activeStep = state.mode === 'running' && !state.waitingForNextRun ? steps[state.currentStep] : null;
    $('execution-current-action').textContent = !saved ? '等待选择脚本'
      : state.waitingForNextRun ? `第 ${state.currentIteration} 轮已完成，等待 ${plan.intervalMs} 毫秒后继续`
        : activeStep ? `第 ${state.currentStep + 1} 步 · ${stepLabel(activeStep).join('：')}`
          : state.pendingRun ? '等待目标窗口进入前台'
            : state.mode === 'running' ? '正在开始执行'
              : '等待执行，启动后会自动跟随当前步骤';
    renderSteps('execution-steps', steps, false);
  }

  function submitExecution() {
    if (view !== 'execution' || isBusy() || !hasSavedScript()) return;
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

  function renderSteps(containerId, steps, editable) {
    const container = $(containerId);
    container.replaceChildren();
    if (!steps.length) {
      const empty = document.createElement('div');
      empty.className = 'empty';
      empty.textContent = editable ? '在要录制的窗口按快捷键，开始记录键盘与鼠标操作。' : '从左侧选择一个已保存脚本，查看完整执行步骤。';
      container.appendChild(empty);
    }
    steps.forEach((step, index) => {
      const current = !editable && state.mode === 'running' && !state.waitingForNextRun && state.currentStep === index;
      const row = document.createElement('div');
      row.className = `step${current ? ' current' : ''}`;
      if (current) row.setAttribute('aria-current', 'step');
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
      row.append(number, icon, copy);
      if (!editable) {
        const delay = document.createElement('span');
        delay.className = 'execution-delay';
        delay.textContent = step.delayMs ? `等待 ${step.delayMs} 毫秒` : '立即执行';
        row.append(delay);
        container.append(row);
        const highlightKey = `${state.script.id}:${state.currentIteration}:${index}`;
        if (current && view === 'execution' && highlightKey !== lastHighlightedStep) {
          row.scrollIntoView({ block: 'nearest', inline: 'nearest' });
          lastHighlightedStep = highlightKey;
        }
        return;
      }
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
      delay.disabled = isBusy();
      delay.addEventListener('change', () => send('delay', { index, value: Number(delay.value) }));
      controls.append(delay);
      if (step.type === 'Text') {
        const edit = document.createElement('button');
        edit.className = 'step-delete';
        edit.textContent = '✎';
        edit.title = '编辑文字';
        edit.disabled = isBusy();
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
      remove.disabled = isBusy();
      remove.addEventListener('click', () => send('delete', { index }));
      controls.append(remove);
      row.append(controls);
      container.append(row);
    });
    if (!editable && (state.mode !== 'running' || state.waitingForNextRun)) lastHighlightedStep = '';
  }

  function render() {
    if (state.workspace === 'editor' || state.workspace === 'execution') showView(state.workspace);
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
    $('window-name').textContent = selected ? selected.title : '等待快捷键确认目标窗口';
    document.querySelector('.window-meta small').textContent = selected ? `${selected.process} · 已确认` : '切到要录制的窗口，按快捷键即可开始';
    $('hotkey-select').value = state.script.hotkey;
    if (document.activeElement !== $('script-name')) $('script-name').value = state.script.name;
    $('script-name').disabled = state.mode !== 'ready';
    $('hotkey-select').disabled = isBusy();
    $('nav-editor').disabled = isBusy();
    $('nav-execution').disabled = isBusy();
    $('status-text').textContent = ({ ready: '就绪，等待操作', recording: '正在录制', paused: '录制已暂停', running: '正在执行脚本' })[state.mode] || '错误';
    $('status-hint').textContent = state.message;
    $('status-dot').className = `pulse${state.mode === 'recording' ? ' recording' : state.mode === 'running' ? ' running' : ''}`;
    $('record-button').querySelector('span').textContent = state.pendingRecord ? '取消等待录制' : state.mode === 'recording' ? '录制中' : state.mode === 'paused' ? '继续录制' : '开始录制';
    $('record-button').disabled = state.mode === 'running' || state.mode === 'recording' || state.pendingRun;
    $('finish-button').hidden = state.mode !== 'recording' && state.mode !== 'paused';
    $('add-text').disabled = isBusy() || !(state.script.clientWidth > 0 && state.script.clientHeight > 0);
    $('run-button').disabled = isBusy();
    $('progress-label').textContent = state.waitingForNextRun ? '等待下一轮' : state.mode === 'running' ? '执行中' : state.pendingRun ? '等待目标窗口' : '准备就绪';
    const current = state.mode === 'running' && !state.waitingForNextRun ? state.currentStep + 1 : 0;
    $('progress-fraction').textContent = `${current} / ${state.script.steps.length}`;
    $('progress-fill').style.width = state.script.steps.length ? `${current * 100 / state.script.steps.length}%` : '0%';
    document.querySelector('.help').textContent = state.mode === 'recording'
      ? `按 ${state.script.hotkey} 或点击「结束并保存」即可结束录制并自动保存。`
      : state.pendingRecord ? '切到要录制的窗口后会自动开始；再次点击可取消等待。'
        : state.mode === 'paused' ? `切回录制窗口按 ${state.script.hotkey} 继续，或点击「结束并保存」。`
          : `在要录制的窗口按 ${state.script.hotkey} 开始，录制完成后再按一次自动保存。每次开始都会录制一个新脚本。`;
    $('step-count').textContent = `${state.script.steps.length} 个步骤`;
    renderSteps('steps', state.script.steps, true);
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
    for (const id of ['load-button', 'save-button', 'new-editor-script', 'pause-button', 'refresh-windows',
      'window-select', 'script-name-row', 'plan-summary', 'open-execution', 'add-step']) $(id).hidden = true;
    $('finish-button').hidden = true;
    $('finish-button').textContent = '结束并保存';
    $('run-button').textContent = '开启脚本 →';
    $('add-text').hidden = false;
    $('recording-subtitle').textContent = '在目标窗口按快捷键开始录制，再按一次结束，脚本自动保存。';
    $('recording-target-note').textContent = '在要录制的窗口按快捷键，即可自动确认目标';
    $('run-title').textContent = '录制快捷键';
    $('hotkey-select').setAttribute('aria-label', '设置录制开始与结束快捷键');
    $('recording-shortcut-note').textContent = '按一次开始，再按一次结束并保存';
    document.querySelector('.demo-badge').textContent = 'DESKTOP APP';
    document.querySelector('.sidebar-bottom').innerHTML = '<strong><span class="dot"></span>自动保存</strong>结束录制后，脚本会自动保存在本机的脚本库中。';
    document.querySelector('.footer-note').textContent = '脚本仅在选定窗口位于前台且尺寸一致时执行。';
    document.querySelector('.help').textContent = '在目标窗口按快捷键开始录制，再按一次结束并自动保存。';
    $('hotkey-select').replaceChildren(...['F8', 'F9', 'F10', 'F11'].map(key => {
      const option = document.createElement('option'); option.value = key; option.textContent = key; return option;
    }));
    $('execution-refresh-windows').addEventListener('click', () => send('refresh'));
    $('execution-window-select').addEventListener('change', event => send('select', { value: event.target.value }));
    $('hotkey-select').addEventListener('change', event => send('hotkey', { value: event.target.value }));
    $('execution-hotkey').addEventListener('change', event => send('hotkey', { value: event.target.value }));
    $('nav-editor').addEventListener('click', () => navigate('editor'));
    $('nav-execution').addEventListener('click', () => navigate('execution'));
    $('delete-script').addEventListener('click', () => {
      if (view === 'execution' && !isBusy() && hasSavedScript() && window.confirm(`确定删除「${state.script.name}」吗？此操作无法恢复。`))
        send('deleteScript', { id: state.script.id });
    });
    for (const id of ['mode-once', 'mode-count', 'mode-continuous', 'repeat-count', 'repeat-interval'])
      $(id).addEventListener('change', submitExecution);
    $('execution-start').addEventListener('click', () => send('run'));
    $('record-button').addEventListener('click', () => send('record'));
    $('finish-button').addEventListener('click', () => send('finish'));
    $('run-button').addEventListener('click', () => navigate('execution'));
    $('add-text').addEventListener('click', () => {
      const value = window.prompt('输入回放时要写入的文字');
      if (value) send('text', { value });
    });
    window.chrome.webview.addEventListener('message', event => { state = event.data; render(); });
    showView('editor');
    send('refresh');
  });
})();
