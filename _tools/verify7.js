(async () => {
  const cs = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')];
  const turns = cs.map(c => { const mr = c.querySelector("model-response [id*='model-response-message-content']"); return mr ? mr.textContent : ''; });
  const pt = turns.find(t => t.includes('«PAYLOAD:')) || null;
  const root = await navigator.storage.getDirectory();
  let content = null, err = null;
  try { content = await (await root.getFileHandle('AfterWwBump.cs')).getFile().then(f => f.text()); } catch (e) { err = String(e); }
  return JSON.stringify({ rawHadLangLabel: pt ? /«PAYLOAD:content»[ \t]*(C#|csharp)/i.test(pt) : null, scrapeTest: document.documentElement.getAttribute('data-gem-scrapetest'), err, file: content }, null, 1);
})()
