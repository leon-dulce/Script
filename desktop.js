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
  let lastRecordedStep = '';

  const isBusy = () => !state || state.mode !== 'ready' || state.pendingRecord || state.pendingRun || state.namingRequired;
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
    $('settings-view').hidden = next !== 'settings';
    $('sidebar-library').hidden = next !== 'execution';
    for (const [name, id] of [['editor', 'nav-editor'], ['execution', 'nav-execution'], ['settings', 'nav-settings']]) {
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

  function renderFlowSettings() {
    const flow = state.flow || { autoSwitch: false, returnToApp: false, targetId: '', targetTitle: '', message: '修改后自动保存，适用于所有脚本。' };
    $('flow-auto').checked = flow.autoSwitch;
    $('flow-manual').checked = !flow.autoSwitch;
    $('flow-return').checked = flow.returnToApp;
    $('flow-stay').checked = !flow.returnToApp;
    for (const id of ['flow-auto', 'flow-manual', 'flow-return', 'flow-stay', 'flow-target', 'flow-refresh']) $(id).disabled = isBusy();
    $('flow-target-panel').hidden = !flow.autoSwitch;
    const picker = $('flow-target');
    picker.replaceChildren();
    const empty = document.createElement('option'); empty.value = ''; empty.textContent = '请选择已打开的窗口'; picker.append(empty);
    for (const item of state.windows) { const option = document.createElement('option'); option.value = item.id; option.textContent = item.process + ' · ' + item.title; picker.append(option); }
    picker.value = flow.targetId;
    $('flow-target-note').textContent = flow.targetTitle && !flow.targetId ? '上次选择：' + flow.targetTitle + '。窗口未找到或有重名，请刷新后选择。' : '记住窗口名称与程序；程序重新打开后会尝试重新匹配。';
    $('flow-preview').textContent = '开始执行 → ' + (flow.autoSwitch ? '自动切到' + (flow.targetTitle || '指定窗口（待选择）') : '等待你切到目标窗口') + ' → 运行脚本 → ' + (flow.returnToApp ? '回到 FlowKey' : '保持当前窗口');
    $('flow-save-status').textContent = flow.message;
  }

  function submitFlowSettings(targetChanged = false) {
    if (view !== 'settings' || isBusy()) return;
    const fields = { autoSwitch: !!$('flow-auto').checked, returnToApp: !!$('flow-return').checked };
    if (targetChanged) fields.targetId = $('flow-target').value;
    send('flowSettings', fields);
  }

  function renderLibrary() {
    const library = $('saved-scripts');
    library.replaceChildren();
    $('library-count').textContent = String(state.savedScripts.length);
    if (!state.savedScripts.length) {
      const empty = document.createElement('div');
      empty.className = 'library-empty';
      empty.textContent = '尚无已保存脚本。结束录制后，为脚本命名并确认保存，就会出现在这里。';
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
    targetSelect.disabled = settingsDisabled || !!state.flow?.autoSwitch;
    $('execution-optional-target').hidden = !!state.flow?.autoSwitch;
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
      ? (state.switchingWindow ? '正在自动切换窗口，就绪后开始；再次点击可取消。' : '已准备执行，切回目标窗口后开始；再次点击可取消等待。')
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
    if (state.flow?.autoSwitch) {
      $('execution-target').textContent = '自动切换目标：' + (state.flow.targetTitle || '请先到设置页选择窗口');
      if (!active) $('execution-help').textContent = '开始后自动切换到指定窗口；结束后' + (state.flow.returnToApp ? '返回 FlowKey。' : '保持当前窗口。');
    }
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

  function keyName(key) {
    const names = {
      3: 'Cancel', 8: 'Backspace', 9: 'Tab', 12: 'Clear', 13: 'Enter',
      16: 'Shift', 17: 'Ctrl', 18: 'Alt', 19: 'Pause', 20: 'Caps Lock',
      21: 'Kana', 23: 'Junja', 24: 'Final', 25: 'Hanja', 27: 'Esc',
      28: 'Convert', 29: 'NonConvert', 30: 'Accept', 31: 'Mode Change',
      32: 'Space', 33: 'Page Up', 34: 'Page Down', 35: 'End', 36: 'Home',
      37: '←', 38: '↑', 39: '→', 40: '↓', 41: 'Select', 42: 'Print',
      43: 'Execute', 44: 'Print Screen', 45: 'Insert', 46: 'Delete', 47: 'Help',
      91: '左 Win', 92: '右 Win', 93: 'Menu', 95: 'Sleep',
      106: 'Numpad *', 107: 'Numpad +', 108: 'Numpad 分隔符', 109: 'Numpad -',
      110: 'Numpad .', 111: 'Numpad /', 144: 'Num Lock', 145: 'Scroll Lock',
      160: '左 Shift', 161: '右 Shift', 162: '左 Ctrl', 163: '右 Ctrl', 164: '左 Alt', 165: '右 Alt',
      166: '浏览器后退', 167: '浏览器前进', 168: '浏览器刷新', 169: '浏览器停止',
      170: '浏览器搜索', 171: '浏览器收藏', 172: '浏览器主页',
      173: '静音', 174: '降低音量', 175: '提高音量', 176: '下一曲', 177: '上一曲',
      178: '停止媒体', 179: '播放 / 暂停', 180: '邮件', 181: '媒体', 182: '应用 1', 183: '应用 2',
      186: '; / :', 187: '= / +', 188: ', / <', 189: '- / _', 190: '. / >', 191: '/ / ?',
      192: '` / ~', 219: '[ / {', 220: '\\ / |', 221: '] / }', 222: "' / \"", 226: 'OEM 102',
      229: 'IME Process', 231: 'Unicode Packet', 246: 'Attn', 247: 'CrSel', 248: 'ExSel',
      249: 'Erase EOF', 250: 'Play', 251: 'Zoom', 253: 'PA1', 254: 'OEM Clear'
    };
    if (names[key]) return names[key];
    if ((key >= 48 && key <= 57) || (key >= 65 && key <= 90)) return String.fromCharCode(key);
    if (key >= 96 && key <= 105) return `Numpad ${key - 96}`;
    if (key >= 112 && key <= 135) return `F${key - 111}`;
    return `按键 0x${key.toString(16).toUpperCase().padStart(2, '0')}`;
  }

  function stepLabel(step) {
    switch (step.type) {
      case 'Click': return ['点击', `${step.button} · (${step.x}, ${step.y})`];
      case 'DoubleClick': return ['双击', `${step.button} · (${step.x}, ${step.y})`];
      case 'Scroll': return ['滚动', `${step.wheelDelta > 0 ? '向上' : '向下'} ${Math.abs(step.wheelDelta)} 格`];
      case 'Key': return [step.keyAction === 'Down' ? '按下按键' : step.keyAction === 'Up' ? '松开按键' : '按键', step.keys.map(keyName).join(' + ')];
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
      empty.textContent = editable ? '按快捷键或「开始录制」后，可切换到其他窗口。每次按下、松开按键与间隔都会即时显示。' : '从左侧选择一个已保存脚本，查看完整执行步骤。';
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
        delay.textContent = `间隔 ${step.delayMs} 毫秒`;
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
      const interval = document.createElement('span');
      interval.className = 'step-interval';
      interval.textContent = `间隔 ${step.delayMs} 毫秒`;
      controls.append(interval);
      const delay = document.createElement('input');
      delay.type = 'number';
      delay.min = '0';
      delay.max = '2147483647';
      delay.step = '1';
      delay.value = String(step.delayMs);
      delay.className = 'delay';
      delay.style.width = '74px';
      delay.title = '执行前等待毫秒数';
      delay.setAttribute('aria-label', `步骤 ${index + 1} 的等待毫秒数`);
      delay.disabled = isBusy();
      delay.hidden = isBusy();
      delay.addEventListener('change', () => {
        const value = Number(delay.value);
        if (!delay.value.trim() || !Number.isInteger(value) || value < 0 || value > 2147483647) {
          window.alert('步骤间隔须为 0–2147483647 毫秒的整数。');
          delay.value = String(step.delayMs);
          return;
        }
        send('delay', { index, value });
      });
      controls.append(delay);
      if (step.type === 'Text') {
        const edit = document.createElement('button');
        edit.className = 'step-delete';
        edit.textContent = '✎';
        edit.title = '编辑文字';
        edit.disabled = isBusy();
        edit.hidden = isBusy();
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
      remove.hidden = isBusy();
      remove.addEventListener('click', () => send('delete', { index }));
      controls.append(remove);
      row.append(controls);
      container.append(row);
      if (state.mode === 'recording' && view === 'editor' && index === steps.length - 1) {
        const recordingKey = `${state.script.id}:${steps.length}`;
        if (recordingKey !== lastRecordedStep) {
          row.scrollIntoView({ block: 'nearest', inline: 'nearest' });
          lastRecordedStep = recordingKey;
        }
      }
    });
    if (editable && !steps.length) lastRecordedStep = '';
    if (!editable && (state.mode !== 'running' || state.waitingForNextRun)) lastHighlightedStep = '';
  }

  function renderNaming() {
    const dialog = $('recording-name-dialog');
    if (!state.namingRequired) {
      if (dialog.open) dialog.close();
      return;
    }
    if (!dialog.open) {
      $('recording-name-input').value = state.script.name || '';
      dialog.showModal();
    }
    $('recording-name-error').textContent = state.namingError || '';
  }

  function saveRecording() {
    if (!state?.namingRequired) return;
    const name = $('recording-name-input').value.trim();
    if (!name || name.length > 100) {
      $('recording-name-error').textContent = !name ? '请输入脚本名称。' : '脚本名称不可超过 100 个字符。';
      return;
    }
    $('recording-name-error').textContent = '';
    send('saveRecording', { name });
  }

  function render() {
    if (['editor', 'execution', 'settings'].includes(state.workspace)) showView(state.workspace);
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
    $('hotkey-select').value = state.script.hotkey;
    if (document.activeElement !== $('script-name')) $('script-name').value = state.script.name;
    $('script-name').disabled = state.mode !== 'ready';
    $('hotkey-select').disabled = isBusy();
    $('nav-editor').disabled = isBusy();
    $('nav-settings').disabled = isBusy();
    renderFlowSettings();
    $('nav-execution').disabled = isBusy();
    $('status-text').textContent = state.namingRequired ? '录制结束，等待命名保存' : ({ ready: '就绪，等待操作', recording: '正在录制', paused: '录制已暂停', running: '正在执行脚本' })[state.mode] || '错误';
    $('status-hint').textContent = state.message;
    $('restart-admin').hidden = !!state.elevated;
    $('restart-admin').disabled = isBusy();
    $('recording-access-warning').textContent = state.recordingAccessWarning || '';
    $('recording-access-warning').hidden = !state.recordingAccessWarning;
    $('recording-access-warning').style.color = '#ffcf8a';
    $('status-dot').className = `pulse${state.mode === 'recording' ? ' recording' : state.mode === 'running' ? ' running' : ''}`;
    $('add-text').disabled = true;
    $('record-button').disabled = state.namingRequired || state.workspace !== 'editor' || state.mode === 'running';
    $('record-button').querySelector('span').textContent = state.mode === 'recording' ? '停止录制' : '开始录制';
    document.querySelector('.help').textContent = state.mode === 'recording'
      ? `再次按 ${state.script.hotkey} 结束录制，然后为脚本命名并确认保存。`
      : state.namingRequired ? '录制已结束，请在对话框输入脚本名称并确认保存。'
        : state.mode === 'paused' ? `录制已暂停。按 ${state.script.hotkey} 结束，然后为脚本命名保存。`
          : `设置快捷键后，按 ${state.script.hotkey} 或点击「开始录制」。切换窗口会继续记录，再按一次结束并命名保存。`;
    $('step-count').textContent = `${state.script.steps.length} 个步骤`;
    renderSteps('steps', state.script.steps, true);
    renderLibrary();
    renderExecution(selected);
    renderNaming();
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
      'window-select', 'script-name-row', 'plan-summary', 'open-execution', 'add-step',
      'run-button', 'recording-progress', 'finish-button', 'recording-target-config', 'recording-window-card', 'add-text']) $(id).hidden = true;
    $('record-button').hidden = false;
    $('target-title').textContent = '录制状态';
    document.querySelector('.card-label').textContent = '01 / 状态';
    $('recording-subtitle').textContent = '设置快捷键或点击开始录制，自由切换窗口，实时记录每个按键与间隔。';
    $('recording-target-note').textContent = '跨窗口持续录制键盘动作';
    $('run-title').textContent = '录制快捷键';
    $('hotkey-select').setAttribute('aria-label', '设置录制开始与结束快捷键');
    $('recording-shortcut-note').textContent = '按一次开始，再按一次结束并命名';
    document.querySelector('.demo-badge').textContent = 'DESKTOP APP';
    document.querySelector('.sidebar-bottom').innerHTML = '<strong><span class="dot"></span>本地脚本库</strong>结束录制后，为脚本命名并确认保存，即可在执行页使用。';
    document.querySelector('.footer-note').textContent = '脚本仅在选定窗口位于前台且尺寸一致时执行。';
    document.querySelector('.help').textContent = '按快捷键或开始录制按钮开始，再按一次结束并命名保存。';
    $('hotkey-select').replaceChildren(...['F8', 'F9', 'F10', 'F11'].map(key => {
      const option = document.createElement('option'); option.value = key; option.textContent = key; return option;
    }));
    $('execution-refresh-windows').addEventListener('click', () => send('refresh'));
    $('execution-window-select').addEventListener('change', event => send('select', { value: event.target.value }));
    $('hotkey-select').addEventListener('change', event => send('hotkey', { value: event.target.value }));
    $('record-button').addEventListener('click', () => send('record'));
    $('restart-admin').addEventListener('click', () => { if (!isBusy()) send('restartAdmin'); });
    $('execution-hotkey').addEventListener('change', event => send('hotkey', { value: event.target.value }));
    $('nav-editor').addEventListener('click', () => navigate('editor'));
    $('nav-execution').addEventListener('click', () => navigate('execution'));
    $('nav-settings').addEventListener('click', () => navigate('settings'));
    for (const id of ['flow-auto', 'flow-manual', 'flow-return', 'flow-stay']) $(id).addEventListener('change', () => submitFlowSettings());
    $('flow-target').addEventListener('change', () => submitFlowSettings(true));
    $('flow-refresh').addEventListener('click', () => { if (!isBusy()) send('refresh'); });
    $('delete-script').addEventListener('click', () => {
      if (view === 'execution' && !isBusy() && hasSavedScript() && window.confirm(`确定删除「${state.script.name}」吗？此操作无法恢复。`))
        send('deleteScript', { id: state.script.id });
    });
    for (const id of ['mode-once', 'mode-count', 'mode-continuous', 'repeat-count', 'repeat-interval'])
      $(id).addEventListener('change', submitExecution);
    $('execution-start').addEventListener('click', () => send('run'));
    $('recording-name-dialog').addEventListener('cancel', event => event.preventDefault());
    $('recording-name-confirm').addEventListener('click', saveRecording);
    $('recording-name-input').addEventListener('keydown', event => {
      if (event.key === 'Enter') { event.preventDefault(); saveRecording(); }
    });
    $('recording-name-discard').addEventListener('click', () => {
      if (state?.namingRequired && window.confirm('确定放弃本次录制吗？本次录制的步骤将不会保存。')) send('discardRecording');
    });
    window.chrome.webview.addEventListener('message', event => { state = event.data; render(); });
    showView('editor');
    send('refresh');
  });
})();
