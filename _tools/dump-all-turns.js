(() => {
  const cs = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')];
  return JSON.stringify(cs.map((c,i) => {
    const uq = c.querySelector('user-query');
    const mr = c.querySelector("model-response [id*='model-response-message-content']");
    return { i, q: (uq?uq.textContent:'').slice(0,140), r: (mr?mr.textContent:'').slice(0,500) };
  }), null, 1);
})()
