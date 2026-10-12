// Dump every conversation turn of the current Gemini chat: the user query text and the model response's
// textContent AND innerHTML. The innerHTML is the evidence for scrape bugs - it shows exactly what the
// renderer did to the model's markdown (fences, escapes, stripped tags) before ReadMessageText ran.
(() => {
  const cs = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')];
  return JSON.stringify(cs.map((c, i) => {
    const uq = c.querySelector('user-query');
    const mr = c.querySelector("model-response [id*='model-response-message-content']");
    return {
      i,
      user: uq ? (uq.textContent || '').trim().slice(0, 400) : null,
      text: mr ? mr.textContent : null,
      html: mr ? mr.innerHTML : null,
    };
  }), null, 1);
})()
