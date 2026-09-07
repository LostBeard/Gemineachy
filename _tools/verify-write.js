(async () => {
  // 1. What did Gemini actually emit? (proves the PAYLOAD channel was used)
  const cs = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')];
  const turns = cs.map(c => {
    const mr = c.querySelector("model-response [id*='model-response-message-content']");
    return (mr ? mr.textContent : '');
  });
  const payloadTurn = turns.find(t => t.includes('«PAYLOAD:')) || null;
  // 2. What actually landed on disk?
  const root = await navigator.storage.getDirectory();
  let content = null, err = null;
  try { content = await (await root.getFileHandle('ChromeStrip.cs')).getFile().then(f => f.text()); }
  catch (e) { err = String(e); }
  return JSON.stringify({
    usedPayloadChannel: !!payloadTurn,
    emittedHeadHasLangLabel: payloadTurn ? /«PAYLOAD:content»\s*C#/.test(payloadTurn) : null,
    scrapeTest: document.documentElement.getAttribute('data-gem-scrapetest'),
    err, file: content
  }, null, 1);
})()
