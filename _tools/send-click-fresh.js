// Re-query the send button immediately before each click - Angular swaps the element and a captured
// reference goes stale (see memory: gemini compose send button behavior).
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const ed = () => document.querySelector('div.ql-editor[contenteditable="true"]');
  const send = () => [...document.querySelectorAll('button')].find(b => !b.disabled && /send/i.test(b.getAttribute('aria-label') || ''));
  const before = ed()?.textContent.trim() || '';
  for (let i = 0; i < 4; i++) {
    const b = send();
    if (!b) return JSON.stringify({ attempt: i, error: 'no enabled send button' });
    b.click();
    await sleep(1200);
    const now = ed()?.textContent.trim() || '';
    if (now.length === 0 || now !== before) return JSON.stringify({ attempt: i, sent: true, editorAfter: now.slice(0, 60) });
  }
  return JSON.stringify({ sent: false, editorStill: (ed()?.textContent || '').slice(0, 60) });
})()
