// Describe Gemini's compose box and send button as they exist right now (structure drifts; probe, don't assume).
(() => {
  const eds = [...document.querySelectorAll('[contenteditable="true"]')];
  const btns = [...document.querySelectorAll('button')].filter(b => /send|submit/i.test((b.getAttribute('aria-label') || '') + ' ' + (b.className || '')));
  return JSON.stringify({
    editors: eds.map(e => ({ tag: e.tagName, cls: e.className, inChatWindow: !!e.closest('chat-window'), html: e.innerHTML.slice(0, 300), parentTag: e.parentElement?.tagName })),
    sendButtons: btns.map(b => ({ label: b.getAttribute('aria-label'), disabled: b.disabled, ariaDisabled: b.getAttribute('aria-disabled'), cls: b.className.slice(0, 120), inChatWindow: !!b.closest('chat-window') })),
    chatWindow: !!document.querySelector('chat-window'),
  }, null, 1);
})()
