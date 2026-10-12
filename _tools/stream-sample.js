// Send window.__composeText, then sample the NEW turn every 300 ms for up to 60 s: Stop/Send/Dictate button
// presence (what GeminiChatService.IsProcessing reads), aria-busy inside the model-response, whether the
// Copy action exists, and text length. Prints only state changes. Finds a reliable "turn finished" signal.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const q = s => document.querySelector(s);
  const ed = q('chat-window div[contenteditable="true"]');
  const before = document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length;
  ed.focus();
  document.execCommand('selectAll', false, null);
  document.execCommand('insertText', false, window.__composeText || 'Write about 250 words on why tides happen.');
  await sleep(300);
  q('chat-window button[aria-label*="Send"]').click();
  const rows = []; let last = ''; const t0 = performance.now();
  for (let i = 0; i < 200; i++) {
    const cs = document.querySelectorAll('chat-window infinite-scroller > div.conversation-container');
    const c = cs.length > before ? cs[cs.length - 1] : null;
    const mr = c?.querySelector('model-response');
    const busy = mr ? [...mr.querySelectorAll('[aria-busy]')].map(e => e.getAttribute('aria-busy')).join(',') : '';
    const copy = !!mr?.querySelector('button[aria-label="Copy"]');
    const len = (mr?.querySelector("[id*='model-response-message-content']")?.textContent || '').length;
    const btn = (q('chat-window button[aria-label*="Stop"]') ? 'STOP ' : '') + (q('chat-window button[aria-label*="Send"]') ? 'SEND ' : '') + (q('chat-window button[aria-label*="Dictate"]') ? 'DICT' : '');
    const s = `turn=${!!c} [${btn.trim()}] busy=${busy} copy=${copy} len=${len > 0 ? (len < 100 ? len : '100+') : 0}`;
    if (s !== last) { rows.push(`${Math.round(performance.now() - t0)}ms ${s}`); last = s; }
    if (copy && len > 0 && !btn.includes('STOP')) break;
    await sleep(300);
  }
  return rows.join('\n');
})()
