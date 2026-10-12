// Click the existing Send button (compose already holds text) and report acceptance over 6 s, with the
// tab's visibility - for the hidden-tab A/B.
(async () => {
  const q = s => document.querySelector(s);
  const send = q('chat-window button[aria-label*="Send"]');
  if (!send) return 'no send button';
  const vis = document.visibilityState;
  send.click();
  const t0 = Date.now(); let acceptedAt = null;
  while (Date.now() - t0 < 6000) {
    if (!(q('chat-window div[contenteditable="true"]')?.textContent) || q('chat-window button[aria-label*="Stop"]')) { acceptedAt = Date.now() - t0; break; }
    await new Promise(r => setTimeout(r, 100));
  }
  return JSON.stringify({ vis, acceptedAt });
})()
