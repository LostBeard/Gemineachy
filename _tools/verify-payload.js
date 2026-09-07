(async () => {
  const cs = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')];
  const turns = cs.map(c => { const mr = c.querySelector("model-response [id*='model-response-message-content']"); return mr ? mr.textContent : ''; });
  const pt = turns.find(t => t.includes('«PAYLOAD:')) || null;
  const root = await navigator.storage.getDirectory();
  let content = null, err = null;
  try { content = await (await root.getFileHandle('PayloadProof.cs')).getFile().then(f => f.text()); } catch (e) { err = String(e); }
  return JSON.stringify({
    rawEmitted: pt ? pt.slice(0, 400) : null,
    labelPresentInRawDom: pt ? /«PAYLOAD:content»[ \t]*(C#|csharp|Csharp)/i.test(pt) : null,
    err, file: content
  }, null, 1);
})()
