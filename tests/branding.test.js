const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');

test('demo server serves image, font and stylesheet resources with correct MIME types', () => {
  const {handle} = require('./preview-demo.cjs');
  for (const [url, type, prefix] of [['/assets/flowkey.svg','image/svg+xml','<svg'],['/branding.css','text/css; charset=utf-8','@font-face'],['/assets/NotoSerifTC.ttf','font/ttf',null]]) {
    const headers = {}; let body;
    handle({url},{setHeader:(k,v)=>headers[k]=v,end:v=>body=v});
    assert.equal(headers['Content-Type'],type);
    assert.ok(body.length>0);
    if (prefix) assert.ok(body.toString().startsWith(prefix));
  }
  let status;
  handle({url:'/missing.svg'}, {writeHead:value=>status=value,end:()=>{}});
  assert.equal(status,404,'Missing images must not silently return HTML');
});

test('production and demo share an offline logo and readable serif heading typography', () => {
  for (const file of ['index.html','FlowKey-UI-Demo.html']) {
    const html = read(file);
    assert.doesNotMatch(html, /rem-theme|rem-character|rem-banner|雷姆/);
    assert.match(html, /lang="zh-Hant"/);
    assert.match(html, /href="branding.css"/);
    const encoded = html.match(/src="data:image\/svg\+xml;base64,([^"]+)"/);
    assert.ok(encoded, 'Logo is embedded so a preview URL or moved HTML cannot break its image path');
    assert.equal(Buffer.from(encoded[1], 'base64').toString('utf8').replace(/\r\n/g, '\n'), read('assets/flowkey.svg').replace(/\r\n/g, '\n'));
  }
  const css = read('branding.css');
  assert.match(read('index.html'), /href="desktop-ui.css"/);
  assert.match(read('desktop-ui.css'), /--accent:#465fff/);
  assert.match(css, /font-display:swap/);
  assert.match(css, /Microsoft JhengHei/);
  assert.match(css, /h1,h2,.card-title,.brand/);
  assert.doesNotMatch(css, /https?:/);
  const font = fs.readFileSync(path.join(root, 'assets/NotoSerifTC.ttf'));
  assert.equal(font.readUInt32BE(0), 0x00010000);
  assert.match(read('assets/OFL-NotoSerifTC.txt'), /SIL OPEN FONT LICENSE/);
});

test('executable and window use the same multi-resolution icon built from the UI SVG', () => {
  assert.match(read('FlowKey.Desktop/FlowKey.Desktop.csproj'), /<ApplicationIcon>..\/assets\/flowkey.ico<\/ApplicationIcon>/);
  assert.match(read('FlowKey.Desktop/MainWindow.xaml'), /Icon="Assets\/flowkey.ico"/);
  const ico = fs.readFileSync(path.join(root, 'assets/flowkey.ico'));
  assert.equal(ico.readUInt16LE(2), 1);
  const sizes = [];
  for (let i = 0; i < ico.readUInt16LE(4); i++) {
    const offset = 6 + i * 16;
    sizes.push(ico[offset] || 256);
    const length = ico.readUInt32LE(offset + 8), start = ico.readUInt32LE(offset + 12);
    assert.ok(start + length <= ico.length && length > 0);
  }
  assert.deepEqual(sizes, [16,20,24,32,40,48,64,128,256]);
  assert.match(read('assets/generate_icon.py'), /ET.parse\(root \/ 'flowkey.svg'\)/);
});
