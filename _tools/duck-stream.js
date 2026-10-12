// Duck.ai: type a prompt into the React-controlled textarea, submit, and sample every 300 ms: compose length,
// submit-button label/disabled, the last assistant turn's data-* attributes and text length, assistant count.
// Prints state changes only. Finds Duck.ai's accept + still-streaming + done signals.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const ta = document.querySelector('textarea[name="user-prompt"], textarea');
  const text = window.__composeText || 'Write about 200 words on why the sky is blue.';
  ta.focus();
  Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(ta, text);
  ta.dispatchEvent(new Event('input', { bubbles: true }));
  await sleep(0);
  const before = document.querySelectorAll('[data-activeresponse]').length;
  const btn = () => document.querySelector('button[type="submit"]');
  btn().click();
  const rows = []; let last = ''; const t0 = performance.now();
  for (let i = 0; i < 200; i++) {
    const asses = document.querySelectorAll('[data-activeresponse]');
    const a = asses.length > before ? asses[asses.length - 1] : null;
    const b = btn();
    const attrs = a ? [...a.attributes].filter(x => x.name.startsWith('data-') || x.name.startsWith('aria-')).map(x => `${x.name}=${x.value}`).join(',') : '';
    const busyIn = a ? a.querySelectorAll('[aria-busy="true"]').length : 0;
    const len = a ? (a.textContent || '').length : 0;
    const labels = [...document.querySelectorAll('form button, button[aria-label*="top"]')].map(x => x.getAttribute('aria-label') || x.textContent.trim().slice(0,12)).join('/');
    const s = `labels=${labels} ta=${(document.querySelector('textarea')?.value || '').length} btn=${b ? (b.getAttribute('aria-label') + (b.disabled ? '(dis)' : '')) : 'none'} ass=${asses.length} [${attrs}] busyIn=${busyIn} len=${len > 0 ? (len < 50 ? len : '50+') : 0}`;
    if (s !== last) { rows.push(`${Math.round(performance.now() - t0)}ms ${s}`); last = s; }
    await sleep(300);
    if (i > 20 && a && !/stop/i.test(b?.getAttribute('aria-label') || '') && rows.length && performance.now() - t0 > 4000 && s === last && i % 10 === 0) {
      // settled long enough
      const finalLen = (a.textContent || '').length; await sleep(1500);
      if ((a.textContent || '').length === finalLen) { rows.push(`settled len=${finalLen}`); break; }
    }
  }
  return rows.join('\n');
})()
