(() => {
  const cs = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')];
  return JSON.stringify(cs.map(c => {
    const mr = c.querySelector("model-response [id*='model-response-message-content']");
    return (mr ? mr.textContent : '').slice(0, 900);
  }));
})()
