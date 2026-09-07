(() => {
  const t = [...document.querySelectorAll('div,span,p')].map(e => (e.childElementCount===0 ? (e.textContent||'').trim() : '')).filter(s => s && /error|wrong|try again|limit|unable|sorry/i.test(s));
  return JSON.stringify({ convos: document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length,
    url: location.href, matches: [...new Set(t)].slice(0,8) }, null, 1);
})()
