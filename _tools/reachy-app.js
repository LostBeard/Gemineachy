// Open the Reachy app from the taskbar and report robot cards. Optionally click a named control button.
// Set window.__reachyClick to a button label (e.g. "Wake", "Status") to click it in the first robot card.
(async () => {
  function deepAll(root) {
    const acc = [];
    const walk = (node) => { for (const el of node.querySelectorAll('*')) { acc.push(el); if (el.shadowRoot) walk(el.shadowRoot); } };
    walk(root); return acc;
  }
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  // If the app isn't open yet, click its taskbar launcher.
  let cards = deepAll(document).filter(e => e.classList?.contains('robot-card'));
  if (cards.length === 0) {
    const launcher = deepAll(document).find(e => (e.getAttribute?.('title') || e.getAttribute?.('aria-label') || '') === 'Reachy');
    if (launcher) { launcher.click(); await sleep(900); }
  }

  const clickLabel = window.__reachyClick;
  if (clickLabel) {
    const btns = deepAll(document).filter(e => e.tagName === 'BUTTON' && (e.textContent || '').trim() === clickLabel);
    if (btns.length) { btns[0].click(); await sleep(2500); } // allow relay round-trip
  }

  cards = deepAll(document).filter(e => e.classList?.contains('robot-card'));
  const report = cards.map(c => {
    const q = (sel) => (c.querySelector(sel)?.textContent || '').trim();
    const dot = c.querySelector('.robot-dot');
    return { name: q('.robot-name'), origin: q('.robot-origin'), status: q('.robot-status'),
             online: dot ? dot.classList.contains('on') : null };
  });
  const statusline = (deepAll(document).find(e => e.classList?.contains('reachy-statusline'))?.textContent || '').trim();
  const activity = deepAll(document).filter(e => e.classList?.contains('activity-row')).map(row => {
    const q = (sel) => (row.querySelector(sel)?.textContent || '').trim();
    return { tool: q('.activity-tool'), robot: q('.activity-robot'), result: q('.activity-result'), time: q('.activity-time') };
  });
  return JSON.stringify({ cards: report, activity, statusline, clicked: clickLabel || null });
})()
