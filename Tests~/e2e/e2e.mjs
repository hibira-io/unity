// Cross-play test: the package's C# client (run with `dotnet run -- e2e`) and
// a browser-style @hibira-io/lobby player share one room on a real server.
//
//   E2E_HOST=127.0.0.1 E2E_PORT=6001 E2E_TLS=0 \
//   E2E_APP_ID=... E2E_KEY=... E2E_SECRET=... npm test
//
// The app needs client events turned on. This script serves the auth
// endpoint (authorizeRoom from @hibira-io/lobby/server), starts the C# side,
// and plays the JS side. Exits 0 when both sides saw everything.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import PusherClient from 'pusher-js';
import PusherServer from 'pusher';
import { Room } from '@hibira-io/lobby';
import { authorizeRoom, RoomFullError } from '@hibira-io/lobby/server';

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
const channel = `presence-room-E2E${Date.now().toString(36).toUpperCase()}`;

// The auth endpoint the C# client posts to, as nextjs-lobby's would.
const auth = createServer((req, res) => {
  let body = '';
  req.on('data', (c) => (body += c));
  req.on('end', async () => {
    const form = new URLSearchParams(body);
    try {
      const signed = await authorizeRoom(hibira, {
        socketId: form.get('socket_id'),
        channel: form.get('channel_name'),
        userId: form.get('user_id'),
        userInfo: { name: form.get('name') },
        capacity: 4,
      });
      res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(signed));
    } catch (err) {
      res.writeHead(err instanceof RoomFullError ? 403 : 500, { 'content-type': 'application/json' }).end(JSON.stringify({ error: err.message }));
    }
  });
});
await new Promise((r) => auth.listen(0, '127.0.0.1', r));
const authUrl = `http://127.0.0.1:${auth.address().port}/auth`;

const fail = (msg) => {
  console.error(`js: FAIL ${msg}`);
  process.exit(1);
};
const timeout = setTimeout(() => fail('timed out'), 30000);

// ---- C# side ------------------------------------------------------------------
const testsDir = fileURLToPath(new URL('../Hibira.Tests/', import.meta.url));
const unity = spawn('dotnet', ['run', '--', 'e2e'], {
  cwd: testsDir,
  env: { ...env, E2E_AUTH_URL: authUrl, E2E_CHANNEL: channel },
  stdio: ['ignore', 'pipe', 'inherit'],
});
const unityLines = [];
let onUnityLine = () => {};
let buf = '';
unity.stdout.on('data', (d) => {
  buf += d;
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    console.log(`c#: ${line}`);
    unityLines.push(line);
    onUnityLine(line);
  }
});
const unityExit = new Promise((r) => unity.on('exit', r));
const waitUnity = (text) =>
  new Promise((resolve) => {
    if (unityLines.includes(text)) return resolve();
    const prev = onUnityLine;
    onUnityLine = (l) => {
      prev(l);
      if (l === text) resolve();
    };
  });

// ---- JS side ------------------------------------------------------------------
await waitUnity('JOINED');
const pusher = new PusherClient(env.E2E_KEY, {
  cluster: 'mt1',
  wsHost: host,
  wsPort: port,
  wssPort: port,
  forceTLS: tls,
  enabledTransports: ['ws'],
  channelAuthorization: {
    customHandler: ({ socketId, channelName }, cb) =>
      authorizeRoom(hibira, { socketId, channel: channelName, userId: 'u-js', userInfo: { name: 'Bea' }, capacity: 4 }).then(
        (a) => cb(null, a),
        (e) => cb(e, null),
      ),
  },
});
const room = Room.join(pusher, channel, { capacity: 4 });
const seen = { settings: false, chat: false, start: false };
const check = () => {
  if (seen.settings && seen.chat && seen.start) {
    console.log('js: saw settings, chat and start from the Unity host');
    // A game event from JS to the Unity player, then leave.
    room.channel.trigger('client-game-tap', { round: 1, ms: 123 });
    setTimeout(() => room.leave(), 300);
  }
};
room.on('ready', ({ host: h }) => {
  if (h?.id !== 'u-unity') fail(`host is ${h?.id}, want u-unity`);
  room.setReady(true);
  room.send('hi from js');
});
room.on('settings', ({ settings, by }) => {
  if (settings.map === 'desert' && by === 'u-unity') seen.settings = true;
});
room.on('message', (m) => {
  if (!m.mine && m.text === 'hi from unity' && m.info?.name === 'Uma') seen.chat = true;
  check();
});
room.on('start', ({ data, by }) => {
  if (data?.seed !== 42 || by !== 'u-unity') fail(`bad start ${JSON.stringify(data)} by ${by}`);
  seen.start = true;
  check();
});

const code = await unityExit;
clearTimeout(timeout);
pusher.disconnect();
auth.close();
if (code !== 0) fail(`C# side exited ${code}`);
console.log('PASS: Unity and @hibira-io/lobby played a round in one room');
process.exit(0);
