(() => {
  if (window.__netHooked) return 'already';
  window.__netLog = [];
  const of = window.fetch;
  window.fetch = async function (...a) {
    const url = (typeof a[0] === 'string' ? a[0] : (a[0] && a[0].url) || '') + '';
    try { const r = await of.apply(this, a); if (/Generate|assistant|batchexecute/i.test(url)) window.__netLog.push({ url: url.slice(0, 90), status: r.status }); return r; }
    catch (e) { window.__netLog.push({ url: url.slice(0, 90), error: String(e) }); throw e; }
  };
  const os = XMLHttpRequest.prototype.send, oo = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (m, u, ...r) { this.__u = u + ''; return oo.call(this, m, u, ...r); };
  XMLHttpRequest.prototype.send = function (...a) {
    this.addEventListener('loadend', () => { if (/Generate|assistant|batchexecute/i.test(this.__u || '')) window.__netLog.push({ xhr: (this.__u || '').slice(0, 90), status: this.status }); });
    return os.apply(this, a);
  };
  window.__netHooked = true;
  return 'hooked';
})()
