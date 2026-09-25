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
    if (plan.mode === 'Count') return `執行 ${plan.repeatCount} 次 · 每輪間隔 ${plan.intervalMs} 毫秒`;
    if (plan.mode === 'Continuous') return `持續執行 · 每輪間隔 ${plan.intervalMs} 毫秒`;
    return '只執行一次';
  }

  function showView(next) {
    view = next;
    $('workspace-breadcrumb').textContent = { editor: '錄製與編輯', execution: '執行腳本', settings: '設定' }[next];
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
    const flow = state.flow || { autoSwitch: false, returnToApp: false, targetId: '', targetTitle: '', message: '修改後自動儲存，適用於所有腳本。' };
    $('flow-auto').checked = flow.autoSwitch;
    $('flow-manual').checked = !flow.autoSwitch;
    $('flow-return').checked = flow.returnToApp;
    $('flow-stay').checked = !flow.returnToApp;
    for (const id of ['flow-auto', 'flow-manual', 'flow-return', 'flow-stay', 'flow-target', 'flow-refresh']) $(id).disabled = isBusy();
    $('flow-target-panel').hidden = !flow.autoSwitch;
    const picker = $('flow-target');
    picker.replaceChildren();
    const empty = document.createElement('option'); empty.value = ''; empty.textContent = '請選擇已開啟的視窗'; picker.append(empty);
    for (const item of state.windows) { const option = document.createElement('option'); option.value = item.id; option.textContent = item.process + ' · ' + item.title; picker.append(option); }
    picker.value = flow.targetId;
    $('flow-target-note').textContent = flow.targetTitle && !flow.targetId ? '上次選擇：' + flow.targetTitle + '。視窗未找到或有重名，請重新整理後選擇。' : '記住視窗名稱與程式；程式重新開啟後會嘗試重新匹配。';
    $('flow-preview').textContent = '開始執行 → ' + (flow.autoSwitch ? '自動切到' + (flow.targetTitle || '指定視窗（待選擇）') : '等待你切到目標視窗') + ' → 執行腳本 → ' + (flow.returnToApp ? '回到 FlowKey' : '保持當前視窗');
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
      empty.textContent = '尚無已儲存腳本。結束錄製後，為腳本命名並確認儲存，就會出現在這裡。';
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
      detail.textContent = `${script.stepCount} 個步驟 · ${script.mode === 'Continuous' ? '持續' : script.mode === 'Count' ? '指定次數' : '單次'}`;
      button.append(title, detail);
      button.addEventListener('click', () => {
        if (view === 'execution' && !isBusy() && script.id !== state.script.id &&
            (!state.dirty || window.confirm('上次自動儲存失敗。確定切換腳本並放棄尚未儲存的修改嗎？')))
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
    $('execution-script-name').textContent = saved ? state.script.name : '請選擇已儲存的腳本';
    $('execution-target').textContent = selected ? `${selected.process} · ${selected.title} · 已確認` : '在目標視窗按快捷鍵，即可確認視窗並執行';
    const targetSelect = $('execution-window-select');
    targetSelect.replaceChildren();
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '自動使用快捷鍵所在視窗';
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
    $('execution-description').textContent = !saved ? '從左側選擇一個已儲存腳本，即可設定執行方式。' : plan.mode === 'Once'
      ? '腳本將從第一步到最後一步執行一次。'
      : plan.mode === 'Count'
        ? `腳本將執行 ${plan.repeatCount} 輪；每輪完成後等待 ${plan.intervalMs} 毫秒，再開始下一輪。`
        : `腳本將持續迴圈；每輪完成後等待 ${plan.intervalMs} 毫秒。再次按 ${state.script.hotkey} 可停止。`;
    $('execution-hotkey').value = state.script.hotkey;
    $('execution-hotkey').disabled = settingsDisabled;
    const active = state.mode === 'running' || state.pendingRun;
    $('execution-start').disabled = !active && (isBusy() || !saved || !state.script.steps.length);
    $('execution-start').textContent = active ? '■ 停止執行' : '▶ 開始執行';
    $('execution-start').className = `button ${active ? 'danger' : 'primary'}`;
    $('execution-help').textContent = state.pendingRun
      ? (state.switchingWindow ? '正在自動切換視窗，就緒後開始；再次點選可取消。' : '已準備執行，切回目標視窗後開始；再次點選可取消等待。')
      : state.mode === 'running' ? `再按 ${state.script.hotkey} 可停止執行。`
        : !saved ? '請從左側選擇要執行的已儲存腳本。'
          : !state.script.steps.length ? '此腳本沒有可執行的步驟。'
            : `在目標視窗按 ${state.script.hotkey} 開始執行；再次按下停止。也可點選「開始執行」後切回目標視窗。`;
    $('cycle-number').textContent = state.mode === 'running'
      ? `${state.currentIteration}${plan.mode === 'Count' ? ` / ${plan.repeatCount}` : ''}${state.waitingForNextRun ? ' · 等待下一輪' : ''}`
      : '—';
    $('execution-progress-label').textContent = state.waitingForNextRun ? '等待下一輪' : state.mode === 'running' ? '執行中' : state.pendingRun ? '等待目標視窗' : '準備就緒';
    const current = state.mode === 'running' && !state.waitingForNextRun ? state.currentStep + 1 : 0;
    const steps = saved ? state.script.steps : [];
    $('execution-progress-fraction').textContent = `${current} / ${steps.length}`;
    $('execution-progress-fill').style.width = steps.length ? `${current * 100 / steps.length}%` : '0%';
    $('execution-step-count').textContent = `${steps.length} 個步驟`;
    const activeStep = state.mode === 'running' && !state.waitingForNextRun ? steps[state.currentStep] : null;
    $('execution-current-action').textContent = !saved ? '等待選擇腳本'
      : state.waitingForNextRun ? `第 ${state.currentIteration} 輪已完成，等待 ${plan.intervalMs} 毫秒後繼續`
        : activeStep ? `第 ${state.currentStep + 1} 步 · ${stepLabel(activeStep).join('：')}`
          : state.pendingRun ? '等待目標視窗進入前臺'
            : state.mode === 'running' ? '正在開始執行'
              : '等待執行，啟動後會自動跟隨當前步驟';
    if (state.flow?.autoSwitch) {
      $('execution-target').textContent = '自動切換目標：' + (state.flow.targetTitle || '請先到設定頁選擇視窗');
      if (!active) $('execution-help').textContent = '開始後自動切換到指定視窗；結束後' + (state.flow.returnToApp ? '返回 FlowKey。' : '保持當前視窗。');
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
      window.alert('執行次數須為 1–10000，輪次間隔須為 100–60000 毫秒。');
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
      166: '瀏覽器後退', 167: '瀏覽器前進', 168: '瀏覽器重新整理', 169: '瀏覽器停止',
      170: '瀏覽器搜尋', 171: '瀏覽器收藏', 172: '瀏覽器主頁',
      173: '靜音', 174: '降低音量', 175: '提高音量', 176: '下一曲', 177: '上一曲',
      178: '停止媒體', 179: '播放 / 暫停', 180: '郵件', 181: '媒體', 182: '應用 1', 183: '應用 2',
      186: '; / :', 187: '= / +', 188: ', / <', 189: '- / _', 190: '. / >', 191: '/ / ?',
      192: '` / ~', 219: '[ / {', 220: '\\ / |', 221: '] / }', 222: "' / \"", 226: 'OEM 102',
      229: 'IME Process', 231: 'Unicode Packet', 246: 'Attn', 247: 'CrSel', 248: 'ExSel',
      249: 'Erase EOF', 250: 'Play', 251: 'Zoom', 253: 'PA1', 254: 'OEM Clear'
    };
    if (names[key]) return names[key];
    if ((key >= 48 && key <= 57) || (key >= 65 && key <= 90)) return String.fromCharCode(key);
    if (key >= 96 && key <= 105) return `Numpad ${key - 96}`;
    if (key >= 112 && key <= 135) return `F${key - 111}`;
    return `按鍵 0x${key.toString(16).toUpperCase().padStart(2, '0')}`;
  }

  function stepLabel(step) {
    switch (step.type) {
      case 'Click': return ['點選', `${step.button} · (${step.x}, ${step.y})`];
      case 'DoubleClick': return ['雙擊', `${step.button} · (${step.x}, ${step.y})`];
      case 'Scroll': return ['滾動', `${step.wheelDelta > 0 ? '向上' : '向下'} ${Math.abs(step.wheelDelta)} 格`];
      case 'Key': return [step.keyAction === 'Down' ? '按下按鍵' : step.keyAction === 'Up' ? '鬆開按鍵' : '按鍵', step.keys.map(keyName).join(' + ')];
      case 'Text': return ['輸入文字', step.text];
      default: return ['未知步驟', ''];
    }
  }

  function renderSteps(containerId, steps, editable) {
    const container = $(containerId);
    container.replaceChildren();
    if (!steps.length) {
      const empty = document.createElement('div');
      empty.className = 'empty';
      empty.textContent = editable ? '按快捷鍵或「開始錄製」後，可切換到其他視窗。每次按下、鬆開按鍵與間隔都會即時顯示。' : '從左側選擇一個已儲存腳本，檢視完整執行步驟。';
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
        delay.textContent = `間隔 ${step.delayMs} 毫秒`;
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
      interval.textContent = `間隔 ${step.delayMs} 毫秒`;
      controls.append(interval);
      const delay = document.createElement('input');
      delay.type = 'number';
      delay.min = '0';
      delay.max = '2147483647';
      delay.step = '1';
      delay.value = String(step.delayMs);
      delay.className = 'delay';
      delay.style.width = '74px';
      delay.title = '執行前等待毫秒數';
      delay.setAttribute('aria-label', `步驟 ${index + 1} 的等待毫秒數`);
      delay.disabled = isBusy();
      delay.hidden = isBusy();
      delay.addEventListener('change', () => {
        const value = Number(delay.value);
        if (!delay.value.trim() || !Number.isInteger(value) || value < 0 || value > 2147483647) {
          window.alert('步驟間隔須為 0–2147483647 毫秒的整數。');
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
        edit.title = '編輯文字';
        edit.disabled = isBusy();
        edit.hidden = isBusy();
        edit.addEventListener('click', () => {
          const value = window.prompt('編輯輸入文字步驟', step.text);
          if (value) send('text', { index, value });
        });
        controls.append(edit);
      }
      const remove = document.createElement('button');
      remove.className = 'step-delete';
      remove.textContent = '×';
      remove.title = '刪除步驟';
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
      $('recording-name-error').textContent = !name ? '請輸入腳本名稱。' : '腳本名稱不可超過 100 個字元。';
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
    placeholder.textContent = '選擇視窗';
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
    $('status-text').textContent = state.namingRequired ? '錄製結束，等待命名儲存' : ({ ready: '就緒，等待操作', recording: '正在錄製', paused: '錄製已暫停', running: '正在執行腳本' })[state.mode] || '錯誤';
    $('status-hint').textContent = state.message;
    $('restart-admin').hidden = !!state.elevated;
    $('restart-admin').disabled = isBusy();
    $('recording-access-warning').textContent = state.recordingAccessWarning || '';
    $('recording-access-warning').hidden = !state.recordingAccessWarning;
    $('recording-access-warning').style.color = '#ffcf8a';
    $('status-dot').className = `pulse${state.mode === 'recording' ? ' recording' : state.mode === 'running' ? ' running' : ''}`;
    $('add-text').disabled = true;
    $('record-button').disabled = state.namingRequired || state.workspace !== 'editor' || state.mode === 'running';
    $('record-button').querySelector('span').textContent = state.mode === 'recording' ? '停止錄製' : '開始錄製';
    document.querySelector('.help').textContent = state.mode === 'recording'
      ? `再次按 ${state.script.hotkey} 結束錄製，然後為腳本命名並確認儲存。`
      : state.namingRequired ? '錄製已結束，請在對話方塊輸入腳本名稱並確認儲存。'
        : state.mode === 'paused' ? `錄製已暫停。按 ${state.script.hotkey} 結束，然後為腳本命名儲存。`
          : `設定快捷鍵後，按 ${state.script.hotkey} 或點選「開始錄製」。切換視窗會繼續記錄，再按一次結束並命名儲存。`;
    $('step-count').textContent = `${state.script.steps.length} 個步驟`;
    $('record-total').textContent = String(state.script.steps.length);
    $('record-seconds').textContent = (state.script.steps.reduce((sum, step) => sum + step.delayMs, 0) / 1000).toFixed(1);
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
    $('target-title').textContent = '錄製狀態';
    document.querySelector('.card-label').textContent = '01 / 狀態';
    $('recording-subtitle').textContent = '設定快捷鍵或點選開始錄製，自由切換視窗，即時記錄每個按鍵與間隔。';
    $('recording-target-note').textContent = '跨視窗持續錄製鍵盤動作';
    $('run-title').textContent = '按住一次，記錄一次';
    $('hotkey-select').setAttribute('aria-label', '設定錄製開始與結束快捷鍵');
    $('recording-shortcut-note').textContent = '按一次開始，再按一次結束並命名';
    document.querySelector('.sidebar-bottom').innerHTML = '<strong><span class="dot"></span>本地腳本庫</strong>結束錄製後，為腳本命名並確認儲存，即可在執行頁使用。';
    document.querySelector('.footer-note').textContent = '腳本與設定儲存於本機。執行中可再次按快捷鍵停止。';
    document.querySelector('.help').textContent = '按快捷鍵或開始錄製按鈕開始，再按一次結束並命名儲存。';
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
      if (view === 'execution' && !isBusy() && hasSavedScript() && window.confirm(`確定刪除「${state.script.name}」嗎？此操作無法恢復。`))
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
      if (state?.namingRequired && window.confirm('確定放棄本次錄製嗎？本次錄製的步驟將不會儲存。')) send('discardRecording');
    });
    window.chrome.webview.addEventListener('message', event => { state = event.data; render(); });
    showView('editor');
    send('refresh');
  });
})();
