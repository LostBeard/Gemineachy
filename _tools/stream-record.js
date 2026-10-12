// Site-agnostic adapter study: type window.__siteMsg into the page's main compose box, send it, and RECORD what
// changes until the page has been quiet for 8 s (max 120 s):
//   - every attribute mutation (element signature + attribute + new value), deduplicated, with first-seen time
//   - the set of visible button labels (aria-label / data-testid), as additions/removals over time
// From that log you can read the site's Send / Stop controls and the attribute that flips when a reply finishes.
// Send: window.__sendSel if set, else a visible button whose label matches /send|submit/i, else Enter.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const skip = el => /search|phone|email|password/i.test((el.getAttribute('placeholder') || '') + ' ' + (el.getAttribute('type') || ''));
  const box = [...document.querySelectorAll('textarea, [contenteditable="true"]')].filter(e => vis(e) && !skip(e))
    .sort((a, b) => b.getBoundingClientRect().width - a.getBoundingClientRect().width)[0];
  if (!box) return 'no compose box';
  const sig = e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '')
    + [...e.attributes].filter(a => /^(data-testid|data-message-author-role|role|aria-label)$/.test(a.name)).map(a => `[${a.name}=${a.value.slice(0, 30)}]`).join('');
  const labels = () => new Set([...document.querySelectorAll('button')].filter(vis)
    .map(b => (b.getAttribute('aria-label') || b.getAttribute('data-testid') || '').trim()).filter(Boolean));
  const t0 = performance.now(); const log = []; const seen = new Set(); let lastEvent = performance.now();
  const ms = () => Math.round(performance.now() - t0);
  const mo = new MutationObserver(recs => {
    for (const r of recs) {
      if (r.type !== 'attributes' || r.attributeName === 'style') continue;
      const el = r.target; let v = el.getAttribute(r.attributeName);
      if (r.attributeName === 'class') v = (v || '').split(/\s+/).filter(c => /stream|load|busy|pending|typing|anim|generat|done|complete/i.test(c)).join(' ');
      if (r.attributeName === 'class' && !v) continue;
      const key = `${sig(el)} ${r.attributeName}=${(v ?? 'null').slice(0, 60)}`;
      if (seen.has(key)) continue;
      seen.add(key); log.push(`${ms()}ms ATTR ${key}`); lastEvent = performance.now();
    }
  });
  mo.observe(document.body, { attributes: true, subtree: true });
  let prev = labels();
  const text = window.__siteMsg || 'hello';
  box.focus();
  if (box.tagName === 'TEXTAREA') {
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(box, text);
    box.dispatchEvent(new Event('input', { bubbles: true }));
  } else {
    document.execCommand('selectAll', false, null);
    document.execCommand('insertText', false, text);
  }
  await sleep(500);
  const sendSel = window.__sendSel;
  const send = sendSel ? document.querySelector(sendSel)
    : [...document.querySelectorAll('button')].filter(vis).find(b => /send|submit/i.test((b.getAttribute('aria-label') || '') + ' ' + (b.getAttribute('data-testid') || '')) && !b.disabled);
  log.push(`${ms()}ms SEND via ${send ? sig(send) : 'Enter'}`);
  if (send) send.click();
  else box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true }));
  while (performance.now() - t0 < 120000 && performance.now() - lastEvent < 8000) {
    await sleep(250);
    const now = labels();
    const add = [...now].filter(x => !prev.has(x)), rem = [...prev].filter(x => !now.has(x));
    if (add.length || rem.length) { log.push(`${ms()}ms BUTTONS +[${add.join(' | ')}] -[${rem.join(' | ')}]`); lastEvent = performance.now(); }
    prev = now;
  }
  mo.disconnect();
  return log.slice(0, 120).join('\n');
})()
