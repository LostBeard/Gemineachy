// Google AI Mode: send window.__siteMsg via the follow-up textarea and sample every 200 ms - query count, turn
// count, the last turn's data-complete, whether its feedback buttons exist, any Stop/Cancel control, the last
// turn's text length, and the Send button. Prints changes only. Finds AI Mode's completion signal.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const ta = [...document.querySelectorAll('textarea')].find(t => t.getBoundingClientRect().width > 0);
  ta.focus();
  Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(ta, window.__siteMsg || 'Write about 200 words on coral reefs.');
  ta.dispatchEvent(new Event('input', { bubbles: true }));
  await sleep(400);
  document.querySelector('button[aria-label="Send"]').click();
  const rows = []; let last = ''; const t0 = performance.now();
  for (let i = 0; i < 300; i++) {
    const turns = document.querySelectorAll('[data-scope-id="turn"]');
    const t = turns[turns.length - 1];
    const stop = [...document.querySelectorAll('button,[role=button]')].filter(b => b.getBoundingClientRect().width > 0)
      .map(b => b.getAttribute('aria-label') || '').filter(l => /stop|cancel/i.test(l)).join('/');
    const len = t ? (t.textContent || '').length : 0;
    const s = `q=${document.querySelectorAll('span[role=heading][aria-level="2"]').length} turns=${turns.length} complete=${t ? t.getAttribute('data-complete') : '-'} fb=${t ? !!t.querySelector('button[aria-label="Good response"]') : '-'} stop=[${stop}] send=${!!document.querySelector('button[aria-label="Send"]')} len=${len < 100 ? len : '100+'}`;
    if (s !== last) { rows.push(`${Math.round(performance.now() - t0)}ms ${s}`); last = s; }
    if (i > 30 && t && t.getAttribute('data-complete') === 'true' && rows.length > 2 && performance.now() - t0 > 6000) break;
    await sleep(200);
  }
  return rows.join('\n');
})()
