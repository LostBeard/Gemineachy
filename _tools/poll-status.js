(async () => {
  function deepAll(root) { const acc = []; const walk = n => { for (const el of n.querySelectorAll('*')) { acc.push(el); if (el.shadowRoot) walk(el.shadowRoot); } }; walk(root); return acc; }
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  for (let i = 0; i < 55; i++) {
    const line = deepAll(document).find(e => e.classList?.contains('reachy-statusline'))
              || deepAll(document).find(e => (e.className || '').toString().includes('status') && (e.textContent || '').includes('heard'));
    const acts = deepAll(document).filter(e => e.classList?.contains('activity-row') || e.classList?.contains('act-result'));
    const txt = acts.map(a => (a.textContent || '').trim()).find(t => /heard|transcription/i.test(t));
    if (txt) return JSON.stringify({ waitedSeconds: i * 2, result: txt });
    await sleep(2000);
  }
  return JSON.stringify({ timedOut: true });
})()
