(() => {
  const c = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')].pop();
  const mr = c.querySelector("model-response [id*='model-response-message-content']");
  const cb = mr.querySelector('code-block');
  return JSON.stringify({
    textContent: mr.textContent,
    innerText: mr.innerText,
    preText: cb ? (cb.querySelector('pre')||{}).textContent : null,
    htmlHead: mr.innerHTML.slice(0, 700)
  }, null, 1);
})()
