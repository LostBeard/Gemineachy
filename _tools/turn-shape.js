// What does a FINISHED Gemini turn have that a streaming one does not? Lists the custom elements and
// attribute markers inside the last conversation-container's model-response (tag names, aria-busy,
// action buttons). Run once while streaming and once after, and diff.
(() => {
  const c = [...document.querySelectorAll('chat-window infinite-scroller > div.conversation-container')].pop();
  if (!c) return 'no turn';
  const mr = c.querySelector('model-response');
  const tags = new Set();
  for (const e of mr.querySelectorAll('*')) if (e.tagName.includes('-')) tags.add(e.tagName.toLowerCase());
  const busy = [...mr.querySelectorAll('[aria-busy]')].map(e => e.tagName.toLowerCase() + ':' + e.getAttribute('aria-busy'));
  const btns = [...mr.querySelectorAll('button')].map(b => b.getAttribute('aria-label') || b.getAttribute('mattooltip') || '').filter(Boolean);
  return JSON.stringify({ tags: [...tags].sort(), busy, btns, stop: !!document.querySelector('chat-window button[aria-label*="Stop"]'),
    textLen: (mr.querySelector("[id*='model-response-message-content']")?.textContent || '').length,
    mrAttrs: [...mr.attributes].map(a => a.name + '=' + a.value.slice(0, 40)) });
})()
