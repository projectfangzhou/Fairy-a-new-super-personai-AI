namespace TransferSelfTest;

/// <summary>
/// The page the phone opens. It is a real peer: it answers the data channel, speaks the same frame
/// format as the PC (sealed chunks, hash check, resume plan, completion confirmation) and asks the
/// person for permission before writing anything.
///
/// Kept in one string on purpose — the whole point is that a phone can load it with nothing but a
/// browser, so there is no build step, no APK, and no dependency to install.
/// </summary>
internal static class WebPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>仙灵 · 接收测试</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  body { margin: 0; padding: 18px 16px 40px; background: #08080a; color: #e8e8ed;
         font: 15px/1.55 -apple-system, "PingFang SC", "Microsoft YaHei", sans-serif; }
  h1 { font-size: 17px; margin: 0 0 4px; }
  .sub { color: #7c7c88; font-size: 12px; margin: 0 0 16px; }
  .card { background: #121216; border: 1px solid #202027; border-radius: 14px; padding: 14px; margin-bottom: 12px; }
  .row { display: flex; gap: 10px; align-items: center; }
  .grow { flex: 1; }
  .mono { font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; font-size: 12px; word-break: break-all; }
  .dim { color: #7c7c88; }
  button { border: 0; border-radius: 10px; padding: 12px 16px; font-size: 15px; font-weight: 600; cursor: pointer; }
  button.primary { background: #edeff5; color: #0c0c0f; }
  button.ghost { background: #1c1c22; color: #d6d6e0; border: 1px solid #2c2c35; }
  button:disabled { opacity: .45; }
  #bar { height: 6px; background: #1c1c22; border-radius: 3px; overflow: hidden; margin-top: 10px; }
  #barFill { height: 100%; width: 0; background: #b3a6f7; transition: width .15s linear; }
  .ok { color: #86e0a8; }
  .err { color: #ff9d94; }
  #log { font-family: ui-monospace, Consolas, monospace; font-size: 11.5px; color: #8a8a96;
         max-height: 34vh; overflow: auto; white-space: pre-wrap; margin: 0; }
  a.download { display: inline-block; margin-top: 10px; color: #b3a6f7; font-weight: 600; }
  .pill { display: inline-block; padding: 2px 8px; border-radius: 999px; background: #1c1c22;
          border: 1px solid #2c2c35; font-size: 11px; color: #a9a9b8; }
</style>
</head>
<body>
<h1>仙灵 · 接收测试</h1>
<p class="sub">这台手机将作为第二台设备：先配对通道，再等电脑发文件，允许后才落盘。</p>

<div class="card">
  <div class="row"><span class="grow" id="state">正在连接…</span><span class="pill" id="conn">未连接</span></div>
  <div class="mono dim" id="ids" style="margin-top:8px"></div>
</div>

<div class="card" id="offer" hidden>
  <div style="font-weight:600;margin-bottom:6px" id="offerTitle">收到传输请求</div>
  <div class="mono dim" id="offerBody"></div>
  <div class="row" style="margin-top:12px">
    <button class="primary grow" id="accept">允许接收</button>
    <button class="ghost" id="reject">拒绝</button>
  </div>
</div>

<div class="card" id="progressCard" hidden>
  <div class="row"><span class="grow" id="progressText" class="mono">接收中…</span></div>
  <div id="bar"><div id="barFill"></div></div>
  <div id="result" style="margin-top:10px"></div>
</div>

<div class="card">
  <div class="dim" style="font-size:12px;margin-bottom:6px">过程</div>
  <pre id="log"></pre>
</div>

<script src="/simple-crypto.js"></script>
<script>
'use strict';

const { encryptGcm, decryptGcm, sha256 } = SimpleCrypto;

const S = { groupId: '', deviceId: '', hostDeviceId: '', lastSeen: 0 };
let key = null, pc = null, dc = null, pendingIce = [], remoteSet = false;
let manifest = null, files = [], sessionBytes = 0;
const CHUNK = 65536;

const $ = id => document.getElementById(id);
const enc = text => new TextEncoder().encode(text);
const dec = buf => new TextDecoder().decode(buf);

function log(text) {
  const line = new Date().toLocaleTimeString() + '  ' + text + '\n';
  $('log').textContent += line;
  $('log').scrollTop = $('log').scrollHeight;
  fetch('/api/log', { method: 'POST', body: text }).catch(() => {});
}

function b64encode(bytes) {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

function b64decode(text) {
  const binary = atob(text);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}

function concat(...parts) {
  let total = 0;
  for (const part of parts) total += part.length;
  const out = new Uint8Array(total);
  let offset = 0;
  for (const part of parts) { out.set(part, offset); offset += part.length; }
  return out;
}

function hex(bytes) {
  return Array.from(bytes).map(b => b.toString(16).padStart(2, '0')).join('');
}

// ---------- sealed signalling ----------

async function sealSignal(kind, value, to) {
  const from = S.deviceId;
  const sentAt = Date.now();
  const nonce = crypto.getRandomValues(new Uint8Array(12));
  const nonceHex = hex(nonce);
  const aad = enc(S.groupId + '\n' + from + '\n' + to + '\n' + sentAt + '\n' + nonceHex);
  const plain = enc(JSON.stringify({ From: from, Kind: kind, Value: value }));
  const sealed = encryptGcm(key, nonce, plain, aad);
  return { groupId: S.groupId, from, to, sentAt, nonce: nonceHex, sealed: b64encode(concat(nonce, sealed.tag, sealed.ciphertext)) };
}

async function openSignal(envelope) {
  const blob = b64decode(envelope.sealed);
  const nonce = blob.slice(0, 12);
  const tag = blob.slice(12, 28);
  const cipher = blob.slice(28);
  const aad = enc(envelope.groupId + '\n' + envelope.from + '\n' + envelope.to + '\n' + envelope.sentAt + '\n' + envelope.nonce);
  const plain = decryptGcm(key, nonce, cipher, tag, aad);
  return JSON.parse(dec(plain));
}

async function postSignal(kind, value, to) {
  const envelope = await sealSignal(kind, value, to || S.hostDeviceId);
  const res = await fetch('/api/signal', { method: 'POST', body: JSON.stringify(envelope) });
  if (!res.ok) log('信令发送失败 ' + res.status + ' ' + await res.text());
}

async function poll() {
  try {
    const request = await sealSignal('poll', JSON.stringify({ since: S.lastSeen }), S.hostDeviceId);
    const res = await fetch('/api/poll', { method: 'POST', body: JSON.stringify(request) });
    if (!res.ok) { log('轮询失败 ' + res.status); return; }
    const body = await res.json();
    for (const message of body.messages || []) {
      if (message.id <= S.lastSeen) continue;
      S.lastSeen = message.id;
      const signal = await openSignal(JSON.parse(message.envelope));
      await onSignal(signal);
    }
  } catch (err) {
    log('轮询异常：' + err.message);
  }
}

// ---------- frames ----------

async function openFrame(buffer) {
  const bytes = new Uint8Array(buffer);
  if (bytes.length < 44) throw new Error('帧太短');
  const header = bytes.slice(0, 28);
  const view = new DataView(header.buffer);
  const kind = header[3];
  const fileIndex = view.getInt32(4, true);
  const chunkIndex = view.getInt32(8, true);
  const length = view.getInt32(12, true);
  const nonce = header.slice(16, 28);
  const cipher = bytes.slice(28, 28 + length);
  const tag = bytes.slice(28 + length, 28 + length + 16);
  const plain = decryptGcm(key, nonce, cipher, tag, header);
  return { kind, fileIndex, chunkIndex, payload: plain };
}

async function sendFrame(kind, fileIndex, chunkIndex, payload) {
  const header = new Uint8Array(28);
  header[0] = 0x46; header[1] = 0x4E; header[2] = 1; header[3] = kind;
  const view = new DataView(header.buffer);
  view.setInt32(4, fileIndex, true);
  view.setInt32(8, chunkIndex, true);
  view.setInt32(12, payload.length, true);
  const nonce = crypto.getRandomValues(new Uint8Array(12));
  header.set(nonce, 16);
  const sealed = encryptGcm(key, nonce, payload, header);
  dc.send(concat(header, sealed.ciphertext, sealed.tag));
}

async function onFrame(buffer) {
  const frame = await openFrame(buffer);

  if (frame.kind === 1) {                      // manifest
    manifest = JSON.parse(dec(frame.payload));
    files = manifest.Files.map(file => ({
      meta: file,
      chunkCount: Math.ceil(file.Size / CHUNK),
      chunks: new Map(),
      received: 0
    }));
    sessionBytes = 0;
    $('offerTitle').textContent = manifest.Sender + ' 想发送 ' + manifest.Files.length + ' 个文件';
    $('offerBody').textContent = manifest.Files
      .map(f => f.Name + '  ' + (f.Size / 1048576).toFixed(2) + ' MB')
      .join('\n');
    $('offer').hidden = false;
    log('收到清单：' + manifest.Files.map(f => f.Name).join('、'));
    return;
  }

  if (frame.kind === 2) {                      // chunk
    const file = files[frame.fileIndex];
    if (!file) return;
    if (!file.chunks.has(frame.chunkIndex)) file.received++;
    file.chunks.set(frame.chunkIndex, frame.payload);
    sessionBytes += frame.payload.length;
    updateProgress();
    return;
  }

  if (frame.kind === 4) {                      // complete
    await finishFile(frame.fileIndex);
    return;
  }

  if (frame.kind === 5) {                      // cancel
    log('对方取消了传输');
    $('state').textContent = '对方取消了传输';
    return;
  }
}

function updateProgress() {
  let total = 0, received = 0;
  for (const file of files) {
    total += file.chunkCount;
    received += file.received;
  }
  const percent = total === 0 ? 0 : Math.round(received * 100 / total);
  $('progressText').textContent = '接收中 ' + percent + '%（' + (sessionBytes / 1048576).toFixed(2) + ' MB）';
  $('barFill').style.width = percent + '%';
}

async function finishFile(index) {
  const file = files[index];
  if (!file) return;

  const parts = [];
  for (let i = 0; i < file.chunkCount; i++) {
    const part = file.chunks.get(i);
    if (!part) { log('缺分块 ' + i + '，无法完成'); return; }
    parts.push(part);
  }

  const blob = new Blob(parts);
  if (blob.size !== file.meta.Size) {
    log('大小不符：收到 ' + blob.size + '，应为 ' + file.meta.Size);
    $('result').innerHTML = '<span class="err">大小不符，已丢弃</span>';
    return;
  }

  const actual = hex(sha256(new Uint8Array(await blob.arrayBuffer())));
  const expected = String(file.meta.Sha256).toLowerCase();

  if (actual !== expected) {
    log('校验不通过：' + actual);
    $('result').innerHTML = '<span class="err">整文件校验不通过，已丢弃</span>';
    return;
  }

  const url = URL.createObjectURL(blob);
  $('result').innerHTML = '<span class="ok">校验通过，可以保存</span>' +
    '<div class="mono dim" style="margin-top:6px">sha256 ' + actual + '</div>' +
    '<a class="download" download="' + file.meta.Name + '" href="' + url + '">保存到手机</a>';
  log('校验通过：' + actual);

  // The PC only calls the transfer done once it hears this, so it must not be skipped.
  await sendFrame(3, -1, -1, enc(JSON.stringify({ SessionId: manifest.SessionId, Saved: [file.meta.Name] })));
}

// ---------- webRTC ----------

async function onSignal(signal) {
  const value = JSON.parse(signal.Value);

  if (signal.Kind === 'Offer') {
    log('收到对方的连接请求');
    pc = new RTCPeerConnection({ iceServers: [] });
    pc.onicecandidate = event => {
      if (event.candidate) postSignal('Ice', JSON.stringify(event.candidate));
    };
    pc.onconnectionstatechange = () => setConn(pc.connectionState);
    pc.ondatachannel = event => {
      dc = event.channel;
      dc.binaryType = 'arraybuffer';
      dc.onopen = () => { setConn('open'); log('数据通道已建立'); };
      dc.onmessage = event => { onFrame(event.data).catch(err => log('处理帧失败：' + err.message)); };
      dc.onclose = () => log('数据通道已关闭');
    };

    await pc.setRemoteDescription(value);
    remoteSet = true;
    for (const candidate of pendingIce.splice(0)) await pc.addIceCandidate(candidate).catch(() => {});
    await pc.setLocalDescription(await pc.createAnswer());
    await postSignal('Answer', JSON.stringify(pc.localDescription));
    return;
  }

  if (signal.Kind === 'Answer') {
    if (pc) await pc.setRemoteDescription(value);
    return;
  }

  if (signal.Kind === 'Ice') {
    if (!pc) return;
    if (!remoteSet) { pendingIce.push(value); return; }
    await pc.addIceCandidate(value).catch(() => {});
  }
}

function setConn(state) {
  $('conn').textContent = state;
  $('conn').className = 'pill ' + (state === 'open' ? 'ok' : '');
}

// ---------- approval ----------

$('accept').onclick = async () => {
  $('offer').hidden = true;
  $('progressCard').hidden = false;
  $('state').textContent = '正在接收…';

  // The plan doubles as the acceptance: the PC sends no chunks until it arrives.
  const plan = {
    SessionId: manifest.SessionId,
    Files: files.map((file, index) => ({
      FileIndex: index,
      MissingChunks: Array.from({ length: file.chunkCount }, (_, i) => i)
    }))
  };

  await sendFrame(3, -1, -1, enc(JSON.stringify(plan)));
  log('已允许接收，并告知对方需要的分块');
};

$('reject').onclick = async () => {
  $('offer').hidden = true;
  $('state').textContent = '已拒绝这次传输';
  await sendFrame(5, -1, -1, new Uint8Array(0));
  log('已拒绝，并回了取消帧');
};

// ---------- boot ----------

(async () => {
  const session = await (await fetch('/api/session')).json();
  S.groupId = session.groupId;
  S.deviceId = session.deviceId;
  S.hostDeviceId = session.hostDeviceId;

  key = b64decode(session.groupKey);

  $('state').textContent = '已连接到 ' + session.hostName + '，等待对方发送';
  $('ids').textContent = '本机 ' + S.deviceId + '\n对端 ' + S.hostDeviceId + '\n设备组 ' + S.groupId;
  log('会话就绪');

  setInterval(poll, 90);
})();
</script>
</body>
</html>
""";
}
