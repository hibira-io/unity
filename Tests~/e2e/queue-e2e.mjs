// Cross-play matchmaking test: the package's C# HibiraQueue (run with
// `dotnet run -- e2e-queue`) waits in one queue with two @hibira-io/lobby
// players on a real server. Same env as e2e.mjs:
//
//   E2E_HOST=127.0.0.1 E2E_PORT=6001 E2E_TLS=0 \
//   E2E_APP_ID=... E2E_KEY=... E2E_SECRET=... npm run test:queue
//
// Needs @hibira-io/lobby 0.2 or later: `npm install --no-save @hibira-io/lobby@^0.2`.
//
// The Unity player (rating 1000) joins first and hosts the queue. A JS player
// rated 5000 must be left waiting, and the JS player rated 1030 must be
// matched with Unity and meet it in the match room.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import PusherClient from 'pusher-js';
import PusherServer from 'pusher';
import { Queue } from '@hibira-io/lobby';
import { authorizeQueue, authorizeRoom } from '@hibira-io/lobby/server';

const env = process.env;
for (const k of ['E2E_APP_ID', 'E2E_KEY', 'E2E_SECRET']) {
  if (!env[k]) {
    console.error(`set ${k}`);
    process.exit(2);
  }
}
const host = env.E2E_HOST ?? '127.0.0.1';
const port = Number(env.E2E_PORT ?? 6001);
const tls = env.E2E_TLS === '1';
const hibira = new PusherServer({ appId: env.E2E_APP_ID, key: env.E2E_KEY, secret: env.E2E_SECRET, host, port: String(port), useTLS: tls });
const channel = `presence-queue-E2E${Date.now().toString(36).toUpperCase()}`;

function sign({ socketId, channel: ch, userId, name, rating }) {
  const req = { socketId, channel: ch, userId, userInfo: { name } };
  return ch.startsWith('presence-queue-') ? authorizeQueue(hibira, { ...req, rating }) : authorizeRoom(hibira, req);
}

// The auth endpoint the C# client posts to.
const auth = createServer((req, res) => {
  let body = '';
  req.on('data', (c) => (body += c));
  req.on('end', async () => {
    const form = new URLSearchParams(body);
    try {
      const signed = await sign({
        socketId: form.get('socket_id'),
        channel: form.get('channel_name'),
        userId: form.get('user_id'),
        name: form.get('name'),
        rating: Number(form.get('rating')),
      });
      res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(signed));
    } catch (err) {
      res.writeHead(500, { 'content-type': 'application/json' }).end(JSON.stringify({ error: err.message }));
    }
  });
});
await new Promise((r) => auth.listen(0, '127.0.0.1', r));
const authUrl = `http://127.0.0.1:${auth.address().port}/auth`;

const fail = (msg) => {
  console.error(`js: FAIL ${msg}`);
  process.exit(1);
};
const timeout = setTimeout(() => fail('timed out'), 40000);

// ---- C# side ------------------------------------------------------------------
const testsDir = fileURLToPath(new URL('../Hibira.Tests/', import.meta.url));
const unity = spawn('dotnet', ['run', '--', 'e2e-queue'], {
  cwd: testsDir,
  env: { ...env, E2E_AUTH_URL: authUrl, E2E_CHANNEL: channel },
  stdio: ['ignore', 'pipe', 'inherit'],
});
let onJoined;
const unityJoined = new Promise((r) => (onJoined = r));
let buf = '';
unity.stdout.on('data', (d) => {
  buf += d;
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    console.log(`c#: ${line}`);
    if (line === 'JOINED') onJoined();
  }
});
const unityExit = new Promise((r) => unity.on('exit', r));

// ---- JS side ------------------------------------------------------------------
function player(userId, name, rating) {
  return new PusherClient(env.E2E_KEY, {
    cluster: 'mt1',
    wsHost: host,
    wsPort: port,
    wssPort: port,
    forceTLS: tls,
    enabledTransports: ['ws'],
    channelAuthorization: {
      customHandler: ({ socketId, channelName }, cb) =>
        sign({ socketId, channel: channelName, userId, name, rating }).then((a) => cb(null, a), (e) => cb(e, null)),
    },
  });
}

await unityJoined;
await new Promise((r) => setTimeout(r, 300));
const far = player('u-js-far', 'Fay', 5000);
const farQueue = Queue.join(far, channel, { size: 2, ratingRange: 100, timeout: 30000 });
farQueue.on('match', (m) => fail(`the far player was matched with ${m.players.map((p) => p.id)}`));
await new Promise((r) => setTimeout(r, 500));

const near = player('u-js-near', 'Bea', 1030);
const nearQueue = Queue.join(near, channel, { size: 2, ratingRange: 100, timeout: 30000 });
const match = await new Promise((resolve) => nearQueue.on('match', resolve));
const ids = match.players.map((p) => p.id).sort().join(',');
if (ids !== 'u-js-near,u-unity') fail(`near player matched with ${ids}`);
console.log(`js: matched ${ids} in ${match.channel}`);
await new Promise((resolve) => {
  const seen = () => match.room.players.some((p) => p.id === 'u-unity') && resolve();
  match.room.on('ready', seen);
  match.room.on('members', seen);
});
console.log('js: met the Unity player in the match room');
if (farQueue.status !== 'waiting') fail(`far player status ${farQueue.status}`);

const code = await unityExit;
clearTimeout(timeout);
farQueue.leave();
match.room.leave();
far.disconnect();
near.disconnect();
auth.close();
if (code !== 0) fail(`C# side exited ${code}`);
console.log('PASS: Unity hosted the queue and matched a @hibira-io/lobby player by rating');
process.exit(0);
