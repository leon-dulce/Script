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
  assert.match(readme, /目前版本：\*\*1\.0\.1 Stable\*\*/);
  assert.match(readme, /Current release: \*\*1\.0\.1 Stable\*\*/);
  assert.match(readme, /^## 繁體中文\r?\n\r?\n/m);
  assert.match(readme, /^## English\r?\n\r?\n/m);
  assert.match(readme, /錄製鍵盤操作/);
  assert.match(readme, /Keyboard recording/);
  assert.match(readme, /Windows x64/);
  assert.match(readme, /FlowKey\.exe/);
  assert.doesNotMatch(readme, /FlowKey 1\.1X1\.exe/);
  assert.match(readme, /自動下載及安裝/);
  assert.match(readme, /downloads and installs WebView2/);
  assert.doesNotMatch(readme, /FlowKey-UI-Demo/);
  assert.doesNotMatch(readme, /录制|执行|设置|脚本|键盘|电脑/);
  assert.equal((readme.match(/^```/gm) || []).length % 2, 0, 'code fences are paired');
  for (const [, target] of readme.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
    if (/^https?:\/\//.test(target)) continue;
    assert.equal(fs.existsSync(path.join(root, target)), true, `broken README link: ${target}`);
  }
});

test('desktop release metadata and title identify 1.0.1 Stable', () => {
  const root = path.join(__dirname, '..');
  const project = fs.readFileSync(path.join(root, 'FlowKey.Desktop/FlowKey.Desktop.csproj'), 'utf8');
  const window = fs.readFileSync(path.join(root, 'FlowKey.Desktop/MainWindow.xaml'), 'utf8');
  for (const value of ['<Version>1.0.1</Version>', '<Product>FlowKey</Product>',
    '<AssemblyVersion>1.0.1.0</AssemblyVersion>', '<FileVersion>1.0.1.0</FileVersion>',
    '<InformationalVersion>1.0.1 Stable</InformationalVersion>'])
    assert.ok(project.includes(value), `missing release metadata: ${value}`);
  assert.match(window, /Title="FlowKey 1\.0\.1 Stable"/);
});

test('root executable is a real Windows application stored with Git LFS', () => {
  const root = path.join(__dirname, '..');
  const executable = path.join(root, 'FlowKey.exe');
  const stat = fs.statSync(executable);
  assert.ok(stat.size > 1_000_000, 'root executable must contain the self-contained application, not an LFS pointer');
  const handle = fs.openSync(executable, 'r');
  try {
    const magic = Buffer.alloc(2);
    assert.equal(fs.readSync(handle, magic, 0, 2, 0), 2);
    assert.equal(magic.toString('ascii'), 'MZ');
  } finally { fs.closeSync(handle); }
  assert.match(fs.readFileSync(path.join(root, '.gitattributes'), 'utf8'), /^FlowKey\.exe filter=lfs/m);
  assert.doesNotMatch(fs.readFileSync(path.join(root, '.gitignore'), 'utf8'), /^\/FlowKey\.exe$/m);
});
