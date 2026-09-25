const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const files = {
  '/': ['FlowKey-UI-Demo.html', 'text/html; charset=utf-8'],
  '/FlowKey-UI-Demo.html': ['FlowKey-UI-Demo.html', 'text/html; charset=utf-8'],
  '/branding.css': ['branding.css', 'text/css; charset=utf-8'],
  '/assets/flowkey.svg': ['assets/flowkey.svg', 'image/svg+xml'],
  '/assets/NotoSerifTC.ttf': ['assets/NotoSerifTC.ttf', 'font/ttf'],
  '/favicon.ico': ['assets/flowkey.ico', 'image/x-icon']
};
function handle(req, res) {
  const file = files[new URL(req.url, 'http://localhost').pathname];
  if (!file) { res.writeHead(404); res.end('Not found'); return; }
  res.setHeader('Content-Type', file[1]);
  res.setHeader('Cache-Control', 'no-store');
  res.end(fs.readFileSync(path.join(root, file[0])));
}
module.exports = {handle};
if (require.main === module) http.createServer(handle).listen(8877, '127.0.0.1');
