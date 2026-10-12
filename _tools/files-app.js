// Open the Files app from the taskbar (if it is not open) and report the mounts and recent agent activity.
// Set window.__filesClick to a button label (e.g. "+ Add OPFS") to click it first.
(async () => {
  function deepAll(root) {
    const acc = [];
    const walk = (node) => { for (const el of node.querySelectorAll('*')) { acc.push(el); if (el.shadowRoot) walk(el.shadowRoot); } };
    walk(root); return acc;
  }
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  if (!deepAll(document).some(e => e.classList?.contains('files-app'))) {
    const launcher = deepAll(document).find(e => /^(Files)$/.test(e.getAttribute?.('title') || e.getAttribute?.('aria-label') || ''));
    if (launcher) { launcher.click(); await sleep(900); }
  }
  const label = window.__filesClick;
  if (label) {
    const b = deepAll(document).find(e => e.tagName === 'BUTTON' && (e.textContent || '').trim() === label);
    if (b) { b.click(); await sleep(1500); }
    window.__filesClick = null;
  }
  const all = deepAll(document);
  const q = (el, sel) => (el.querySelector(sel)?.textContent || '').trim();
  return JSON.stringify({
    open: all.some(e => e.classList?.contains('files-app')),
    mounts: all.filter(e => e.classList?.contains('mount-row')).map(r => `${q(r, '.mount-path')} ${q(r, '.mount-kind')} ${q(r, '.mount-perm')}`),
    status: q(all.find(e => e.classList?.contains('files-app')) || document.createElement('div'), '.files-status'),
    activity: all.filter(e => e.classList?.contains('activity-row')).slice(0, 12).map(r => `${q(r, '.activity-op')} ${q(r, '.activity-path')}`),
  });
})()
