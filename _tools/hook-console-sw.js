// Console capture that works in ANY scope (page, worker, service worker) - `window` does not exist in a
// service worker, so everything hangs off globalThis.
(() => {
  const g = globalThis;
  if (g.__conHooked) return 'already:' + (g.__conLog || []).length;
  g.__conLog = [];
  for (const k of ['log', 'warn', 'error']) {
    const o = console[k].bind(console);
    console[k] = (...a) => {
      try { g.__conLog.push(k + ': ' + a.map(x => { try { return typeof x === 'string' ? x : JSON.stringify(x); } catch { return String(x); } }).join(' ').slice(0, 1500)); } catch {}
      o(...a);
    };
  }
  g.addEventListener('error', e => g.__conLog.push('onerror: ' + (e.message || '')));
  g.addEventListener('unhandledrejection', e => g.__conLog.push('unhandledrejection: ' + String(e.reason).slice(0, 800)));
  g.__conHooked = true;
  return 'hooked';
})()
