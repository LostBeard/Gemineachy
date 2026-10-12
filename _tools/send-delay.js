// Does Gemini need the editor to process a programmatic text change before Send is clicked?
// textContent + input event, then click Send after window.__delayMs: -1 = in the SAME task (what
// GeminiChatService did), 0 = after a setTimeout(0) yield, N = after N ms. Reports acceptance over 4 s.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const q = s => document.querySelector(s);
  const ed = q('chat-window div[contenteditable="true"]');
  const text = window.__composeText || 'Reply with just the word: delay';
  const delay = window.__delayMs ?? -1;
  ed.focus();
  ed.textContent = text;
  ed.dispatchEvent(new InputEvent('input', { bubbles: true, cancelable: true, inputType: 'insertText', data: text }));
  if (delay >= 0) await sleep(delay);
  const send = q('chat-window button[aria-label*="Send"]');
  const hadSend = !!send;
  send?.click();
  const t0 = performance.now(); let acceptedAt = null;
  while (performance.now() - t0 < 4000) {
    if (!(q('chat-window div[contenteditable="true"]')?.textContent) || q('chat-window button[aria-label*="Stop"]')) { acceptedAt = Math.round(performance.now() - t0); break; }
    await sleep(50);
  }
  return JSON.stringify({ delay, hadSend, acceptedAt, composeLeft: (q('chat-window div[contenteditable="true"]')?.textContent || '').length });
})()
