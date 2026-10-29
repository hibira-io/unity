// Runs Plugins/WebGL/HibiraWebSocket.jslib outside Unity: a stand-in for the
// Emscripten helpers it uses, and Node's WebSocket (the browser's API).
//
//   npm ci && npm test                      # no server needed
//   E2E_KEY=... E2E_PORT=6001 npm test      # also against a Pusher-compatible server
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { WebSocketServer } from 'ws';

function loadLibrary() {
  const heap = new Map();
  let next = 8;
  const lib = {};
  const g = {
    LibraryManager: { library: lib },
    mergeInto: (target, source) => Object.assign(target, source),
    autoAddDeps: () => {},
    UTF8ToString: (ptr) => heap.get(ptr),
    lengthBytesUTF8: (s) => Buffer.byteLength(s, 'utf8'),
    _malloc: () => (next += 8),
    stringToUTF8: (s, ptr) => heap.set(ptr, s),
  };
  const src = readFileSync(new URL('../../Runtime/Plugins/WebGL/HibiraWebSocket.jslib', import.meta.url), 'utf8');
  new Function(...Object.keys(g), src)(...Object.values(g));
  // Emscripten puts $-prefixed deps in scope as globals.
  globalThis.HibiraWS = lib.$HibiraWS;
  const str = (s) => {
    const p = (next += 8);
    heap.set(p, s);
    return p;
  };
  return {
    open: (url) => lib.HibiraWS_Open(str(url)),
    send: (id, text) => lib.HibiraWS_Send(id, str(text)),
    close: (id, code, reason) => lib.HibiraWS_Close(id, code, str(reason)),
    poll: (id) => {
      const p = lib.HibiraWS_Poll(id);
      return p === 0 ? null : heap.get(p);
    },
    free: (id) => lib.HibiraWS_Free(id),
    sockets: () => lib.$HibiraWS.sockets,
  };
}

// A local WebSocket server; onConnection(sock) gets sendText/close/onText/onClose.
async function startServer(onConnection) {
  const wss = new WebSocketServer({ host: '127.0.0.1', port: 0 });
  await new Promise((r) => wss.on('listening', r));
  wss.on('connection', (raw) => {
    const sock = {
      sendText: (t) => raw.send(t),
      close: (code, reason) => raw.close(code, reason),
      onText: () => {},
      onClose: () => {},
    };
    raw.on('message', (d) => sock.onText(d.toString('utf8')));
    raw.on('close', (code) => sock.onClose(code));
    onConnection(sock);
  });
  return {
    url: `ws://127.0.0.1:${wss.address().port}/`,
    stop: () =>
      new Promise((r) => {
        for (const c of wss.clients) c.terminate();
        wss.close(r);
      }),
  };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function next(ws, id, ms = 3000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const e = ws.poll(id);
    if (e !== null) return e;
    await sleep(5);
  }
  throw new Error('no event');
}

test('open, messages, server close', async () => {
  const server = await startServer((sock) => {
    sock.sendText('hello');
    sock.onText = (t) => {
      if (t === 'bye') sock.close(4200, 'later');
      else sock.sendText(`echo:${t}`);
    };
  });
  const ws = loadLibrary();
  const id = ws.open(server.url);
  assert.equal(ws.send(id, 'too early'), 0);
  assert.equal(await next(ws, id), 'o');
  assert.equal(await next(ws, id), 'mhello');
  assert.equal(ws.send(id, 'héllo 日本'), 1);
  assert.equal(await next(ws, id), 'mecho:héllo 日本');
  ws.send(id, 'bye');
  assert.equal(await next(ws, id), 'c4200|later');
  await sleep(50);
  assert.equal(ws.poll(id), null);
  ws.free(id);
  assert.equal(ws.sockets()[id], undefined);
  assert.equal(ws.poll(id), null);
  await server.stop();
});

test('answers server pings without the game', async () => {
  let pong = null;
  const server = await startServer((sock) => {
    sock.onText = (t) => (pong = t);
    sock.sendText('{"event":"pusher:ping","data":{}}');
    sock.sendText('{"event":"other","data":"pusher:ping"}');
  });
  const ws = loadLibrary();
  const id = ws.open(server.url);
  assert.equal(await next(ws, id), 'o');
  assert.equal(await next(ws, id), 'm{"event":"other","data":"pusher:ping"}');
  for (let i = 0; i < 100 && pong === null; i++) await sleep(10);
  assert.equal(pong, '{"event":"pusher:pong","data":{}}');
  ws.free(id);
  await server.stop();
});

test('local close is reported at once, with its code', async () => {
  let serverSaw = null;
  const server = await startServer((sock) => {
    sock.onClose = (code) => (serverSaw = code);
  });
  const ws = loadLibrary();
  const id = ws.open(server.url);
  assert.equal(await next(ws, id), 'o');
  ws.close(id, 4201, 'pong timeout');
  assert.equal(ws.poll(id), 'c4201|pong timeout');
  assert.equal(ws.send(id, 'x'), 0);
  await sleep(100);
  assert.equal(ws.poll(id), null, 'nothing after close');
  assert.equal(serverSaw, 4201);
  ws.free(id);
  await server.stop();
});

test('a refused connection closes with 1006', async () => {
  const gone = await startServer(() => {});
  await gone.stop();
  const ws = loadLibrary();
  const id = ws.open(gone.url);
  assert.equal(await next(ws, id), 'c1006|');
  const bad = ws.open('not a url');
  assert.match(await next(ws, bad), /^c1006\|/);
});

const key = process.env.E2E_KEY;
test('speaks Pusher with a real server', { skip: !key && 'set E2E_KEY' }, async () => {
  const host = process.env.E2E_HOST ?? '127.0.0.1';
  const port = process.env.E2E_PORT ?? '6001';
  const ws = loadLibrary();
  const id = ws.open(`ws://${host}:${port}/app/${key}?protocol=7&client=unity&version=0.1.0`);
  assert.equal(await next(ws, id), 'o');
  const established = JSON.parse((await next(ws, id)).slice(1));
  assert.equal(established.event, 'pusher:connection_established');
  ws.send(id, JSON.stringify({ event: 'pusher:subscribe', data: { channel: 'webgl-test' } }));
  const sub = JSON.parse((await next(ws, id)).slice(1));
  assert.equal(sub.event, 'pusher_internal:subscription_succeeded');
  assert.equal(sub.channel, 'webgl-test');
  ws.send(id, JSON.stringify({ event: 'pusher:ping', data: {} }));
  assert.equal(JSON.parse((await next(ws, id)).slice(1)).event, 'pusher:pong');
  ws.close(id, 1000, '');
  ws.free(id);
});
