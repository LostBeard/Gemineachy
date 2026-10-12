// Dump every conversation turn of the current chat: the user message text and the reply's textContent AND
// innerHTML. The innerHTML is the evidence for scrape bugs - it shows exactly what the renderer did to the
// model's markdown (fences, escapes, stripped tags) before ReadMessageText ran.
// Needs site-table.js prepended:  cdp.cs <site> file:site-table.js+dump-turns-html.js
(() => {
  const S = window.__gemSite;
  if (!S) return JSON.stringify({ error: 'no site-table entry for ' + location.host });
  return JSON.stringify([...document.querySelectorAll(S.user)].map((u, i) => {
    const r = S.replyOf(u);
    const body = r ? S.body(r) : null;
    return { i, user: S.userText(u).slice(0, 400), text: body ? body.textContent : null, html: body ? body.innerHTML : null };
  }), null, 1);
})()
