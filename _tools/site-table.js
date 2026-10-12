// Shared per-site table for the _tools probes - the page-side mirror of the C# IChatSite adapters. Prepend it:
//   dotnet run cdp.cs <site> file:site-table.js+wait-idle.js
// Defines window.__gemSite for the current host: user/reply selectors, compose + send, busy() and helpers.
window.__gemSite = (() => {
  const q = s => document.querySelector(s);
  const visible = s => { const e = q(s); if (!e) return false; const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const T = {
    'gemini.google.com': {
      user: 'chat-window user-query', reply: 'chat-window model-response',
      compose: 'chat-window div[contenteditable="true"]', send: 'chat-window button[aria-label*="Send"]',
      busy: () => !!q('chat-window button[aria-label*="Stop"]') || !!q('chat-window model-response [aria-busy="true"]'),
      userText: u => (u.textContent || '').trim(),
      replyOf: u => u.closest('div.conversation-container')?.querySelector('model-response'),
      body: r => r.querySelector("[id*='model-response-message-content']") || r,
      newChat: () => { location.href = 'https://gemini.google.com/app'; },
    },
    'duck.ai': {
      user: '[data-testid="user-message"]', reply: '[data-activeresponse]',
      compose: 'textarea[name="user-prompt"]', send: 'form[data-chat-footer] button[type="submit"]',
      busy: () => visible('button[aria-label="Stop generating"]') || !!q('[data-activeresponse="false"]'),
      userText: u => ((u.querySelector('p') || u).textContent || '').trim(),
      replyOf: u => u.parentElement?.querySelector('[data-activeresponse]'),
      body: r => r.querySelector('[data-dark-theme]') || r,
      newChat: () => [...document.querySelectorAll('button')].find(b => /^new chat/i.test((b.textContent || '').trim()))?.click(),
    },
    'chatgpt.com': {
      user: 'li[data-message-role="user"]', reply: 'li[data-message-role="assistant"]',
      compose: 'form textarea[name="prompt"]', send: 'form button[aria-label="Send message"]',
      busy: () => !!q('button[aria-label="Stop generating"]') || !!q('li[data-message-role="assistant"][data-message-streaming]'),
      userText: u => ((u.querySelector('p[data-user-message-copy]') || u).textContent || '').trim(),
      replyOf: u => { const n = u.nextElementSibling; return n && n.getAttribute('data-message-role') === 'assistant' ? n : null; },
      body: r => r.querySelector('[data-assistant-markdown]') || r,
      // Signed out, the "New chat" link opens a login modal; a page load is the only way to a fresh chat.
      newChat: () => { location.href = 'https://chatgpt.com/'; },
    },
  };
  return T[location.host] || null;
})();
