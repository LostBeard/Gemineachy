// Completion study for any site in site-table.js (prepend it: file:site-table.js+site-busy.js). Sends
// window.__siteMsg (or a short default) and SAMPLES candidate busy/done signals every 200 ms until 30 s (window.__quietMs) after the
// last change: Send present/disabled, any visible Stop button, the last reply's footer button labels and classes.
(async () => {
  const S = window.__gemSite; if (!S) return 'no site-table entry for ' + location.host;
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const q = s => document.querySelector(s);
  const t0 = performance.now(); const ms = () => Math.round(performance.now() - t0);
  const lastReply = () => { const a = document.querySelectorAll(S.reply); return a[a.length - 1]; };
  const sample = () => {
    const b = q(S.send), r = lastReply();
    const stop = [...document.querySelectorAll('button')].filter(x => /stop/i.test(x.getAttribute('aria-label') || '') && x.getBoundingClientRect().width > 0).length;
    const btns = r ? [...r.querySelectorAll('button')].map(x => x.getAttribute('aria-label') || [...x.classList].find(c => /copy|regen|edit/i.test(c)) || 'btn').join('|') : '-';
    return JSON.stringify({ replies: document.querySelectorAll(S.reply).length, send: b ? (b.disabled ? 'disabled' : 'enabled') : 'absent', stop,
      replyCls: r ? [...r.classList].filter(c => /await|stream|load|done|complete|generat/i.test(c)).join(' ') : '-', btns, textLen: r ? (r.textContent || '').length : -1 });
  };
  const log = []; let prev = ''; let lastChange = performance.now();
  const ta = q(S.compose);
  ta.focus();
  Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(ta, window.__siteMsg || 'Reply with a two-sentence fact about owls.');
  ta.dispatchEvent(new Event('input', { bubbles: true }));
  await sleep(400);
  log.push(`${ms()} BEFORE ${sample()}`);
  q(S.send).click();
  while (performance.now() - t0 < 150000) {
    await sleep(200);
    const s = sample();
    const key = s.replace(/"textLen":-?\d+/, '');   // text grows every frame while streaming: log it only alongside a real change
    if (key !== prev) { log.push(`${ms()} ${s}`); prev = key; lastChange = performance.now(); }
    else if (performance.now() - lastChange > (window.__quietMs || 30000)) break;
  }
  return log.join('\n');
})()
