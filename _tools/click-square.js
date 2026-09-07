// Click the Nth .square (deep shadow) in the chess board and report selection/targets.
// Pass the square index via a global set by cdp (we hardcode below per invocation).
(async () => {
  function deepAll(root) {
    const acc = [];
    const walk = (node) => { for (const el of node.querySelectorAll('*')) { acc.push(el); if (el.shadowRoot) walk(el.shadowRoot); } };
    walk(root); return acc;
  }
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  const idx = window.__sqIdx ?? 0;
  const squares = deepAll(document).filter(e => e.classList?.contains('square'));
  if (squares.length < 64) return JSON.stringify({ error: 'board not found', squares: squares.length });
  squares[idx].click();
  await sleep(250);
  const sq2 = deepAll(document).filter(e => e.classList?.contains('square'));
  return JSON.stringify({
    clickedIndex: idx,
    selected: sq2.filter(e => e.classList.contains('selected')).length,
    targets: sq2.filter(e => e.classList.contains('target')).length,
    status: (deepAll(document).find(e => e.classList?.contains('status-banner'))?.textContent || '').trim()
  });
})()
