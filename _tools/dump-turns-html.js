// Dump every conversation turn of the current chat: the user message text and the reply's textContent AND
// innerHTML. The innerHTML is the evidence for scrape bugs - it shows exactly what the renderer did to the
// model's markdown (fences, escapes, stripped tags) before ReadMessageText ran. Site-aware (Gemini, Duck.ai).
(() => {
  const duck = location.host === 'duck.ai';
  if (duck) {
    const users = [...document.querySelectorAll('[data-testid="user-message"]')];
    return JSON.stringify(users.map((u, i) => {
      const r = u.parentElement ? u.parentElement.querySelector('[data-activeresponse]') : null;
      const body = r ? (r.querySelector('[data-dark-theme]') || r) : null;
      const p = u.querySelector('p');
      return { i, user: (p || u).textContent.trim().slice(0, 400), text: body ? body.textContent : null, html: body ? body.innerHTML : null };
    }), null, 1);
  }
  const cs = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')];
  return JSON.stringify(cs.map((c, i) => {
    const uq = c.querySelector('user-query');
    const mr = c.querySelector("model-response [id*='model-response-message-content']");
    return { i, user: uq ? (uq.textContent || '').trim().slice(0, 400) : null, text: mr ? mr.textContent : null, html: mr ? mr.innerHTML : null };
  }), null, 1);
})()
