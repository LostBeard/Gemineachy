(async () => {
  const url = 'ws://reachy-mini.local:8443';
  return await new Promise(res => {
    let done = false;
    const t = setTimeout(() => { if (!done) { done = true; res(JSON.stringify({ url, result: 'timeout(8s)' })); } }, 8000);
    let ws;
    try { ws = new WebSocket(url); }
    catch (e) { clearTimeout(t); return res(JSON.stringify({ url, result: 'ctor threw', error: String(e) })); }
    ws.onopen = () => { if (done) return; };
    ws.onmessage = ev => {
      if (done) return; done = true; clearTimeout(t);
      res(JSON.stringify({ url, result: 'OPEN + first message', firstMessage: String(ev.data).slice(0, 200) }));
      try { ws.close(); } catch {}
    };
    ws.onerror = () => { if (done) return; done = true; clearTimeout(t); res(JSON.stringify({ url, result: 'error event', readyState: ws.readyState })); };
    ws.onclose = ev => { if (done) return; done = true; clearTimeout(t); res(JSON.stringify({ url, result: 'closed', code: ev.code, reason: ev.reason })); };
  });
})()
