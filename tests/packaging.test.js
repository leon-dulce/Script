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
