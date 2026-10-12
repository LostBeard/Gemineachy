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
    'chat.qwen.ai': {   // user and assistant live in SIBLING .qwen-chat-message-select-turn blocks
      user: '.qwen-chat-message-user', reply: '.qwen-chat-message-assistant',
      compose: 'textarea.message-input-textarea', send: 'button[aria-label="Send"]',
      busy: () => !!q('button[aria-label="Stop"]'),
      userText: u => ((u.querySelector('p.user-message-content') || u).textContent || '').trim(),
      replyOf: u => u.closest('.qwen-chat-message-select-turn')?.nextElementSibling?.querySelector('.qwen-chat-message-assistant') || null,
      body: r => r.querySelector('.response-message-content') || r,
      newChat: () => { location.href = 'https://chat.qwen.ai/'; },
    },
    'chat.z.ai': {   // messages are div[id^=message-] blocks; the Send button is REMOVED while a reply streams
      user: 'div.user-message', reply: '.chat-assistant',
      compose: 'textarea#chat-input', send: 'button#send-message-button',
      busy: () => !q('button#send-message-button'),
      userText: u => (u.textContent || '').trim(),
      // each message has its OWN wrapper div: walk all messages in document order, not siblings
      replyOf: u => { const ms = [...document.querySelectorAll('div[id^="message-"]:not([id$="-start"])')]; for (let j = ms.indexOf(u) + 1; j > 0 && j < ms.length; j++) { if (ms[j].matches('div.user-message')) return null; const a = ms[j].querySelector('.chat-assistant'); if (a) return a; } return null; },
      body: r => r.querySelector('#response-content-container') || r,
      newChat: () => { location.href = 'https://chat.z.ai/'; },
    },
    'www.google.com': {   // AI Mode (udm=50): queries are NOT inside their turns - query i pairs with turn i
      user: 'span[role="heading"][aria-level="2"]', reply: '[data-scope-id="turn"]',
      compose: 'textarea[maxlength="8192"]', send: 'button[data-xid="input-plate-send-button"]',
      busy: () => {
        const qs = document.querySelectorAll('span[role="heading"][aria-level="2"]').length;
        const ts = [...document.querySelectorAll('[data-scope-id="turn"]')];
        const t = ts[ts.length - 1];
        // complete = feedback buttons present (a chat's FIRST turn never gets data-complete)
        return qs > ts.length || (t && !t.querySelector('button[aria-label="Good response"]'));
      },
      userText: u => (u.textContent || '').trim(),
      replyOf: u => { const qs = [...document.querySelectorAll('span[role="heading"][aria-level="2"]')]; return document.querySelectorAll('[data-scope-id="turn"]')[qs.indexOf(u)] || null; },
      body: r => r.querySelector('h3[data-ignore-copy] + div') || r,
      newChat: () => { location.href = 'https://www.google.com/search?udm=50'; },
    },
  };
  return T[location.host] || null;
})();
