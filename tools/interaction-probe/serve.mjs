import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const argument = process.argv[2] ?? process.env.PORT ?? '8787';
if (!/^\d+$/.test(argument) || Number(argument) < 1 || Number(argument) > 65535) {
  console.error('用法：node tools/interaction-probe/serve.mjs [1..65535 端口]');
  process.exit(1);
}
const root = dirname(fileURLToPath(import.meta.url));
const files = new Map([
  ['/', ['index.html', 'text/html; charset=utf-8']],
  ['/index.html', ['index.html', 'text/html; charset=utf-8']],
  ['/probe.js', ['probe.js', 'text/javascript; charset=utf-8']],
  ['/core.js', ['core.js', 'text/javascript; charset=utf-8']],
  ['/audit.js', ['audit.js', 'text/javascript; charset=utf-8']],
  ['/probe.css', ['probe.css', 'text/css; charset=utf-8']]
]);
const policy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
const server = createServer(async (request, response) => {
  const headers = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': policy, 'Referrer-Policy': 'no-referrer', 'Cross-Origin-Resource-Policy': 'same-origin' };
  const reject = (status, text, extra = {}) => { response.writeHead(status, { ...headers, 'Content-Type': 'text/plain; charset=utf-8', ...extra }); response.end(request.method === 'HEAD' ? undefined : text); };
  if (request.method !== 'GET' && request.method !== 'HEAD') { request.resume(); return reject(405, 'Method not allowed', { Allow: 'GET, HEAD' }); }
  if (request.headers['transfer-encoding'] || (request.headers['content-length'] && request.headers['content-length'] !== '0')) { request.resume(); return reject(400, 'Request body is not accepted'); }
  // Match raw paths against a fixed allowlist; never resolve caller-controlled paths.
  const path = (request.url || '').split('?')[0];
  const entry = files.get(path);
  if (!entry) return reject(404, 'Not found');
  try {
    const buffer = await readFile(join(root, entry[0]));
    response.writeHead(200, { ...headers, 'Content-Type': entry[1], 'Content-Length': buffer.length });
    response.end(request.method === 'HEAD' ? undefined : buffer);
  } catch { reject(500, 'Static file unavailable'); }
});
server.requestTimeout = 5000;
server.headersTimeout = 5000;
server.on('error', error => { console.error(`无法启动本机实验台：${error.message}`); process.exitCode = 1; });
server.listen(Number(argument), '127.0.0.1', () => console.log(`Superb 本机交互实验台：http://127.0.0.1:${argument}/\n仅提供静态文件，不接收或保存事件日志。Ctrl+C 关闭。`));
