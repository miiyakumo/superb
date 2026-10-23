'use strict';
const { before, after, test } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const http = require('node:http');
const net = require('node:net');
const { join } = require('node:path');
let child, port;
before(async () => {
  const reservation = net.createServer();
  await new Promise(resolve => reservation.listen(0, '127.0.0.1', resolve));
  port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  child = spawn(process.execPath, [join(__dirname, 'serve.mjs'), String(port)], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(Error('local server startup timed out')), 5000);
    child.once('exit', code => { clearTimeout(timeout); reject(Error('server exited: ' + code)); });
    child.stdout.once('data', () => { clearTimeout(timeout); resolve(); });
    child.once('error', error => { clearTimeout(timeout); reject(error); });
  });
});
after(async () => {
  if (child && child.exitCode === null) await new Promise(resolve => { child.once('exit', resolve); child.kill(); });
});
function request(path, method = 'GET', headers = {}, body) {
  return new Promise((resolve, reject) => {
    const req = http.request({ hostname: '127.0.0.1', port, path, method, headers }, response => {
      let output = ''; response.setEncoding('utf8');
      response.on('data', chunk => { output += chunk; });
      response.once('end', () => resolve({ status: response.statusCode, headers: response.headers, body: output }));
    });
    req.once('error', reject); req.end(body);
  });
}
test('GET serves only the expected HTML and own scripts/styles with no-connect CSP', async () => {
  const page = await request('/');
  assert.equal(page.status, 200); assert.match(page.body, /交互痕迹实验台/);
  assert.match(page.headers['content-security-policy'], /connect-src 'none'/);
  assert.equal(page.headers['cache-control'], 'no-store');
  for (const path of ['/index.html', '/core.js', '/audit.js', '/probe.js', '/probe.css']) assert.equal((await request(path)).status, 200);
});
test('HEAD returns headers and no body', async () => {
  const response = await request('/probe.js', 'HEAD');
  assert.equal(response.status, 200); assert.equal(response.body, ''); assert.ok(Number(response.headers['content-length']) > 0);
});
test('missing, directory, encoded and raw traversal paths are denied', async () => {
  for (const path of ['/README.md', '/core.test.cjs', '/serve.mjs', '/unknown', '/../README.md', '/%2e%2e/README.md', '/..%5cREADME.md', '//index.html', '/probe.js/../index.html']) assert.equal((await request(path)).status, 404, path);
});
test('POST and unsupported methods cannot receive browser logs', async () => {
  const response = await request('/', 'POST', { 'Content-Type': 'application/json' }, '{"notStored":true}');
  assert.equal(response.status, 405); assert.equal(response.headers.allow, 'GET, HEAD');
  assert.equal((await request('/', 'DELETE')).status, 405);
});
test('even GET request bodies are rejected', async () => {
  assert.equal((await request('/', 'GET', { 'Content-Length': '1' }, 'x')).status, 400);
  assert.equal((await request('/', 'GET', { 'Transfer-Encoding': 'chunked' }, 'x')).status, 400);
});
