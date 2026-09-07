// Open the Chess app from the Gemineachy taskbar/start menu and report the board.
// Pierces shadow roots (the desktop renders in an open shadow tree).
(async () => {
  function deepAll(root, acc) {
    acc = acc || [];
    const walk = (node) => {
      for (const el of node.querySelectorAll('*')) {
        acc.push(el);
        if (el.shadowRoot) walk(el.shadowRoot);
      }
    };
    walk(root);
    return acc;
  }
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  const all = deepAll(document);
  // Find a clickable Chess launcher: title/aria-label "Chess" or the ♞ glyph, preferring taskbar/start items.
  const cand = all.filter(e => {
    const t = (e.getAttribute?.('title') || e.getAttribute?.('aria-label') || '').trim();
    // Match the launcher by its title/aria-label only (NOT the ♞ glyph - the board's knight pieces use it).
    return t === 'Chess' || t.includes('Chess');
  });

  // Report squares before (to detect a board appearing)
  const squaresBefore = deepAll(document).filter(e => e.classList?.contains('square')).length;

  let clicked = null;
  // Prefer the smallest element (the actual icon/button, not a big container)
  cand.sort((a,b) => (a.textContent||'').length - (b.textContent||'').length);
  if (cand.length) { cand[0].click(); clicked = (cand[0].getAttribute?.('title') || cand[0].textContent || cand[0].tagName); }

  await sleep(700);

  const sq = deepAll(document).filter(e => e.classList?.contains('square'));
  const pieces = deepAll(document).filter(e => e.classList?.contains('piece'));
  const white = pieces.filter(e => e.classList.contains('white-piece')).length;
  const black = pieces.filter(e => e.classList.contains('black-piece')).length;
  return JSON.stringify({
    clicked, candidates: cand.length, squaresBefore,
    squares: sq.length, pieces: pieces.length, white, black,
    status: (deepAll(document).find(e => e.classList?.contains('status-banner'))?.textContent || '').trim()
  });
})()
