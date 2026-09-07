(() => {
  const out = { convos: 0, blocks: [] };
  const nodes = document.querySelectorAll('chat-window infinite-scroller > div.conversation-container');
  out.convos = nodes.length;
  const cbs = document.querySelectorAll('code-block, pre');
  out.cbCount = cbs.length;
  for (let i = 0; i < Math.min(cbs.length, 3); i++) {
    const cb = cbs[i];
    out.blocks.push({
      tag: cb.tagName,
      outerHTMLHead: cb.outerHTML.slice(0, 1200),
      textContentHead: JSON.stringify(cb.textContent.slice(0, 200)),
      innerTextHead: JSON.stringify((cb.innerText||'').slice(0, 200))
    });
  }
  return out;
})()
