import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { webcrypto } from 'node:crypto';

if (!globalThis.crypto) globalThis.crypto = webcrypto;
const source = await readFile(new URL('./cloudflare-worker.js', import.meta.url), 'utf8');
const { default: worker } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const tokens = { pc: 'a'.repeat(48), phone: 'b'.repeat(48), stranger: 'c'.repeat(48), other: 'd'.repeat(48) };
const origin = 'https://app.example.test';

function fixture() {
  const store = new Map();
  const writes = [];
  const deletions = [];
  const env = {
    FAIRYAI_DEVICES: JSON.stringify([
      { tenant: 'a', device: 'pc', token: tokens.pc, peers: ['phone'] },
      { tenant: 'a', device: 'phone', token: tokens.phone, peers: ['pc'] },
      { tenant: 'a', device: 'stranger', token: tokens.stranger, peers: [] },
      { tenant: 'b', device: 'phone', token: tokens.other, peers: [] },
    ]),
    FAIRYAI_ALLOWED_ORIGINS: JSON.stringify([origin]),
    FAIRYAI_KV: {
      async put(key, value, options) { store.set(key, value); writes.push({ key, options }); },
      async get(key) { return store.get(key) ?? null; },
      async list({ prefix, limit }) {
        return { keys: [...store.keys()].filter(key => key.startsWith(prefix)).slice(0, limit).map(name => ({ name })) };
      },
      async delete(key) { deletions.push(key); store.delete(key); },
    },
  };
  function request(path = '/poll', { token = tokens.pc, method = 'GET', data, body, headers = {}, ...rest } = {}) {
    const h = new Headers(headers);
    if (token !== null) h.set('Authorization', `Bearer ${token}`);
    if (data !== undefined) { body = JSON.stringify(data); h.set('Content-Type', 'application/json'); }
    return worker.fetch(new Request(`https://relay.example.test${path}`, { method, headers: h, body, ...rest }), env);
  }
  const send = (payload = 'hello') => request('/sync', { method: 'POST', data: { target_device: 'phone', payload } });
  return { env, store, writes, deletions, request, send };
}

test('missing, wrong and legacy shared credentials cannot access any route', async () => {
  const f = fixture();
  f.env.FAIRYAI_SECRET = 'legacy'.repeat(8);
  for (const path of ['/poll', '/health', '/sync', '/ack']) {
    for (const token of [null, 'z'.repeat(48), f.env.FAIRYAI_SECRET]) {
      const result = await f.request(path, { token, method: ['/sync', '/ack'].includes(path) ? 'POST' : 'GET' });
      assert.equal(result.status, 401);
    }
  }
  assert.equal(f.store.size, 0);
});

test('missing storage, device config or origin config fails closed', async () => {
  for (const key of ['FAIRYAI_KV', 'FAIRYAI_DEVICES', 'FAIRYAI_ALLOWED_ORIGINS']) {
    const f = fixture();
    delete f.env[key];
    assert.equal((await f.send()).status, 503);
    assert.equal((await f.request('/health')).status, 503);
    assert.equal(f.store.size, 0);
  }
});

test('invalid provisioning, duplicate tokens and foreign pairing fail closed', async () => {
  const mutations = [
    rows => { rows[0].token = rows[1].token; },
    rows => { rows[0].token = 'short'; },
    rows => { rows[0].tenant = 'a:b'; },
    rows => { rows[0].peers = ['unprovisioned']; },
    rows => { rows[3].peers = ['pc']; },
    rows => { rows.push({ ...rows[0], token: 'x'.repeat(48) }); },
  ];
  for (const mutate of mutations) {
    const f = fixture();
    const rows = JSON.parse(f.env.FAIRYAI_DEVICES);
    mutate(rows);
    f.env.FAIRYAI_DEVICES = JSON.stringify(rows);
    assert.equal((await f.send()).status, 503);
  }
  for (const invalid of ['broken-json', '[]', '{}']) {
    const f = fixture();
    f.env.FAIRYAI_DEVICES = invalid;
    assert.equal((await f.send()).status, 503);
  }
});

test('send is paired and server derives source and tenant from token', async () => {
  const f = fixture();
  assert.equal((await f.request('/sync', { token: tokens.stranger, method: 'POST', data: { target_device: 'phone', payload: 1 } })).status, 403);
  assert.equal((await f.request('/sync', { token: tokens.other, method: 'POST', data: { target_device: 'pc', payload: 1 } })).status, 403);
  for (const extra of [{ tenant: 'b' }, { source_device: 'stranger' }, { device: 'phone' }]) {
    assert.equal((await f.request('/sync', { method: 'POST', data: { target_device: 'phone', payload: 1, ...extra } })).status, 400);
  }
  const result = await f.send({ tenant: 'b', source_device: 'stranger' });
  assert.equal(result.status, 201);
  const { id } = await result.json();
  assert.match(f.writes[0].key, /^v2:a:phone:/);
  assert.equal(f.writes[0].options.expirationTtl, 86400);
  const items = (await (await f.request('/poll', { token: tokens.phone })).json()).items;
  assert.equal(items[0].id, id);
  assert.equal(items[0].source_device, 'pc');
  assert.deepEqual((await (await f.request('/poll', { token: tokens.other })).json()).items, []);
  assert.deepEqual((await (await f.request('/poll')).json()).items, []);
});

test('poll is non-destructive; only recipient ack deletes; retries are idempotent', async () => {
  const f = fixture();
  const { id } = await (await f.send()).json();
  for (let i = 0; i < 2; i++) {
    const result = await f.request('/poll', { token: tokens.phone });
    assert.equal((await result.json()).items.length, 1);
  }
  assert.equal(f.deletions.length, 0);
  for (const token of [tokens.other, tokens.pc, tokens.stranger]) {
    assert.equal((await f.request('/ack', { token, method: 'POST', data: { ids: [id] } })).status, 200);
    assert.equal(f.store.size, 1);
  }
  for (let i = 0; i < 2; i++) {
    assert.equal((await f.request('/ack', { token: tokens.phone, method: 'POST', data: { ids: [id] } })).status, 200);
  }
  assert.equal(f.store.size, 0);
});

test('ack rejects client keys, malformed ids, extra identity and oversized batches', async () => {
  const f = fixture();
  const { id } = await (await f.send()).json();
  for (const data of [{ ids: [] }, { ids: ['v2:a:phone:' + id] }, { ids: [id], tenant: 'a' }, { ids: Array(26).fill(id) }, { ids: [null] }]) {
    assert.equal((await f.request('/ack', { method: 'POST', token: tokens.phone, data })).status, 400);
  }
  assert.equal(f.deletions.length, 0);
});

test('body limit checks declared length, actual bytes and chunked streams on both POST routes', async () => {
  for (const path of ['/sync', '/ack']) {
    const f = fixture();
    assert.equal((await f.request(path, { method: 'POST', body: '{}', headers: { 'Content-Type': 'application/json', 'Content-Length': '65537' } })).status, 413);
    assert.equal((await f.request(path, { method: 'POST', body: ' '.repeat(65537), headers: { 'Content-Type': 'application/json', 'Content-Length': '2' } })).status, 413);
    assert.equal((await f.request(path, { method: 'POST', body: '界'.repeat(22000), headers: { 'Content-Type': 'application/json' } })).status, 413);
    let cancelled = false;
    const body = new ReadableStream({
      pull(controller) { controller.enqueue(new Uint8Array(8192)); },
      cancel() { cancelled = true; },
    });
    assert.equal((await f.request(path, { method: 'POST', body, duplex: 'half', headers: { 'Content-Type': 'application/json' } })).status, 413);
    assert.equal(cancelled, true);
    assert.equal(f.store.size, 0);
  }
});

test('exact byte limit is accepted and invalid JSON or media type is rejected', async () => {
  const f = fixture();
  const overhead = Buffer.byteLength(JSON.stringify({ target_device: 'phone', payload: '' }));
  assert.equal((await f.send('x'.repeat(65536 - overhead))).status, 201);
  assert.equal((await f.send('x'.repeat(65537 - overhead))).status, 413);
  assert.equal((await f.request('/sync', { method: 'POST', body: '{', headers: { 'Content-Type': 'application/json' } })).status, 400);
  assert.equal((await f.request('/sync', { method: 'POST', body: '{}' })).status, 415);
  assert.equal((await f.request('/sync', { method: 'POST', data: {}, headers: { 'Content-Encoding': 'gzip' } })).status, 415);
});

test('paths and methods are exact, including legacy device selectors', async () => {
  const f = fixture();
  for (const path of ['/poll/phone', '/poll/phone/extra', '/poll?device=phone', '/sync/', '/unknown']) {
    assert.equal((await f.request(path)).status, 404);
  }
  for (const [path, method] of [['/sync', 'GET'], ['/ack', 'GET'], ['/poll', 'POST'], ['/health', 'DELETE']]) {
    assert.equal((await f.request(path, { method })).status, 405);
  }
  assert.equal((await f.request('/health')).status, 200);
});

test('CORS allows exact origins only, validates preflight, and never replaces authentication', async () => {
  const f = fixture();
  for (const denied of ['null', 'https://app.example.test.evil.test', 'http://app.example.test']) {
    const result = await f.request('/poll', { headers: { Origin: denied } });
    assert.equal(result.status, 403);
    assert.equal(result.headers.get('Access-Control-Allow-Origin'), null);
  }
  const allowed = await f.request('/poll', { headers: { Origin: origin } });
  assert.equal(allowed.headers.get('Access-Control-Allow-Origin'), origin);
  assert.equal(allowed.headers.get('Cache-Control'), 'no-store');
  assert.equal((await f.request('/poll', { token: null, headers: { Origin: origin } })).status, 401);
  const headers = { Origin: origin, 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'authorization,content-type' };
  assert.equal((await f.request('/sync', { method: 'OPTIONS', token: null, headers })).status, 204);
  assert.equal((await f.request('/sync', { method: 'OPTIONS', headers: { ...headers, 'Access-Control-Request-Method': 'DELETE' } })).status, 403);
  assert.equal((await f.request('/sync', { method: 'OPTIONS', headers: { ...headers, 'Access-Control-Request-Headers': 'x-tenant' } })).status, 403);
  f.env.FAIRYAI_ALLOWED_ORIGINS = '[]';
  assert.equal((await f.request()).status, 200);
  assert.equal((await f.request('/poll', { headers: { Origin: origin } })).status, 403);
  f.env.FAIRYAI_ALLOWED_ORIGINS = '["*"]';
  assert.equal((await f.request()).status, 503);
});

test('storage errors are generic failures and failed reads never delete', async () => {
  for (const method of ['put', 'get', 'list', 'delete']) {
    const f = fixture();
    const { id } = await (await f.send()).json();
    f.env.FAIRYAI_KV[method] = async () => { throw new Error('secret infrastructure details'); };
    const result = method === 'put' ? await f.send()
      : method === 'delete' ? await f.request('/ack', { token: tokens.phone, method: 'POST', data: { ids: [id] } })
        : await f.request('/poll', { token: tokens.phone });
    assert.equal(result.status, 503);
    assert.deepEqual(await result.json(), { error: 'Service unavailable' });
    assert.equal(f.store.size, 1);
    assert.equal(f.deletions.length, 0);
  }
});

test('poll batches are bounded and remaining messages become available after ack', async () => {
  const f = fixture();
  for (let i = 0; i < 26; i++) assert.equal((await f.send(i)).status, 201);
  const { items } = await (await f.request('/poll', { token: tokens.phone })).json();
  assert.equal(items.length, 25);
  assert.equal((await f.request('/ack', { token: tokens.phone, method: 'POST', data: { ids: items.map(item => item.id) } })).status, 200);
  assert.equal((await (await f.request('/poll', { token: tokens.phone })).json()).items.length, 1);
});
