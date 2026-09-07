(async () => {
  function deepAll(root) { const acc = []; const walk = n => { for (const el of n.querySelectorAll('*')) { acc.push(el); if (el.shadowRoot) walk(el.shadowRoot); } }; walk(root); return acc; }
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  for (let i = 0; i < 85; i++) {
    const hit = deepAll(document).map(e => (e.childElementCount === 0 ? (e.textContent || '').trim() : ''))
      .find(t => /STT self-test (PASS|FAIL|THREW)|STT self-test:/.test(t));
    if (hit) return JSON.stringify({ waitedSeconds: i * 2, result: hit.slice(0, 400) });
    await sleep(2000);
  }
  return JSON.stringify({ timedOut: true });
})()
