// Relay protocol v2: requires client migration; legacy FAIRYAI_SECRET is ignored.
// Bind FAIRYAI_KV and set FAIRYAI_DEVICES as a secret JSON array, for example:
// [{"tenant":"team-a","device":"pc","token":"<unique random token, 32+ chars>","peers":["phone"]},
//  {"tenant":"team-a","device":"phone","token":"<another random token, 32+ chars>","peers":["pc"]}]
// peers grants directed send permission within the configured tenant only.
// Provision tokens out of band; clients cannot register identities or grant pairing.
// FAIRYAI_ALLOWED_ORIGINS is a JSON array of exact HTTP(S) origins, without paths.
// Use [] for native-only access. Origin-less requests still require a device token.
// POST /sync {target_device, payload}; GET /poll; POST /ack {ids: [message UUID]}.
// Poll returns server envelopes; persist/process them before ack, deduplicate by id.
// KV is eventually consistent: retries/redelivery are expected, including after ack.
// Delivery is bounded by the 24-hour TTL; durable/atomic delivery requires a different store.

const MAX_BODY_BYTES = 64 * 1024;
const PAGE_SIZE = 25;
const ID = /^[A-Za-z0-9_-]{1,64}$/;
const TOKEN = /^[A-Za-z0-9_-]{32,256}$/;
const MESSAGE_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const METHODS = new Map([
  ['/sync', 'POST'], ['/poll', 'GET'], ['/ack', 'POST'], ['/health', 'GET'],
]);

class RequestError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function configuration(env) {
  if (!env.FAIRYAI_KV || ['put', 'get', 'list', 'delete'].some(
    method => typeof env.FAIRYAI_KV[method] !== 'function',
  )) throw new Error('Invalid configuration');
  const devices = JSON.parse(env.FAIRYAI_DEVICES);
  const origins = JSON.parse(env.FAIRYAI_ALLOWED_ORIGINS);
  if (!Array.isArray(devices) || devices.length === 0 || !Array.isArray(origins)) {
    throw new Error('Invalid configuration');
  }
  const identities = new Set();
  const tokens = new Set();
  for (const entry of devices) {
    if (!isObject(entry) || typeof entry.tenant !== 'string' || !ID.test(entry.tenant)
      || typeof entry.device !== 'string' || !ID.test(entry.device)
      || typeof entry.token !== 'string' || !TOKEN.test(entry.token)
      || !Array.isArray(entry.peers) || entry.peers.some(peer => typeof peer !== 'string' || !ID.test(peer))) {
      throw new Error('Invalid configuration');
    }
    const identity = `${entry.tenant}:${entry.device}`;
    if (identities.has(identity) || tokens.has(entry.token)) throw new Error('Invalid configuration');
    identities.add(identity);
    tokens.add(entry.token);
  }
  for (const entry of devices) {
    if (entry.peers.some(peer => peer === entry.device || !identities.has(`${entry.tenant}:${peer}`))) {
      throw new Error('Invalid configuration');
    }
  }
  for (const origin of origins) {
    if (typeof origin !== 'string') throw new Error('Invalid configuration');
    const parsed = new URL(origin);
    if (!['https:', 'http:'].includes(parsed.protocol) || parsed.origin !== origin) {
      throw new Error('Invalid configuration');
    }
  }
  return { devices, origins };
}

async function authenticate(request, devices) {
  const match = /^Bearer ([A-Za-z0-9_-]{32,256})$/.exec(request.headers.get('Authorization') || '');
  if (!match) throw new RequestError(401, 'Unauthorized');
  const digest = async token => new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(token)));
  const supplied = await digest(match[1]);
  let identity;
  for (const entry of devices) {
    const expected = await digest(entry.token);
    let difference = 0;
    for (let i = 0; i < supplied.length; i++) difference |= supplied[i] ^ expected[i];
    if (difference === 0) identity = entry;
  }
  if (!identity) throw new RequestError(401, 'Unauthorized');
  return identity;
}

async function readBody(request) {
  const length = request.headers.get('Content-Length');
  if (length !== null && (!/^\d+$/.test(length) || Number(length) > MAX_BODY_BYTES)) {
    throw new RequestError(413, 'Request body too large');
  }
  if (request.headers.has('Content-Encoding')) throw new RequestError(415, 'Unsupported media type');
  if (!/^application\/json(?:\s*;\s*charset=utf-8)?$/i.test(request.headers.get('Content-Type') || '')) {
    throw new RequestError(415, 'Unsupported media type');
  }
  if (!request.body) throw new RequestError(400, 'Invalid request');
  const reader = request.body.getReader();
  const chunks = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > MAX_BODY_BYTES) {
        void reader.cancel().catch(() => {});
        throw new RequestError(413, 'Request body too large');
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  try {
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch {
    throw new RequestError(400, 'Invalid request');
  }
}

function mailbox(tenant, device) {
  return `v2:${tenant}:${device}:`;
}

export default {
  async fetch(request, env) {
    const headers = {
      'Content-Type': 'application/json',
      'Cache-Control': 'no-store',
      'Vary': 'Origin',
      'X-Content-Type-Options': 'nosniff',
    };
    const respond = (data, status = 200) => new Response(JSON.stringify(data), { status, headers });
    try {
      const { devices, origins } = configuration(env);
      const origin = request.headers.get('Origin');
      if (origin !== null) {
        if (!origins.includes(origin)) throw new RequestError(403, 'Forbidden');
        headers['Access-Control-Allow-Origin'] = origin;
      }
      const url = new URL(request.url);
      const method = METHODS.get(url.pathname);
      if (!method || url.search) throw new RequestError(404, 'Not found');
      if (request.method === 'OPTIONS') {
        if (origin === null || request.headers.get('Access-Control-Request-Method') !== method) {
          throw new RequestError(403, 'Forbidden');
        }
        const requested = (request.headers.get('Access-Control-Request-Headers') || '')
          .split(',').map(value => value.trim().toLowerCase()).filter(Boolean);
        if (requested.some(value => !['authorization', 'content-type'].includes(value))) {
          throw new RequestError(403, 'Forbidden');
        }
        return new Response(null, { status: 204, headers: {
          ...headers,
          'Access-Control-Allow-Methods': method,
          'Access-Control-Allow-Headers': 'Authorization, Content-Type',
          'Vary': 'Origin, Access-Control-Request-Method, Access-Control-Request-Headers',
        } });
      }
      if (request.method !== method) {
        headers.Allow = method;
        throw new RequestError(405, 'Method not allowed');
      }
      const identity = await authenticate(request, devices);
      if (url.pathname === '/health') return respond({ status: 'ok', protocol: 2 });
      if (url.pathname === '/sync') {
        const data = await readBody(request);
        if (!isObject(data) || Object.keys(data).length !== 2
          || !Object.hasOwn(data, 'payload') || typeof data.target_device !== 'string'
          || !ID.test(data.target_device)) throw new RequestError(400, 'Invalid request');
        if (!identity.peers.includes(data.target_device)) throw new RequestError(403, 'Forbidden');
        const id = crypto.randomUUID();
        const envelope = {
          id, source_device: identity.device, target_device: data.target_device,
          created_at: new Date().toISOString(), payload: data.payload,
        };
        await env.FAIRYAI_KV.put(mailbox(identity.tenant, data.target_device) + id, JSON.stringify(envelope), {
          expirationTtl: 86400,
        });
        return respond({ status: 'queued', id }, 201);
      }
      const prefix = mailbox(identity.tenant, identity.device);
      if (url.pathname === '/poll') {
        const list = await env.FAIRYAI_KV.list({ prefix, limit: PAGE_SIZE });
        const items = [];
        for (const key of list.keys) {
          const value = await env.FAIRYAI_KV.get(key.name);
          if (value !== null) items.push(JSON.parse(value));
        }
        return respond({ items });
      }
      const data = await readBody(request);
      if (!isObject(data) || Object.keys(data).length !== 1 || !Array.isArray(data.ids)
        || data.ids.length === 0 || data.ids.length > PAGE_SIZE
        || data.ids.some(id => typeof id !== 'string' || !MESSAGE_ID.test(id))) {
        throw new RequestError(400, 'Invalid request');
      }
      // Derive every deletion key from the authenticated recipient, never a client key.
      for (const id of new Set(data.ids)) await env.FAIRYAI_KV.delete(prefix + id);
      return respond({ status: 'acknowledged' });
    } catch (error) {
      if (error instanceof RequestError) return respond({ error: error.message }, error.status);
      return respond({ error: 'Service unavailable' }, 503);
    }
  },
};
