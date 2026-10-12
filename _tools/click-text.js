// Click the first visible button/link whose trimmed text equals window.__clickText (case-insensitive) - used
// to accept a site's terms or cookie prompt in the debug profile so a probe can continue (the user does this
// in real use). Returns what it clicked, or the candidate texts when nothing matched.
(() => {
  const want = (window.__clickText || '').trim().toLowerCase();
  const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const els = [...document.querySelectorAll('button, a, [role="button"]')].filter(vis);
  const hit = els.find(e => (e.textContent || '').trim().toLowerCase() === want);
  if (hit) { hit.click(); return `clicked "${hit.textContent.trim()}"`; }
  return 'no match; buttons: ' + els.map(e => (e.textContent || '').trim()).filter(Boolean).slice(0, 25).join(' | ');
})()
