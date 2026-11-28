namespace MyAiAssistant.Services.Dashboard;

/// <summary>
/// The standalone chat page served at <c>/chat</c>. It talks straight to the WebChat socket
/// (<c>ws://127.0.0.1:9881/</c>) and to nothing else — no dashboard token, no API call — so it also
/// serves as the reference client for the protocol in <c>docs/webchat-protocol.md</c>.
///
/// Self-contained on purpose: no external stylesheet, font or script, because the point is that it
/// works offline on a machine where the app is running.
/// </summary>
internal static class WebChatPage
{
    internal const string Html = """
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>仙灵 · 本机对话</title>
<style>
  :root{ --bg:#0b0e14; --panel:#141a24; --line:#243046; --ink:#e8eefb; --muted:#8b9ab3;
         --accent:#4ea1ff; --user:#1d2b3f; --err:#ff6b6b; }
  *{ box-sizing:border-box; }
  body{ margin:0; min-height:100vh; background:var(--bg); color:var(--ink);
        font:15px/1.6 -apple-system,"Segoe UI","Microsoft YaHei UI",sans-serif; }
  header{ display:flex; gap:12px; align-items:center; flex-wrap:wrap;
          padding:14px 20px; border-bottom:1px solid var(--line); background:var(--panel); }
  header h1{ font-size:15px; font-weight:600; margin:0 auto 0 0; }
  .state{ font-size:12px; color:var(--muted); }
  .state.on{ color:#48d597; }
  .state.bad{ color:var(--err); }
  input,button,textarea{ font:inherit; color:inherit; }
  input[type=number]{ width:92px; background:#0e131c; border:1px solid var(--line);
                      border-radius:8px; padding:6px 8px; }
  button{ background:#1b2433; border:1px solid var(--line); border-radius:8px;
          padding:6px 14px; cursor:pointer; }
  button:hover:not(:disabled){ border-color:var(--accent); }
  button.primary{ background:var(--accent); border-color:transparent; color:#06101f; font-weight:600; }
  button:disabled{ opacity:.45; cursor:default; }
  main{ max-width:820px; margin:0 auto; padding:20px; }
  #log{ display:flex; flex-direction:column; gap:12px; min-height:50vh; }
  .bubble{ max-width:86%; padding:10px 14px; border-radius:12px; white-space:pre-wrap;
           word-break:break-word; }
  .me{ align-self:flex-end; background:var(--user); border:1px solid var(--line); }
  .ai{ align-self:flex-start; background:var(--panel); border:1px solid var(--line); }
  .ai.pending{ color:var(--muted); }
  .sys{ align-self:center; font-size:12px; color:var(--muted); }
  .sys.bad{ color:var(--err); }
  footer{ position:sticky; bottom:0; background:var(--bg); padding:12px 0 20px; }
  .compose{ display:flex; gap:10px; align-items:flex-end; }
  textarea{ flex:1; min-height:64px; max-height:200px; resize:vertical; background:#0e131c;
            border:1px solid var(--line); border-radius:10px; padding:10px 12px; }
  .hint{ margin-top:6px; font-size:12px; color:var(--muted); }
</style></head>
<body>
<header>
  <h1>仙灵 · 本机对话</h1>
  <label class="state" for="port">端口</label>
  <input id="port" type="number" min="1" max="65535" value="9881">
  <span id="connState" class="state">未连接</span>
  <button id="connectBtn" class="primary">连接</button>
  <button id="resetBtn" disabled>清空上下文</button>
</header>
<main>
  <div id="log"></div>
  <footer>
    <div class="compose">
      <textarea id="input" placeholder="说点什么…（Enter 发送，Shift+Enter 换行）" disabled></textarea>
      <button id="sendBtn" class="primary" disabled>发送</button>
    </div>
    <div class="hint">对话走本机 WebSocket；窗口关掉就断了，历史不会保留。模型回复较慢时是正常的（本机推理）。</div>
  </footer>
</main>
<script>
const $ = id => document.getElementById(id);
let socket = null;
let pending = null;

function setState(text, kind){
  const el = $('connState');
  el.textContent = text;
  el.className = 'state' + (kind ? ' ' + kind : '');
}

function addBubble(text, cls){
  const div = document.createElement('div');
  div.className = 'bubble ' + cls;
  div.textContent = text;
  $('log').appendChild(div);
  div.scrollIntoView({ block:'end' });
  return div;
}

function addSystem(text, bad){
  const div = document.createElement('div');
  div.className = 'sys' + (bad ? ' bad' : '');
  div.textContent = text;
  $('log').appendChild(div);
  div.scrollIntoView({ block:'end' });
}

function connect(){
  const port = Number($('port').value) || 9881;
  const url = `ws://127.0.0.1:${port}/`;
  setState('连接中…');
  socket = new WebSocket(url);

  socket.onopen = () => {
    setState('已连接 ' + url, 'on');
    $('connectBtn').disabled = true;
    $('input').disabled = false;
    $('sendBtn').disabled = false;
    $('resetBtn').disabled = false;
    $('input').focus();
  };

  socket.onclose = () => {
    setState('已断开', 'bad');
    $('connectBtn').disabled = false;
    $('input').disabled = true;
    $('sendBtn').disabled = true;
    $('resetBtn').disabled = true;
    if (pending) { pending.classList.remove('pending'); pending.textContent = '(连接中断)'; pending = null; }
  };

  socket.onerror = () => setState('连接失败：服务没启动或端口不对', 'bad');

  socket.onmessage = event => {
    let frame;
    try { frame = JSON.parse(event.data); } catch { return; }

    if (frame.type === 'welcome') {
      addSystem(`已接通（协议 v${frame.protocol}，单条上限 ${frame.maxPromptChars} 字）`);
    } else if (frame.type === 'delta') {
      if (!pending) { pending = addBubble('', 'ai'); }
      // The first delta replaces the "…" placeholder instead of appending to it, or every reply
      // would start with an ellipsis.
      if (pending.dataset.placeholder === '1') {
        pending.textContent = frame.text;
        delete pending.dataset.placeholder;
      } else {
        pending.textContent += frame.text;
      }
      pending.classList.remove('pending');
      pending.scrollIntoView({ block:'end' });
    } else if (frame.type === 'done') {
      if (pending) { pending.classList.remove('pending'); pending = null; }
      addSystem(`本回合 ${frame.chars} 字`);
    } else if (frame.type === 'error') {
      if (pending) { pending.remove(); pending = null; }
      addSystem('失败：' + frame.message, true);
    } else if (frame.type === 'reset') {
      addSystem('上下文已清空');
    }
  };
}

function send(){
  const text = $('input').value.trim();
  if (!text || !socket || socket.readyState !== WebSocket.OPEN) return;
  addBubble(text, 'me');
  pending = addBubble('…', 'ai pending');
  pending.dataset.placeholder = '1';
  socket.send(JSON.stringify({ type:'message', text }));
  $('input').value = '';
  $('input').focus();
}

$('connectBtn').addEventListener('click', connect);
$('sendBtn').addEventListener('click', send);
$('resetBtn').addEventListener('click', () => {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type:'reset' }));
});
$('input').addEventListener('keydown', event => {
  if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); send(); }
});

// If the server is already up, connect without making the user click: a page whose only job is
// chat should be ready to type in.
connect();
</script>
</body></html>
""";
}
