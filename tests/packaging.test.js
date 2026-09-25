const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('Windows executable embeds an administrator manifest without uiAccess', () => {
  const root = path.join(__dirname, '..');
  const project = fs.readFileSync(path.join(root, 'FlowKey.Desktop/FlowKey.Desktop.csproj'), 'utf8');
  const manifest = fs.readFileSync(path.join(root, 'FlowKey.Desktop/app.manifest'), 'utf8');
  assert.match(project, /<ApplicationManifest>app\.manifest<\/ApplicationManifest>/);
  const levels = [...manifest.matchAll(/<requestedExecutionLevel\b[^>]*\/>/g)];
  assert.equal(levels.length, 1);
  assert.match(levels[0][0], /level="requireAdministrator"/);
  assert.match(levels[0][0], /uiAccess="false"/);
  assert.match(manifest, /<trustInfo xmlns="urn:schemas-microsoft-com:asm\.v3">/);
  assert.match(manifest, /<dpiAwareness[^>]*>PerMonitorV2<\/dpiAwareness>/);
});

test('repository does not include the unused IDE sample program', () => {
  const root = path.join(__dirname, '..');
  assert.equal(fs.existsSync(path.join(root, 'main.py')), false);
  assert.equal(fs.existsSync(path.join(root, 'FlowKey.Desktop/FlowKey.Desktop.csproj')), true);
});

test('README explains FlowKey in Traditional Chinese and English with valid local links', () => {
  const root = path.join(__dirname, '..');
  const readme = fs.readFileSync(path.join(root, 'README.md'), 'utf8');
  assert.match(readme, /^# FlowKey\r?\n/m);
  assert.match(readme, /^## 繁體中文\r?\n\r?\n/m);
  assert.match(readme, /^## English\r?\n\r?\n/m);
  assert.match(readme, /錄製鍵盤操作/);
  assert.match(readme, /Keyboard recording/);
  assert.match(readme, /Windows x64/);
  assert.doesNotMatch(readme, /录制|执行|设置|脚本|键盘|电脑/);
  assert.equal((readme.match(/^```/gm) || []).length % 2, 0, 'code fences are paired');
  for (const [, target] of readme.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
    if (/^https?:\/\//.test(target)) continue;
    assert.equal(fs.existsSync(path.join(root, target)), true, `broken README link: ${target}`);
  }
});
