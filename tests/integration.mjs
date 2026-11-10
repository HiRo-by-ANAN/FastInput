import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const binary = process.env.FASTINPUT_BINARY || resolve(
  'target', 'debug', process.platform === 'win32' ? 'fastinput.exe' : 'fastinput'
);
const dataDir = await mkdtemp(join(tmpdir(), 'fastinput-integration-'));

async function freePort() {
  const server = createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const port = server.address().port;
  server.close();
  await once(server, 'close');
  return port;
}

async function start() {
  const port = await freePort();
  const child = spawn(binary, ['--port', String(port), '--lan-ip', '127.0.0.1', '--no-browser'], {
    env: { ...process.env, FASTINPUT_DATA_DIR: dataDir },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let output = '';
  const desktopUrl = await new Promise((done, fail) => {
    const timer = setTimeout(() => fail(new Error(`Startup timeout: ${output}`)), 5000);
    child.stdout.on('data', (chunk) => {
      output += chunk;
      const match = output.match(/Computer: (http:\/\/[^\s]+)/);
      if (match) { clearTimeout(timer); done(match[1]); }
    });
    child.once('error', (error) => { clearTimeout(timer); fail(error); });
    child.once('exit', (code) => { clearTimeout(timer); fail(new Error(`Exited ${code}: ${output}`)); });
  });
  return { child, port, desktopUrl, origin: `http://127.0.0.1:${port}` };
}

async function stop(server) {
  server.child.kill();
  await once(server.child, 'exit');
}

function cookie(response) {
  const value = response.headers.get('set-cookie')?.split(';', 1)[0];
  assert.match(value || '', /^fi_session=[0-9a-f]{64}$/);
  return value;
}

async function state(server, auth) {
  const response = await fetch(`${server.origin}/api/state`, {
    headers: { Cookie: auth }, cache: 'no-store',
  });
  assert.equal(response.status, 200);
  assert.match(response.headers.get('cache-control'), /no-store/);
  return response.json();
}

async function post(server, path, auth, body) {
  return fetch(`${server.origin}${path}`, {
    method: 'POST',
    headers: {
      Cookie: auth,
      Origin: server.origin,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
  });
}

let server;
try {
  server = await start();
  const login = await fetch(server.desktopUrl, { redirect: 'manual' });
  assert.equal(login.status, 303);
  const desktopCookie = cookie(login);
  let desktop = await state(server, desktopCookie);
  assert.equal(desktop.status, 'ready');
  assert.equal(desktop.role, 'desktop');
  assert.match(desktop.pairing.svg, /<svg/);
  assert.match(desktop.pairing.pin, /^\d{6}$/);
  const qrResponse = await fetch(`${server.origin}/api/pairing/qr.png`, {
    headers: { Cookie: desktopCookie },
  });
  assert.equal(qrResponse.status, 200);
  assert.equal(qrResponse.headers.get('content-type'), 'image/png');
  assert.equal(Buffer.from(await qrResponse.arrayBuffer()).subarray(1, 4).toString(), 'PNG');

  // Test PIN pairing: wrong PIN
  const wrongPinResp = await post(server, '/api/pairing/pin', '', { pin: '000000' === desktop.pairing.pin ? '111111' : '000000' });
  assert.equal(wrongPinResp.status, 403);

  // Test PIN pairing: valid PIN with spaces
  const formattedPin = `${desktop.pairing.pin.slice(0, 3)} ${desktop.pairing.pin.slice(3)}`;
  const validPinResp = await post(server, '/api/pairing/pin', '', { pin: formattedPin });
  assert.equal(validPinResp.status, 200);
  const pinPairedCookie = cookie(validPinResp);
  let pinPeer = await state(server, pinPairedCookie);
  assert.equal(pinPeer.status, 'ready');
  assert.equal(pinPeer.role, 'phone');

  // A successful pairing rotates the code; the refreshed QR must be used.
  desktop = await state(server, desktopCookie);
  const scan = await fetch(desktop.pairing.url, { redirect: 'manual' });
  assert.equal(scan.status, 303);
  const phoneCookie = cookie(scan);
  let phone = await state(server, phoneCookie);
  assert.equal(phone.role, 'phone');
  desktop = await state(server, desktopCookie);
  assert.equal(desktop.devices.length, 1, 're-pairing from the same address keeps one device row');
  assert.equal((await state(server, pinPairedCookie)).role, 'phone', 'the earlier browser session still works');
  const rescan = await fetch(desktop.pairing.url, {
    headers: { Cookie: phoneCookie }, redirect: 'manual',
  });
  assert.equal(rescan.status, 303);
  assert.equal(rescan.headers.get('set-cookie'), null);
  assert.equal((await state(server, desktopCookie)).pending.length, 0);

  const eventResponse = await fetch(`${server.origin}/api/events`, {
    headers: { Cookie: desktopCookie },
  });
  assert.equal(eventResponse.status, 200);
  const reader = eventResponse.body.getReader();
  const firstEvent = new TextDecoder().decode((await reader.read()).value);
  assert.match(firstEvent, /"revision":0/);

  const changed = await post(server, '/api/text', phoneCookie, {
    text: '手機輸入\n第二行', revision: 0, generation: phone.generation,
  });
  assert.equal(changed.status, 200);
  const nextEvent = await Promise.race([
    reader.read(),
    new Promise((_, reject) => setTimeout(() => reject(new Error('SSE update timed out')), 3000)),
  ]);
  assert.match(new TextDecoder().decode(nextEvent.value), /手機輸入/);
  await reader.cancel();
  desktop = await state(server, desktopCookie);
  assert.equal(desktop.text, '手機輸入\n第二行');
  assert.equal(desktop.revision, 1);

  const stale = await post(server, '/api/text', phoneCookie, {
    text: '舊草稿', revision: 0, generation: phone.generation,
  });
  assert.equal(stale.status, 409);
  assert.equal((await stale.json()).text, '手機輸入\n第二行');

  const forgedOrigin = await fetch(`${server.origin}/api/text`, {
    method: 'POST',
    headers: { Cookie: phoneCookie, Origin: 'http://evil.invalid', 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: 'hijack', revision: 1, generation: phone.generation }),
  });
  assert.equal(forgedOrigin.status, 403);

  await stop(server);
  server = await start();
  phone = await state(server, phoneCookie);
  assert.equal(phone.text, '手機輸入\n第二行');
  assert.equal(phone.revision, 1);
  desktop = await state(server, desktopCookie);
  assert.equal(desktop.devices.length, 1);

  const revoked = await post(server, '/api/device/revoke', desktopCookie, {
    id: desktop.devices[0].id,
  });
  assert.equal(revoked.status, 200);
  assert.equal((await state(server, phoneCookie)).status, 'unpaired');
  assert.equal((await state(server, pinPairedCookie)).status, 'unpaired');
  const denied = await post(server, '/api/text', phoneCookie, {
    text: 'should fail', revision: 1, generation: phone.generation,
  });
  assert.equal(denied.status, 401);
  const shutdown = await post(server, '/api/shutdown', desktopCookie, {});
  assert.equal(shutdown.status, 200);
  await Promise.race([
    once(server.child, 'exit'),
    new Promise((_, reject) => setTimeout(() => reject(new Error('Shutdown timed out')), 3000)),
  ]);
  console.log('FastInput integration checks passed');
} finally {
  if (server && server.child.exitCode === null) await stop(server);
  await rm(dataDir, { recursive: true, force: true });
}
