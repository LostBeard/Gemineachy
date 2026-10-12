// Type + script-click Send, then sample every 250 ms for 20 s: compose length, Stop visible, Send visible,
// turn count. Characterises how (and how fast) Gemini signals that it ACCEPTED a send - the compose box can
// stay full for seconds on a fresh chat while the Stop button already shows the request is in flight.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const q = s => document.querySelector(s);
  const ed = q('chat-window div[contenteditable="true"]');
  if (!ed) return 'no editor';
  ed.focus();
  document.execCommand('selectAll', false, null);
  document.execCommand('insertText', false, window.__composeText || 'Reply with just the word: timeline');
  await sleep(400);
  const send = q('chat-window button[aria-label*="Send"]');
  if (!send) return 'no send button';
  const t0 = performance.now();
  send.click();
  const rows = [];
  let last = '';
  for (let i = 0; i < 80; i++) {
    const e2 = q('chat-window div[contenteditable="true"]');
    const s = `c${(e2?.textContent || '').length} ${q('chat-window button[aria-label*="Stop"]') ? 'STOP' : '-'} ${q('chat-window button[aria-label*="Send"]') ? 'SEND' : '-'} t${document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length}`;
    if (s !== last) { rows.push(`${Math.round(performance.now() - t0)}ms ${s}`); last = s; }
    await sleep(250);
  }
  return rows.join(' | ');
})()
