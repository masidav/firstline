// Serves the web export like a static host would: files as they are, and index.html for every other path (the app
// routes on the client). Usage: node serve.mjs <dir> <port>
import { createReadStream, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize } from 'node:path';

const [dir, port] = process.argv.slice(2);
const types = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.ttf': 'font/ttf',
  '.woff2': 'font/woff2', '.map': 'application/json',
};

createServer((request, response) => {
  const path = normalize(decodeURIComponent(new URL(request.url, 'http://local').pathname)).replace(/^(\.\.[/\\])+/, '');
  let file = join(dir, path);
  try {
    if (!statSync(file).isFile()) throw new Error('not a file');
  } catch {
    file = join(dir, 'index.html');
  }
  response.writeHead(200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream' });
  createReadStream(file).pipe(response);
}).listen(Number(port), '127.0.0.1');
