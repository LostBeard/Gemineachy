// Start a page-side timeline recorder (window.__tl): every 250 ms, log path / user-message count / Stop /
// Send / compose length / <html> classes - changes only, for up to 5 minutes. Run BEFORE sending; read it
// back with `JSON.stringify(window.__tl)` (or timeline-dump via cdp). Explains turns that appear and vanish.
(() => {
  if (window.__tlTimer) clearInterval(window.__tlTimer);
  window.__tl = [];
  const t0 = performance.now();
  let last = '';
  const q = s => document.querySelector(s);
  window.__tlTimer = setInterval(() => {
    const compose = q('chat-window div[contenteditable="true"]') || q('textarea[name="user-prompt"]');
    const len = compose ? (compose.value ?? compose.textContent ?? '').length : -1;
    const s = `${location.pathname} uq=${document.querySelectorAll('chat-window user-query, [data-testid="user-message"]').length}`
      + ` stop=${!!q('chat-window button[aria-label*="Stop"]')} send=${!!q('chat-window button[aria-label*="Send"]')}`
      + ` compose=${len} cls=${document.documentElement.className}`;
    if (s !== last) { window.__tl.push(`${Math.round(performance.now() - t0)}ms ${s}`); last = s; }
    if (performance.now() - t0 > 300000) clearInterval(window.__tlTimer);
  }, 250);
  return 'recording';
})()
